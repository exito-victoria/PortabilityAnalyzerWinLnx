namespace PortabilityAnalyzer.Core;

/// <summary>Patron que identifica una dependencia. Puro dato (deserializable desde JSON).</summary>
public sealed record RulePattern
{
    public PatternKind Tipo { get; init; }
    public string Valor { get; init; } = string.Empty;
    public MatchMode MatchMode { get; init; }

    /// <summary>Si se indica, el hallazgo solo cuenta cuando algun argumento contiene este texto (p. ej. "windows").</summary>
    public string? ArgumentoContiene { get; init; }
    public string? Nota { get; init; }
}

/// <summary>Estimacion de esfuerzo de tres puntos.</summary>
public sealed record EffortEstimate
{
    public double Optimista { get; init; }
    public double MasProbable { get; init; }
    public double Pesimista { get; init; }

    /// <summary>Media segun estimacion de tres puntos (PERT): (O + 4M + P) / 6.</summary>
    public double Media => (Optimista + 4 * MasProbable + Pesimista) / 6.0;

    public static readonly EffortEstimate Zero = new();

    public EffortEstimate Add(EffortEstimate o) => new()
    {
        Optimista = Optimista + o.Optimista,
        MasProbable = MasProbable + o.MasProbable,
        Pesimista = Pesimista + o.Pesimista
    };

    public EffortEstimate Scale(double f) => new()
    {
        Optimista = Optimista * f,
        MasProbable = MasProbable * f,
        Pesimista = Pesimista * f
    };
}

/// <summary>Una regla del catalogo.</summary>
public sealed record PortabilityRule
{
    public string Id { get; init; } = string.Empty;
    public string Categoria { get; init; } = string.Empty;
    public string Descripcion { get; init; } = string.Empty;
    public RulePattern Patron { get; init; } = new();
    public Severity Severidad { get; init; }
    public bool EsBloqueante { get; init; }
    public string AlternativaLinux { get; init; } = string.Empty;
    public EffortEstimate Esfuerzo { get; init; } = EffortEstimate.Zero;
    public Confidence Confianza { get; init; }
}

/// <summary>Catalogo completo cargado desde JSON.</summary>
public sealed record RuleCatalog
{
    public string SchemaVersion { get; init; } = string.Empty;
    public IReadOnlyList<PortabilityRule> Reglas { get; init; } = new List<PortabilityRule>();
}
