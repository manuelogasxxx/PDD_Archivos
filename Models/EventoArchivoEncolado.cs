namespace PDD_Archivos.Modelos;

public class EventoArchivoEncolado
{
    // Id único del archivo generado al momento de subirlo.
    public string IdArchivo { get; set; } = string.Empty;

    // Título del artículo extraído de la primera página
    public string Titulo { get; set; } = string.Empty;
    
    // Texto completo del abstract extraído
    public string Resumen { get; set; } = string.Empty;

    // Lista de palabras clave extraídas
    public List<string> PalabrasClave { get; set; } = [];

    // Idioma detectado del documento 
    public string Idioma { get; set; } = "es";

    //resultado de la extracción del contenido.
    public EstadoExtraccion EstadoExtraccion { get; set; } = EstadoExtraccion.Completo;

    //no se encontró un resumen bien delimitado que contiene las primeras 1500 letras de la primera página
    public string? TextoRespaldo { get; set; }

    //Nombre original del archivo subido por el usuario
    public string NombreArchivoOriginal { get; set; } = string.Empty;

    // Fecha y hora 
    public DateTime FechaSubida { get; set; } = DateTime.UtcNow;
}
// Posibles resultados del proceso de extracción 
public enum EstadoExtraccion
{
    Completo,

    // Solo se extrajo texto parcial
    Parcial,

    //no puede prosesarse 
    NoClasificable
}
