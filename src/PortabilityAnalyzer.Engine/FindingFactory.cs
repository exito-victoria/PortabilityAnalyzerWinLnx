using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine;

/// <summary>Construye un <see cref="Finding"/> a partir de una regla y la ubicacion de trazabilidad.</summary>
internal static class FindingFactory
{
    public static Finding From(PortabilityRule rule, string assembly,
        string? type = null, string? method = null, int? ilOffset = null, string? evidencia = null) => new()
    {
        RuleId = rule.Id,
        Categoria = rule.Categoria,
        Severidad = rule.Severidad,
        EsBloqueante = rule.EsBloqueante,
        Confianza = rule.Confianza,
        AlternativaLinux = rule.AlternativaLinux,
        Esfuerzo = rule.Esfuerzo,
        Assembly = assembly,
        Type = type,
        Method = method,
        IlOffset = ilOffset,
        Evidencia = evidencia,
        PasosRemediacion = rule.PasosRemediacion,
        EstrategiaSeparacion = rule.EstrategiaSeparacion,
        NotaComun = rule.NotaComun
    };
}
