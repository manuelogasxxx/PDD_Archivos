using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Minio;
using MongoDB.Driver;
using PDD_Archivos.Configuracion;
using PDD_Archivos.Models;

namespace PDD_Archivos.Controllers
{
    //se colocaría el [Authorize]
    [Route("[controller]")]
    [ApiController]

    public class admin : ControllerBase
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
        public admin(IMinioClient minioClient, MongoContext context)
        {
            this._minioClient = minioClient;
            this._context = context;
            this.config = new ConfiguracionRabbitMq
            {
				//Servidores = ["localhost"],
				Servidores = ["172.26.160.140"],
				Usuario = "guest",
                Contrasena = "guest",
                UsarColaQuorum = false,
            };
        }

        [HttpGet("users/{userId}/themes")]
        public async Task<IActionResult> VerMisPreferencias(string userId)
        {
            //no se si se vaya a usar un JWT para el admin
            //var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            //var usuarioId = "1";
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("No se pudo identificar al usuario en el token.");
            }
            //Mandar a traer el registro de Mongo
            var filter = Builders<UsuarioPreferencia>.Filter.Eq(u => u.UsuarioId, userId);
            var lista = await _context.PreferenciasUsuarios.Find(filter).ToListAsync();
            return StatusCode(StatusCodes.Status200OK, lista); //poner el mensaje
        }

        [HttpPost("users/{userId}/themes")]
        public async Task<IActionResult> GuardarMiPreferencia(string userId,[FromBody] List<AreaInteres> seleccion)
        {
            // 1. Extraer el ID del usuario desde los Claims del JWT
            // "NameIdentifier" es el estándar para el ID del usuario (ClaimTypes.NameIdentifier)
            //var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            //var usuarioId = "1";
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("No se pudo identificar al usuario en el token.");
            }

            // 2. Crear el objeto para MongoDB
            var nuevaPreferencia = new UsuarioPreferencia
            {
                UsuarioId = userId, // Integración lograda
                AreasInteres = seleccion
            };

            // 3. Guardar (usando Upsert para que si ya existe, lo actualice)
            var filter = Builders<UsuarioPreferencia>.Filter.Eq(u => u.UsuarioId, userId);
            var options = new ReplaceOptions { IsUpsert = true };
            await _context.PreferenciasUsuarios.ReplaceOneAsync(filter, nuevaPreferencia, options);
            //return Ok(new { mensaje = "Preferencia guardada para el usuario", userId });
            return StatusCode(StatusCodes.Status201Created, new { mensaje = "Temáticas asignadas FORZADAS por el admin." }); //poner el mensaje
        }
    }
}
