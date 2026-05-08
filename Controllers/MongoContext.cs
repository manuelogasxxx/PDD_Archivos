
using MongoDB.Driver;
using PDD_Archivos.Models;

namespace PDD_Archivos.Controllers
{
    
    public class MongoContext
    {
        private readonly IMongoDatabase _database;

        public MongoContext(IConfiguration configuration)
        {
            // Traemos la configuración del archivo appsettings.json
            var connectionString = configuration.GetValue<string>("MongoSettings:ConnectionString");
            var databaseName = configuration.GetValue<string>("MongoSettings:DatabaseName");

            var client = new MongoClient(connectionString);
            _database = client.GetDatabase(databaseName);
        }

        public IMongoCollection<MetadataArchivo> Archivos =>
        _database.GetCollection<MetadataArchivo>("Metadata");

        //nueva colección para el catálogo general
        public IMongoCollection<CatalogoAreas> Catalogos =>
        _database.GetCollection<CatalogoAreas>("Catalogos");

        //nueva colección para usuario-areaInteres
        public IMongoCollection<UsuarioPreferencia> PreferenciasUsuarios =>
        _database.GetCollection<UsuarioPreferencia>("PreferenciasUsuarios");
    }
}
