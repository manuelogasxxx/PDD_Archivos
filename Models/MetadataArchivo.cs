//se tiene que modificar los campos de la BD 
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PDD_Archivos.Models
{
    public class MetadataArchivo
    {
        [BsonId]
        //[BsonRepresentation(BsonType.ObjectId)]
        public string? FileId { get; set; }

        public int IdUser { get; set; }

        public string NombreOriginal { get; set; } = null!;
        public DateTime FechaSubida { get; set; } = DateTime.UtcNow;
        public string Categoria { get; set; }

        public string Subcategoria { get; set; }

        public string Estado { get; set; }

    }
}
