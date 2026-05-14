/*
 Tiene los endpoints para subir archivos al minIO
 Notas generales:
    ->El ID de usuario está hardcodeado pero debe sacarse de la solicitud HTTP
    ->Se debe de integrar la parte de la base de datos para guaardar los registros
    ->Hay un problema con el forwarding de puertos para que otras computadoras
      accedan al recurso de MinIO, en caso de que el servicio principal (el que usa local host)
      se caiga. En w11 se puede usar "Mirror" del WSL
    ->30/04/2026: Se empezó a agregar las cosas del Ulises
    _>5/05/2026 : Se modificaron las peticiones al backend
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
using static System.Net.WebRequestMethods;

namespace PDD_Archivos.Controllers
{
    //Se coloca [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class files : ControllerBase
    {
        private readonly IMinioClient _minioClient;
        private readonly MongoContext _context;
        private readonly string _bucketName = "pdfs";
        private readonly ConfiguracionRabbitMq config;

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
            this._context = context;
            this.config = new ConfiguracionRabbitMq
            {
                Servidores = ["localhost"],
                Usuario = "guest",
                Contrasena = "guest",
                UsarColaQuorum = false,
            };
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
            var userId = "1";//este lo debe sacar de la solicitud del MOY

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
            //guardarlo ya en MinIO
            using var stream = file.OpenReadStream();
            var key = $"usuarios/{userId}/{folderId}/{fileId}";
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
            //Ahora se realiza la extracción
            var servicioExtraccion = new ServicioExtraccionPdf();
            var evento = servicioExtraccion.Extraer(file.OpenReadStream(), fileId,file.Name);
            evento.UsuarioId = userId;
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

            using var servicioMensajeria = new ServicioMensajeria(config, Registrar);
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
            var userId = "1"; //se extrae del JWT
            var key = $"usuarios/{userId}/{folderId}/{fileId}";

            var args = new PresignedGetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key)
                .WithExpiry(60 * 15); // 15 minutos en segundos

            string url = await _minioClient.PresignedGetObjectAsync(args);
            return Ok(new { DownloadUrl = url });
        }

        

        //teoricamente ya no se usarían
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

        
        [HttpGet("listar/")]

        public async Task<IActionResult> Listar()
        {
            var userId = 1; //sacarlo del JWT
            var lista = await _context.Archivos
                .Find(a => a.usuarioId == userId)
                .ToListAsync();

            return Ok(lista);
        }

        [HttpPost("inicializar-catalogo")]
        public async Task<IActionResult> Inicializar([FromBody] CatalogoAreas nuevoCatalogo)
        {
            // Verificamos si ya existe para no duplicar
            var existe = await _context.Catalogos.Find(_ => true).AnyAsync();
            if (existe) return BadRequest("El catálogo ya existe.");

            await _context.Catalogos.InsertOneAsync(nuevoCatalogo);
            return Ok("Catálogo guardado exitosamente.");
        }



        //los endpoints nuevos del MOY
        //RF-01
        [HttpGet("themes")]
        public async Task<ActionResult<CatalogoAreas>> GetCatalogo()
        {
            // Buscamos el documento (asumiendo que solo hay uno)
            var catalogo = await _context.Catalogos.Find(_ => true).FirstOrDefaultAsync();

            if (catalogo == null) return NotFound("El catálogo aún no ha sido cargado.");

            return Ok(catalogo);
        }


        //RF-02
        [HttpPost("themes/me")]
        public async Task<IActionResult> GuardarMiPreferencia([FromBody] List<AreaInteres> seleccion)
        {
            // 1. Extraer el ID del usuario desde los Claims del JWT
            // "NameIdentifier" es el estándar para el ID del usuario (ClaimTypes.NameIdentifier)
            //var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var usuarioId = "1";
            if (string.IsNullOrEmpty(usuarioId))
            {
                return Unauthorized("No se pudo identificar al usuario en el token.");
            }

            // 2. Crear el objeto para MongoDB
            var nuevaPreferencia = new UsuarioPreferencia
            {
                UsuarioId = usuarioId, // Integración lograda
                AreasInteres = seleccion
            };

            // 3. Guardar (usando Upsert para que si ya existe, lo actualice)
            var filter = Builders<UsuarioPreferencia>.Filter.Eq(u => u.UsuarioId, usuarioId);
            var existe = await _context.PreferenciasUsuarios.Find(filter).AnyAsync();

            if (existe)
            {
                //409
                return Conflict($"Ya existe una preferencia registrada para el usuario {usuarioId}.");
            }
            await _context.PreferenciasUsuarios.InsertOneAsync(nuevaPreferencia);

            //return Ok(new { mensaje = "Preferencia guardada para el usuario", userId });
            return StatusCode(StatusCodes.Status201Created, new { mensaje= "Temáticas asignadas permanentemente a tu cuenta." }); //poner el mensaje
        }

        //RF-03
        [HttpGet("themes/me")]
        public async Task<IActionResult> VerMisPreferencias([FromBody] List<AreaInteres> seleccion)
        {
            //var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var usuarioId = "1";
            if (string.IsNullOrEmpty(usuarioId))
            {
                return Unauthorized("No se pudo identificar al usuario en el token.");
            }
            //Mandar a traer el registro de Mongo
            var filter = Builders<UsuarioPreferencia>.Filter.Eq(u => u.UsuarioId, usuarioId);
            var lista = await _context.PreferenciasUsuarios.Find(filter).ToListAsync();
            
            
            return StatusCode(StatusCodes.Status200OK, lista); //poner el mensaje
        }

        //RF-04 (Esa que se la saque el Ulises jaja)

        //RF-05
        [HttpPost("/upload")]
        public async Task<IActionResult> UploadFile1(IFormFile file, [FromQuery] string folderId = "root")
        {
            //variables necesarias desde el inicio
            var fileId = Guid.NewGuid().ToString();
            var usuarioId = "1";//este lo debe sacar de la solicitud del MOY
            if (string.IsNullOrEmpty(usuarioId))
            {
                return Unauthorized("No se pudo identificar al usuario en el token.");
            }

            //primero comprobar si el texto es académico
            var validador = new ValidadorAcademico();
            var resultadoValidacion = validador.Validar(file.OpenReadStream());
            if (!resultadoValidacion.EsValido)
            {
                return BadRequest(new
                {
                    nombre = file.Name,
                    Error = resultadoValidacion.Razon
                });
            }
            //guardarlo ya en MinIO
            using var stream = file.OpenReadStream();
            var key = $"usuarios/{usuarioId}/{folderId}/{fileId}";
            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key)
                .WithStreamData(stream)
                .WithObjectSize(file.Length)
                .WithContentType(file.ContentType)
                // Metadatos personalizados
                .WithHeaders(new Dictionary<string, string> {
                    { "x-amz-meta-original-name", file.FileName },
                    { "x-amz-meta-user-id", usuarioId }
                });

            await _minioClient.PutObjectAsync(putObjectArgs);
            //Ahora se realiza la extracción
            var servicioExtraccion = new ServicioExtraccionPdf();
            var evento = servicioExtraccion.Extraer(file.OpenReadStream(), fileId, file.Name);
            var nuevoArchivo = new MetadataArchivo
            {
                id = fileId,
                usuarioId = int.Parse(usuarioId),
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

            using var servicioMensajeria = new ServicioMensajeria(config, Registrar);
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

            return StatusCode(StatusCodes.Status202Accepted,new
            {
                FileId = fileId, //aguas con el Nombre Moy
                nombre = file.FileName,
                estado = "PROCESANDO",//Procesado
                mensaje = "Archivo recibido correctamente"
            });
        }


        //RF-06
        [HttpGet("/{fileId}/status")]
        public async Task<IActionResult> Status1(string fileId)
        {
            //sacarlo del JWT
            var usuarioId = "1";
            if (string.IsNullOrEmpty(usuarioId))
            {
                return Unauthorized("No se pudo identificar al usuario en el token.");
            }
            var projection = Builders<MetadataArchivo>.Projection.Expression(a => a.estado);
    
            var estado = await _context.Archivos
                .Find(a => a.id == fileId)
                .Project(projection)
                .FirstOrDefaultAsync();

            return StatusCode(StatusCodes.Status200OK, new { FileId = fileId, Estado = estado});
        }

        //RF-07
        [HttpGet()]
        public async Task<IActionResult> Listar1()
        {
            //var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var usuarioId = "1"; //sacarlo del JWT
            if (string.IsNullOrEmpty(usuarioId))
            {
                return Unauthorized("No se pudo identificar al usuario en el token.");
            }
            var lista = await _context.Archivos
                .Find(a => a.usuarioId == int.Parse(usuarioId))
                .ToListAsync();

            return Ok(lista);
        }

        //RF-08
        [HttpGet("{fileId}/download")]
        public async Task<IActionResult> GetDownloadUrl1(string fileId, [FromQuery] string folderId = "root")
        {
            var usuarioId = "1"; //se extrae del JWT
            var key = $"usuarios/{usuarioId}/{folderId}/{fileId}";
            if (string.IsNullOrEmpty(usuarioId))
            {
                return Unauthorized("No se pudo identificar al usuario en el token.");
            }

            var args = new PresignedGetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key)
                .WithExpiry(60 * 15); // 15 minutos en segundos

            string url = await _minioClient.PresignedGetObjectAsync(args);
            return Ok(new { DownloadUrl = url });
        }


        //RF-09
        [HttpDelete("{fileId}")]
        public async Task<IActionResult> DeleteFile1(string fileId, [FromQuery] string folderId = "root")
        {
            var usuarioId = "1";//se saca del JWT
            if (string.IsNullOrEmpty(usuarioId))
            {
                return Unauthorized("No se pudo identificar al usuario en el token.");
            }
            var key = $"usuarios/{usuarioId}/{folderId}/{fileId}";

            var args = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key);

            await _minioClient.RemoveObjectAsync(args);
            var filter = Builders<MetadataArchivo>.Filter.And(
                Builders<MetadataArchivo>.Filter.Eq(a => a.id, fileId),
                Builders<MetadataArchivo>.Filter.Eq(a => a.usuarioId, int.Parse(usuarioId))
            );

            // 3. Ejecutar la eliminación
            var resultado = await _context.Archivos.DeleteOneAsync(filter);

            if (resultado.DeletedCount == 0)
            {
                return NotFound("No se encontró el archivo o no tienes permisos para eliminarlo.");
            }

            return Ok(new { mensaje = "Registro eliminado correctamente de todos los nodos" });

            //return NoContent();
        }

    }
}
