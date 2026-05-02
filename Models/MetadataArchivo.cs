//se tiene que modificar los campos de la BD 
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PDD_Archivos.Models
{
    public class MetadataArchivo
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public string NombreOriginal { get; set; } = null!;
        public long TamanoBytes { get; set; }
        public DateTime FechaSubida { get; set; } = DateTime.UtcNow;
        public string UbicacionStorage { get; set; } = null!; // Ej: Ruta en S3 o disco local
    }
}
