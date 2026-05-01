/*
 Tiene los endpoints para subir archivos al minIO
 Notas generales:
    ->El ID de usuario está hardcodeado pero debe sacarse de la solicitud HTTP
    ->Se debe de integrar la parte de la base de datos para guaardar los registros
    ->Hay un problema con el forwarding de puertos para que otras computadoras
      accedan al recurso de MinIO, en caso de que el servicio principal (el que usa local host)
      se caiga. En w11 se puede usar "Mirror" del WSL
    ->
 */
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;

namespace PDD_Archivos.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class files : ControllerBase
    {
        private readonly IMinioClient _minioClient;
        private readonly string _bucketName = "pdfs";

        //el constructor
        public files (IMinioClient minioClient)
        {
            this._minioClient = minioClient;
        }

        //ahora si las peticiones del moy

        //subir archivos
        [HttpPost("upload")]
        public async Task<IActionResult> UploadFile(IFormFile file, [FromQuery] string folderId = "root")
        {
            var userId = "1";//este lo debe sacar de la solicitud
            var fileId = Guid.NewGuid().ToString();
            //la srting se puede hacer mas grande dependiendo de cuantos folders existan
            var key = $"usuarios/{userId}/{folderId}/{fileId}";

            // Usamos el stream directamente del IFormFile para no duplicar memoria
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

            return Ok(new
            {
                FileId = fileId,
                nombre = file.FileName,
                estado = "PROCESANDO",
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
    }
}
