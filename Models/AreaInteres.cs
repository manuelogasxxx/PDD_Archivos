//es el JSon para las categorías predefinidas y la relación Usuario<->Area
using MongoDB.Bson.Serialization.Attributes;

namespace PDD_Archivos.Models
{
    public class AreaInteres
    {
        public string AreaId { get; set; }
        public string NombreArea { get; set; }
        public List<Subcategoria> Subcategorias { get; set; } = new();

    }

    public class Subcategoria
    {
        public string Id { get; set; }
        public string Nombre { get; set; }

        public string NombreMostrar { get; set; }
    }

    //catálogo que solo se sube una ves

    public class CatalogoAreas
    {
        public string Id { get; set; }
        public List<AreaInteres> areas { get; set; } = new();

    }

    //la relación Usuario-Area
    public class UsuarioPreferencia
    {
        [BsonId]
        [BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)]
        public string Id { get; set; }
        public string UsuarioId { get; set; }

        public  List<AreaInteres> AreasInteres  { get; set; }
    }
}
