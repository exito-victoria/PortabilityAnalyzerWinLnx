using System.Text.Json;
using System.Text.Json.Serialization;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>Exporta el informe como JSON legible por maquina (contrato estable para integraciones).</summary>
public sealed class JsonReportExporter : IReportExporter
{
    public string Format => "json";

    public void Export(AnalysisReport report, string outputPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new JsonStringEnumConverter<Severity>(),
                new JsonStringEnumConverter<Confidence>(),
                new JsonStringEnumConverter<AssemblyKind>()
            }
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, options));
    }
}
