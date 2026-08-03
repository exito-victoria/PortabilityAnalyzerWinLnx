using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>Un grupo de hallazgos identicos (misma regla y misma evidencia) con su recuento.</summary>
public sealed record FindingGroup(Finding Representative, int Count);

/// <summary>
/// Agrupacion comun a todos los exportadores: colapsa las ocurrencias identicas (misma regla y misma
/// evidencia) en una sola entrada con su recuento, de modo que cada dependencia aparezca una unica vez.
/// </summary>
public static class ReportGrouping
{
    public static IReadOnlyList<FindingGroup> Group(IEnumerable<Finding> findings) =>
        findings
            .GroupBy(f => (f.RuleId, f.Evidencia))
            .Select(g => new FindingGroup(g.First(), g.Count()))
            .OrderByDescending(g => g.Representative.Severidad)
            .ThenByDescending(g => g.Count)
            .ToList();

    /// <summary>Ubicacion de ejemplo (tipo / metodo); anade una elipsis si el grupo tiene mas de una ocurrencia.</summary>
    public static string SampleLocation(Finding f, int count)
    {
        var loc = string.Join(" / ", new[] { f.Type, f.Method }.Where(x => !string.IsNullOrEmpty(x)));
        return count > 1 && loc.Length > 0 ? loc + " …" : loc;
    }
}
