using System.Text;
using System.Text.RegularExpressions;
using ExtractorPdf.Modelos;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ExtractorPdf.Servicios;

public class ServicioExtraccionPdf
{
    //inicio del resumen/abstract
    private static readonly Regex PatronInicioResumen = new(
        @"\b(abstract|resumen|résumé|zusammenfassung)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    //fin del resumen cuando se encuntre la siguente seccion
    private static readonly Regex PatronFinResumen = new(
        @"\b(introduction|introducción|introduccion|keywords|palabras\s+clave|" +
        @"1\.\s*introduction|i\.\s*introduction|background|motivation)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    //palabras clave en el pdf
    private static readonly Regex PatronLineaPalabrasClave = new(
        @"(?:keywords?|palabras\s+clave|index\s+terms?)\s*[:\-—]?\s*(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    //separadores entre palabras
    private static readonly Regex SeparadorPalabrasClave = new(
        @"[;,·•]\s*", RegexOptions.Compiled);
    //ignorar lines sin contenido importante
    private static readonly Regex PatronRuido = new(
        @"^\s*(\d{1,4}|https?://\S+|doi:\S+|©.+|received:.+|accepted:.+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Recibe el stream directamente desde la petición HTTP.
    public EventoArchivoEncolado Extraer(Stream streamPdf, string idArchivo, string nombreArchivo)
    {
        if (streamPdf is null || streamPdf.Length == 0)
            return new EventoArchivoEncolado
            {
                IdArchivo = idArchivo,
                NombreArchivoOriginal = nombreArchivo,
                EstadoExtraccion = EstadoExtraccion.NoClasificable
            };

        // Resetear el stream al inicio por si el validador lo leyó antes
        streamPdf.Position = 0;

        using var documento = PdfDocument.Open(streamPdf,
            new ParsingOptions { UseLenientParsing = true });

        return ExtraerDesdeDocumento(documento, idArchivo, nombreArchivo);
    }

    //recibe ruta
    public EventoArchivoEncolado Extraer(string rutaPdf, string idArchivo)
    {
        if (!File.Exists(rutaPdf))
            throw new FileNotFoundException($"PDF no encontrado: {rutaPdf}");

        using var stream = File.OpenRead(rutaPdf);
        return Extraer(stream, idArchivo, Path.GetFileName(rutaPdf));
    }

    //ambos metodos se pueden usar ya sea stream o ruta
    private static EventoArchivoEncolado ExtraerDesdeDocumento(
        PdfDocument documento, string idArchivo, string nombreArchivo)
    {
        //esxtrae texto de las primeras 4 paginas
        var textosPorPagina = ExtraerTextosPorPagina(documento, maximoPaginas: 4);
        var textoCompleto = string.Join("\n", textosPorPagina);

        //no encontro texto 
        if (string.IsNullOrWhiteSpace(textoCompleto))
            return new EventoArchivoEncolado
            {
                IdArchivo = idArchivo,
                NombreArchivoOriginal = nombreArchivo,
                EstadoExtraccion = EstadoExtraccion.NoClasificable
            };

        var titulo = ExtraerTitulo(documento, textosPorPagina[0]);
        var resumen = ExtraerResumen(textoCompleto);
        var palabrasClave = ExtraerPalabrasClave(textoCompleto);
        var idioma = DetectarIdioma(resumen ?? textosPorPagina[0]);

        //si no encontro el delimitador del resumen toma un pedaso de texto 
        var estado = resumen is not null
            ? EstadoExtraccion.Completo
            : EstadoExtraccion.Parcial;

        string? textoRespaldo = null;
        if (resumen is null)
        {
            var textoPrimeraLimpio = LimpiarTexto(textosPorPagina[0]);
            textoRespaldo = textoPrimeraLimpio[..Math.Min(1500, textoPrimeraLimpio.Length)];
        }

        return new EventoArchivoEncolado
        {
            IdArchivo = idArchivo,
            NombreArchivoOriginal = nombreArchivo,
            Titulo = titulo,
            Resumen = resumen ?? string.Empty,
            PalabrasClave = palabrasClave,
            Idioma = idioma,
            EstadoExtraccion = estado,
            TextoRespaldo = textoRespaldo,
            FechaSubida = DateTime.UtcNow
        };
    }


    private static List<string> ExtraerTextosPorPagina(PdfDocument documento, int maximoPaginas)
    {
        var textosPaginas = new List<string>();
        int limite = Math.Min(documento.NumberOfPages, maximoPaginas);

        for (int numeroPagina = 1; numeroPagina <= limite; numeroPagina++)
        {
            var pagina = documento.GetPage(numeroPagina);
            var palabras = pagina.GetWords().ToList();

            if (!palabras.Any())
            {
                textosPaginas.Add(string.Empty);
                continue;
            }
            //agrupa las palabras que tengan la misma altura
            var lineas = AgruparPalabrasEnLineas(palabras, toleranciaVertical: 3.0);
            var constructor = new StringBuilder();

            foreach (var linea in lineas)
            {
                //ordena las palabras de isquierda a dercha 
                var ordenadas = linea.OrderBy(p => p.BoundingBox.Left).ToList();
                constructor.AppendLine(string.Join(" ", ordenadas.Select(p => p.Text)));
            }

            textosPaginas.Add(constructor.ToString());
        }

        return textosPaginas;
    }

    //pone las palabras en linea 
    private static List<List<Word>> AgruparPalabrasEnLineas(
        List<Word> palabras, double toleranciaVertical)
    {
        var lineas = new List<List<Word>>();
        var ordenadas = palabras.OrderByDescending(p => p.BoundingBox.Bottom).ToList();

        foreach (var palabra in ordenadas)
        {
            var lineaExistente = lineas.FirstOrDefault(linea =>
                Math.Abs(linea[0].BoundingBox.Bottom - palabra.BoundingBox.Bottom)
                <= toleranciaVertical);

            if (lineaExistente is not null)
                lineaExistente.Add(palabra);
            else
                lineas.Add([palabra]);
        }

        return lineas;
    }

    //extra el titulo que es el texto de mayor tamaño 
    private static string ExtraerTitulo(PdfDocument documento, string textoPrimeraPagina)
    {
        try
        {
            var primeraPagina = documento.GetPage(1);
            var letras = primeraPagina.Letters.ToList();

            if (!letras.Any())
                return InferirTituloDesdeTexto(textoPrimeraPagina);

            var grupoPorTamano = letras
                .Where(l => !string.IsNullOrWhiteSpace(l.Value))
                .GroupBy(l => Math.Round(l.FontSize, 1))
                .OrderByDescending(g => g.Key)
                .FirstOrDefault();

            if (grupoPorTamano is null)
                return InferirTituloDesdeTexto(textoPrimeraPagina);

            var textoTitulo = string.Concat(grupoPorTamano.Select(l => l.Value)).Trim();

            if (textoTitulo.Length < 10 || textoTitulo.All(char.IsDigit))
                return InferirTituloDesdeTexto(textoPrimeraPagina);

            return LimpiarTitulo(textoTitulo);
        }
        catch
        {
            return InferirTituloDesdeTexto(textoPrimeraPagina);
        }
    }
    // Método de respaldo toma las primera lineas antes del resumen/abstract
    private static string InferirTituloDesdeTexto(string textoPrimeraPagina)
    {
        var lineas = textoPrimeraPagina
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 15 && !PatronRuido.IsMatch(l))
            .Take(4)
            .ToList();

        if (!lineas.Any()) return "Título no encontrado";

        var lineasTitulo = new List<string>();
        foreach (var linea in lineas)
        {
            // Detener si aparece una afiliación instituciona
            if (Regex.IsMatch(linea,
                @"(@|\buniversity\b|\bdept\b|\binstitute\b|\buniversidad\b|\bdepartamento\b)",
                RegexOptions.IgnoreCase))
                break;

            lineasTitulo.Add(linea);
            if (lineasTitulo.Count == 2) break;
        }

        return LimpiarTitulo(string.Join(" ", lineasTitulo));
    }

    private static string LimpiarTitulo(string titulo)
    {
        // Insertar espacio antes de mayúscula que siga a minúscula
        titulo = Regex.Replace(titulo, @"(?<=[a-z])(?=[A-Z])", " ");
        // Eliminar espacios múltiples
        titulo = Regex.Replace(titulo, @"\s+", " ").Trim();
        // Eliminar caracteres no imprimibles
        titulo = Regex.Replace(titulo, @"[^\x20-\x7E\u00C0-\u024F]", "");
        return titulo.Length > 250 ? titulo[..250] : titulo;
    }

    // Localiza el resumen buscando el marcador Abstract/Resumen
    private static string? ExtraerResumen(string textoCompleto)
    {
        var coincidenciaInicio = PatronInicioResumen.Match(textoCompleto);
        if (!coincidenciaInicio.Success) return null;
        // Avanzar después de la palabra resumon o abstract
        int indiceInicio = coincidenciaInicio.Index + coincidenciaInicio.Length;
        var textoDespues = textoCompleto[indiceInicio..].TrimStart(':', '-', ' ', '\n', '\r');
        int inicioContenido = textoCompleto.Length - textoDespues.Length;
        // Buscar el fin del resumen a partir del inicio del siguiente contenido
        var coincidenciaFin = PatronFinResumen.Match(textoCompleto, inicioContenido);
        int indiceFin = coincidenciaFin.Success
            ? coincidenciaFin.Index
            : inicioContenido + 2500;

        var textoRaw = textoCompleto[inicioContenido..Math.Min(indiceFin, textoCompleto.Length)];
        var limpio = LimpiarTexto(textoRaw);
        // Si quedó muy corto se ignora
        if (limpio.Length < 80) return null;
        //se retornan los primeros 2000 carcteres
        return limpio.Length > 2000 ? limpio[..2000] : limpio;
    }
    // Extrae la línea de palabras clave y la divide en tokens individuales.
    private static List<string> ExtraerPalabrasClave(string textoCompleto)
    {
        var coincidencia = PatronLineaPalabrasClave.Match(textoCompleto);
        if (!coincidencia.Success) return [];

        var textoRaw = coincidencia.Groups[1].Value;
        var palabrasClave = SeparadorPalabrasClave
            .Split(textoRaw)
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length > 2 && p.Length < 60)
            .Distinct()
            .Take(15)
            .ToList();

        return palabrasClave;
    }
    // Detecta el idioma comparando la frecuencia de palabras de conexion
    private static string DetectarIdioma(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "es";

        var textoMinusculas = texto.ToLowerInvariant();
        var palabras = Regex.Split(textoMinusculas, @"\W+")
            .Where(p => p.Length > 2)
            .ToHashSet();
        // Contar cuántas palabras de conexión de cada idioma aparecen en el texto
        var puntuaciones = new Dictionary<string, int>
        {
            ["en"] = ContarCoincidencias(palabras,
                ["the","this","that","with","from","have","been","were",
                 "their","these","which","study","paper","method","result","propose"]),
            ["es"] = ContarCoincidencias(palabras,
                ["que","con","para","una","los","las","del","por","como",
                 "este","esta","son","estudios","método","resultado","propone"]),
            ["fr"] = ContarCoincidencias(palabras,
                ["les","des","une","pour","dans","avec","sont","cette",
                 "nous","ils","étude","méthode","résultat"]),
            ["de"] = ContarCoincidencias(palabras,
                ["die","der","und","das","eine","mit","von","wird",
                 "werden","haben","studie","methode","ergebnis"]),
            ["pt"] = ContarCoincidencias(palabras,
                ["que","com","para","uma","dos","das","pelo","como",
                 "este","esta","são","estudo","método","resultado"]),
        };
        // El idioma con más coincidencias gana
        return puntuaciones.MaxBy(par => par.Value).Key;
    }

    private static int ContarCoincidencias(HashSet<string> palabras, string[] stopwords)
        => stopwords.Count(palabras.Contains);

    private static string LimpiarTexto(string texto)
    {
        var lineas = texto
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !PatronRuido.IsMatch(l))
            .Select(l => l.Trim());

        var unido = string.Join(" ", lineas);
        // Eliminar guiones de separación
        unido = Regex.Replace(unido, @"-\s+", "");
        // ignorar espacios múltiples
        unido = Regex.Replace(unido, @"\s{2,}", " ");
        return unido.Trim();
    }
}