using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace ExtractorPdf.Servicios;

public class ResultadoValidacion
{
    public bool EsValido { get; init; }
    public string Razon { get; init; } = string.Empty;
    public List<string> IndicadoresEncontrados { get; init; } = [];
    public int Puntuacion { get; init; }
}

public class ValidadorAcademico
{
    private static readonly List<(string Nombre, Regex Patron)> Indicadores =
    [
        (
            "Abstract / Resumen",
            new Regex(@"\b(abstract|resumen)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
        ),
        (
            "Introduction / Introducción",
            new Regex(@"\b(introduction|introducción|introduccion)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
        ),
        (
            "References / Bibliografía",
            new Regex(@"\b(references|bibliography|referencias|bibliografía|bibliografia)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
        ),
        (
            "Keywords / Palabras clave",
            new Regex(@"\b(keywords?|index\s+terms?|palabras\s+clave)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
        ),
        (
            "DOI del artículo",
            new Regex(@"\b(doi\s*:\s*10\.\d{4,}/\S+|https?://doi\.org/10\.\d{4,}/\S+)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
        ),
        (
            "Conclusion / Conclusión",
            new Regex(@"\b(conclusion|conclusions|conclusión|conclusiones)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
        ),
        (
            "Methodology / Metodología",
            new Regex(@"\b(methodology|methods|materials\s+and\s+methods|metodología|metodologia|método)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
        ),
        (
            "Secciones numeradas (ej: 1. Introduction)",
            new Regex(@"^\s*[1-9]\.\s+(introduction|background|related\s+work|methodology|results|conclusion)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled)
        ),
    ];

    private const int PuntuacionMinima = 2;

    //Validación desde Stream
    public ResultadoValidacion Validar(Stream streamPdf)
    {
        if (streamPdf is null || streamPdf.Length == 0)
            return new ResultadoValidacion
            {
                EsValido = false,
                Razon = "El stream del archivo esta vacio",
                IndicadoresEncontrados = [],
                Puntuacion = 0
            };

        // Verificar los primeros 4 bytes de un pdf válido para saber que es un pdf
        if (!EsPdfValido(streamPdf))
            return new ResultadoValidacion
            {
                EsValido = false,
                Razon = "El archivo no es un pdf",
                IndicadoresEncontrados = [],
                Puntuacion = 0
            };

        // Resetear el stream al inicio 
        streamPdf.Position = 0;

        string textoCombinado;
        int totalPaginas;

        try
        {
            // abre directamente desde Stream
            using var documento = PdfDocument.Open(streamPdf,
                new ParsingOptions { UseLenientParsing = true });

            totalPaginas = documento.NumberOfPages;

            // Leer primeras 3 páginas y la ultima
            var paginasALeer = Enumerable.Range(1, Math.Min(3, totalPaginas))
                .Append(totalPaginas)
                .Distinct()
                .ToList();

            var constructor = new System.Text.StringBuilder();
            foreach (var numeroPagina in paginasALeer)
            {
                var pagina = documento.GetPage(numeroPagina);
                foreach (var palabra in pagina.GetWords())
                    constructor.Append(palabra.Text).Append(' ');
                constructor.AppendLine();
            }

            textoCombinado = constructor.ToString();
        }
        catch (Exception ex)
        {
            return new ResultadoValidacion
            {
                EsValido = false,
                Razon = $"No se pudo leer el pdf: {ex.Message}",
                IndicadoresEncontrados = [],
                Puntuacion = 0
            };
        }

        if (string.IsNullOrWhiteSpace(textoCombinado))
            return new ResultadoValidacion
            {
                EsValido = false,
                Razon = "El pdf no contiene texto seleccionable. " +
                                       "Posiblemente es un documento escaneado.",
                IndicadoresEncontrados = [],
                Puntuacion = 0
            };

        var encontrados = Indicadores
            .Where(indicador => indicador.Patron.IsMatch(textoCombinado))
            .Select(indicador => indicador.Nombre)
            .ToList();

        int puntuacion = encontrados.Count;
        bool esValido = puntuacion >= PuntuacionMinima;

        return new ResultadoValidacion
        {
            EsValido = esValido,
            Puntuacion = puntuacion,
            IndicadoresEncontrados = encontrados,
            Razon = esValido
                ? $"Documento válido: {puntuacion} de {Indicadores.Count} " +
                  "indicadores académicos encontrados."
                : $"Documento rechazado: solo {puntuacion} de {PuntuacionMinima} " +
                  "indicadores mínimos encontrados. " +
                  "Probablemente no es un artículo académico."
        };
    }

    //Validación desde ruta 
    public ResultadoValidacion Validar(string rutaPdf)
    {
        if (!File.Exists(rutaPdf))
            throw new FileNotFoundException($"PDF no encontrado: {rutaPdf}");

        using var stream = File.OpenRead(rutaPdf);
        return Validar(stream);
    }

    //verificación de magic bytes para saber que es un pdf
    private static bool EsPdfValido(Stream stream)
    {
        if (stream.Length < 4) return false;

        stream.Position = 0;
        Span<byte> encabezado = stackalloc byte[4];
        stream.Read(encabezado);

        // %PDF en hexadecimal
        return encabezado[0] == 0x25 &&
               encabezado[1] == 0x50 &&
               encabezado[2] == 0x44 &&
               encabezado[3] == 0x46;
    }
}