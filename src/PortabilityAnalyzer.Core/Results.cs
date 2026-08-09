namespace PortabilityAnalyzer.Core;

/// <summary>Hallazgo con trazabilidad completa hasta el sitio de deteccion.</summary>
public sealed record Finding
{
    public required string RuleId { get; init; }
    public required string Categoria { get; init; }
    public required Severity Severidad { get; init; }
    public required bool EsBloqueante { get; init; }
    public required Confidence Confianza { get; init; }
    public required string AlternativaLinux { get; init; }
    public required EffortEstimate Esfuerzo { get; init; }

    // Trazabilidad: ensamblado -> tipo -> metodo -> offset IL.
    public required string Assembly { get; init; }
    public string? Type { get; init; }
    public string? Method { get; init; }
    public int? IlOffset { get; init; }

    /// <summary>Evidencia concreta (nombre de DLL P/Invoke, literal, atributo, API llamada...).</summary>
    public string? Evidencia { get; init; }

    // --- Guia multiplataforma (heredada de la regla). ---
    public IReadOnlyList<string> PasosRemediacion { get; init; } = new List<string>();
    public SeparationStrategy? EstrategiaSeparacion { get; init; }
    public string? NotaComun { get; init; }
}

public sealed record AssemblyClassification(string Path, string Name, AssemblyKind Kind, string? Reason = null);

public sealed record AssemblyAnalysisResult
{
    public required AssemblyClassification Classification { get; init; }
    public IReadOnlyList<Finding> Findings { get; init; } = new List<Finding>();
    public EffortEstimate Effort { get; init; } = EffortEstimate.Zero;
    public Severity MaxSeverity { get; init; } = Severity.Info;
    public bool IsThirdParty { get; init; }

    /// <summary>Hallazgos confirmados (confianza Alta/Media): alimentan esfuerzo, severidad y bloqueantes.</summary>
    public IEnumerable<Finding> ConfirmedFindings() => Findings.Where(f => f.Confianza != Confidence.Baja);

    /// <summary>Hallazgos de senal debil (confianza Baja): se listan para revision manual y NO cuentan
    /// en el esfuerzo ni en la severidad, por su alta tasa de falsos positivos.</summary>
    public IEnumerable<Finding> ManualReviewFindings() => Findings.Where(f => f.Confianza == Confidence.Baja);

    public bool HasBlocker => ConfirmedFindings().Any(f => f.EsBloqueante);
}

public sealed record AnalysisReport
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required IReadOnlyList<AssemblyAnalysisResult> Assemblies { get; init; }
    public EffortEstimate TotalEffort { get; init; } = EffortEstimate.Zero;
    public int BlockerCount { get; init; }
    public int AnalyzedCount { get; init; }
    public int SkippedCount { get; init; }

    /// <summary>Desglose del esfuerzo por bucket de coste multiplataforma (incluye Pruebas y CI).</summary>
    public IReadOnlyList<BucketEffort> CostByBucket { get; init; } = new List<BucketEffort>();

    /// <summary>Hallazgos a nivel de codigo fuente (fichero/linea/segmento) con guia de correccion.</summary>
    public IReadOnlyList<SourceFinding> SourceFindings { get; init; } = new List<SourceFinding>();

    /// <summary>Roles de proyecto configurados (API obligatoria, no modificables, divisibles por UI).</summary>
    public ProjectRoles Roles { get; init; } = new();
}
