/*
 Tiene los endpoints para subir archivos al minIO
 Notas generales:
    ->El ID de usuario está hardcodeado pero debe sacarse de la solicitud HTTP
    ->Se debe de integrar la parte de la base de datos para guaardar los registros
    ->Hay un problema con el forwarding de puertos para que otras computadoras
      accedan al recurso de MinIO, en caso de que el servicio principal (el que usa local host)
      se caiga. En w11 se puede usar "Mirror" del WSL
    ->30/04/2026: Se empezó a agregar las cosas del Ulises
 */
using ExtractorPdf.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Bson;
using MongoDB.Driver;
using PDD_Archivos.Configuracion;
using PDD_Archivos.Models;
using PDD_Archivos.Servicios;
using System.Security.Claims;

namespace PDD_Archivos.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class files : ControllerBase
    {
        private readonly IMinioClient _minioClient;
        private readonly MongoContext _context;
        private readonly string _bucketName = "pdfs";

        void Registrar(string mensaje)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(mensaje);
            Console.ResetColor();
        }
        //el constructor
        public files (IMinioClient minioClient, MongoContext context)
        {
            this._minioClient = minioClient;
            _context = context;
        }
        //ver que onda con los dos constructores

       

        //ahora si las peticiones del moy

        //subir archivos
        /*
         *Verificar si es Académico
         *Proceso de Extracción
         *Subida en la BD
         *Encolado en RabbitMQ
         *Espera a que clasifique (Monitorear MongoDB)
         *Guardar en MinIO
         *Enviar Mensaje
         */
        [HttpPost("upload")]
        public async Task<IActionResult> UploadFile(IFormFile file, [FromQuery] string folderId = "root")
        {
            //variables necesarias desde el inicio
            var fileId = Guid.NewGuid().ToString();
            var userId = "1";//este lo debe sacar de la solicitud
            var configuracion = new ConfiguracionRabbitMq
            {
                Servidores = ["localhost"],
                Usuario = "guest",
                Contrasena = "guest",
                UsarColaQuorum = false,
            };
            //guardarlo

            //primero comprobar si el texto es académico
            var validador = new ValidadorAcademico();
            var resultadoValidacion = validador.Validar(file.OpenReadStream());
            if (!resultadoValidacion.EsValido)
            {
                return BadRequest(new {
                    nombre = file.Name,
                    Error = resultadoValidacion.Razon
                });
            }
            //Ahora se realiza la extracción
            
            var servicioExtraccion = new ServicioExtraccionPdf();
            var evento = servicioExtraccion.Extraer(file.OpenReadStream(), fileId,file.Name);

            /*
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" Extraccion completada");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  Estado          : {evento.EstadoExtraccion}");
            Console.WriteLine($"  Idioma          : {evento.Idioma}");
            Console.WriteLine($"  Palabras clave  : {evento.PalabrasClave.Count} encontradas");
            Console.WriteLine($"  Resumen         : {(string.IsNullOrEmpty(evento.Resumen) ? "no extraído" : evento.Resumen.Length + " caracteres")}");
            Console.ResetColor();
            */
            //se encola y se genera el registro en la BD
            var nuevoArchivo = new MetadataArchivo
            {
                id = fileId,
                usuarioId = int.Parse(userId),
                nombreOriginal = file.FileName,
                fechaSubida = DateTime.UtcNow,
                estado = Estado.Encolado
            };

            try
            {
                await _context.Archivos.InsertOneAsync(nuevoArchivo);
            }
            catch (MongoWriteException ex)
            {
                Console.WriteLine($"Error al escribir en Mongo: {ex.Message}");
            }

            using var servicioMensajeria = new ServicioMensajeria(configuracion, Registrar);
            try
            {
                //crear registro en la BD
                servicioMensajeria.Publicar(evento);
        
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("mantenimiento"))
            {
                // Circuit Breaker abierto: RabbitMQ no disponible
                //cronometro.Stop();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" Sistema en mantenimiento");
                Console.WriteLine($"  {ex.Message}");
                //Console.WriteLine($"  El json fue guardado en: {rutaSalida}");
                Console.ResetColor();
            }
            //esperar a que el archivo sea modificado en MongoDB
            //
            // 1. Cambiamos la promesa para que devuelva el objeto MetadataArchivo
            var tcs = new TaskCompletionSource<MetadataArchivo>();

            // 2. Mantenemos el pipeline filtrando por el fileId específico
            var pipeline = new EmptyPipelineDefinition<ChangeStreamDocument<MetadataArchivo>>()
            .Match(new BsonDocument {
                { "operationType", "update" },
                { "documentKey._id", fileId },
                { "updateDescription.updatedFields.estado", new BsonDocument("$exists", true) }
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // Un minuto de margen

            _ = Task.Run(async () => {
                try
                {
                    using var cursor = await _context.Archivos.WatchAsync(pipeline,
                        new ChangeStreamOptions { FullDocument = ChangeStreamFullDocumentOption.UpdateLookup },
                        cts.Token);

                    while (await cursor.MoveNextAsync(cts.Token))
                    {
                        foreach (var change in cursor.Current)
                        {
                            // Si el estado es el que esperamos, pasamos todo el documento al TCS
                            if (change.FullDocument.estado == Estado.Procesado)
                            {
                                tcs.TrySetResult(change.FullDocument);
                                return;
                            }
                            // Si el proceso falló, también cerramos para no esperar en vano
                            else if (change.FullDocument.estado == Estado.Error)
                            {
                                tcs.TrySetException(new Exception("El procesamiento falló en el microservicio."));
                                return;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetException(new TimeoutException("Tiempo de espera agotado para el procesamiento."));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            // 3. Esperamos y capturamos el objeto completo
            MetadataArchivo archivoProcesado;
            try
            {
                archivoProcesado = await tcs.Task;
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = ex.Message });
            }

            // 4. AHORA ya puedes usar archivoProcesado para construir la key
            // Usamos el nombreArea de la clasificación recibida
            //var area = archivoProcesado.clasificacion?.nombreArea ?? "SinArea";
            //var key = $"usuarios/{userId}/{folderId}/{area}/{fileId}";

            // ... continuar con la subida a MinIO
            //si todo sale bien ya se guarda en MinIO (construir la key)


            var key = $"usuarios/{userId}/{folderId}/{fileId}";

            // Usamos el stream directamente del IFormFile para no duplicar memoria
            //
            //NOTA: aqui va la espera de la BD
            
            using var stream = file.OpenReadStream();

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key)
                .WithStreamData(stream)
                .WithObjectSize(file.Length)
                .WithContentType(file.ContentType)
                // Metadatos personalizados
                .WithHeaders(new Dictionary<string, string> {
                    { "x-amz-meta-original-name", file.FileName },
                    { "x-amz-meta-user-id", userId }
                });

            await _minioClient.PutObjectAsync(putObjectArgs);
            //prueba para meter a la base de datos
            
            
            return Ok(new
            {
                FileId = fileId,
                nombre = file.FileName,
                estado = "PROCESANDO",//Procesado
                mensaje = "Archivo recibido correctamente",
                S3Key = key
            });
        }

        //descargar archivos (url con tiempo límite)
        [HttpGet("download/{fileId}")]
        public async Task<IActionResult> GetDownloadUrl(string fileId, [FromQuery] string folderId = "root")
        {
            var userId = "1";
            var key = $"usuarios/{userId}/{folderId}/{fileId}";

            var args = new PresignedGetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key)
                .WithExpiry(60 * 15); // 15 minutos en segundos

            string url = await _minioClient.PresignedGetObjectAsync(args);
            return Ok(new { DownloadUrl = url });
        }

        [HttpDelete("{fileId}")]
        public async Task<IActionResult> DeleteFile(string fileId, [FromQuery] string folderId = "root")
        {
            var userId = "1";
            var key = $"usuarios/{userId}/{folderId}/{fileId}";

            var args = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key);

            await _minioClient.RemoveObjectAsync(args);

            return NoContent();
        }

        [HttpPost("folder")]
        public async Task<IActionResult> CreateFolder([FromQuery] string folderName, [FromQuery] string parentFolderId = "root")
        {
            var userId = "1";
            var safeFolderName = Uri.EscapeDataString(folderName.Trim());
            var key = $"usuarios/{userId}/{parentFolderId}/{safeFolderName}/";

            // En MinIO/S3, una carpeta es un objeto de 0 bytes que termina en "/"
            using var emptyStream = new MemoryStream(Array.Empty<byte>());

            var args = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key)
                .WithStreamData(emptyStream)
                .WithObjectSize(0);

            await _minioClient.PutObjectAsync(args);

            return Ok(new
            {
                Message = "Carpeta creada exitosamente",
                FolderName = folderName,
                S3Key = key
            });
        }

        [HttpGet]
        public async Task<IActionResult> Listar()
        {
            // Usamos la colección directamente
            var lista = await _context.Archivos.Find(_ => true).ToListAsync();
            return Ok(lista);
        }
    }
}
