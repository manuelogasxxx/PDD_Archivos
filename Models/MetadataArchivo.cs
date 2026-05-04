//se tiene que modificar los campos de la BD 
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PDD_Archivos.Models
{
    public class Clasificacion
    {
        public string areaId { get; set; }
        public string nombreArea { get; set; }
        public string subcategoriaId { get; set; }
        public string nombreSubcategoria { get; set; }
    }

    public class Extraccion
    {
        public string titulo { get; set; }
        public string resumen { get; set; }
        public List<string> palabrasClave { get; set; }
        public string idioma { get; set; }
    }

    //un enum para los estados

    public enum Estado
    {
        Encolado,
        Procesado,
        Error
    }
    public class MetadataArchivo
    {
        [BsonId]
        //[BsonRepresentation(BsonType.ObjectId)]
        public string? id { get; set; }

        public int usuarioId { get; set; }

        public string nombreOriginal { get; set; } = null!;
        public DateTime fechaSubida { get; set; } = DateTime.UtcNow;
        public Estado estado { get; set; }
        
        public Extraccion extraccion { get; set; }

        public Clasificacion clasificacion { get; set; }

    }
}
