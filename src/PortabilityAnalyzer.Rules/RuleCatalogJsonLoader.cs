using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Rules;

/// <summary>
/// Carga el catalogo de reglas desde JSON. Si se indica un esquema, valida el documento
/// contra el ANTES de deserializar y rechaza catalogos mal formados.
/// </summary>
public sealed class RuleCatalogJsonLoader : IRuleCatalogLoader
{
    private readonly string? _schemaPath;

    public RuleCatalogJsonLoader(string? schemaPath = null) => _schemaPath = schemaPath;

    /// <summary>
    /// Opciones de (de)serializacion. Los enums PatternKind y MatchMode usan camelCase en el JSON;
    /// Severity y Confidence usan PascalCase, por eso se registran convertidores por tipo.
    /// </summary>
    public static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter<PatternKind>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<MatchMode>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<Severity>());
        options.Converters.Add(new JsonStringEnumConverter<Confidence>());
        return options;
    }

    public RuleCatalog Load(string catalogPath)
    {
        if (!File.Exists(catalogPath))
            throw new FileNotFoundException("No se encuentra el catalogo de reglas.", catalogPath);

        var json = File.ReadAllText(catalogPath);

        if (_schemaPath is not null)
            ValidateAgainstSchema(json, _schemaPath);

        return JsonSerializer.Deserialize<RuleCatalog>(json, BuildOptions())
               ?? throw new InvalidDataException("El catalogo esta vacio o no se pudo deserializar.");
    }

    private static void ValidateAgainstSchema(string json, string schemaPath)
    {
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException("No se encuentra el esquema de validacion.", schemaPath);

        var schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        using var doc = JsonDocument.Parse(json);

        // NOTA: la API de JsonSchema.Net puede variar entre versiones mayores; verificar EvaluationOptions/EvaluationResults.
        var results = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid) return;

        var errores = results.Details
            .Where(d => d.HasErrors && d.Errors is not null)
            .SelectMany(d => d.Errors!.Select(e => $"  [{d.InstanceLocation}] {e.Key}: {e.Value}"))
            .Distinct();

        throw new InvalidDataException(
            "El catalogo de reglas no cumple el esquema:" + Environment.NewLine +
            string.Join(Environment.NewLine, errores));
    }
}
