namespace PortabilityAnalyzer.Core;

/// <summary>Cómo se ha reescrito un proyecto original en la solución multiplataforma.</summary>
public sealed record RewrittenProject(
    string OriginalProject,
    string Kind,                                  // "Portable" | "Separable" | "SoloWindows"
    string? Reason,
    IReadOnlyList<string> OutputProjects,         // proyectos generados (p. ej. X.Core, X.Windows)
    int PortableFiles,
    int WindowsFiles);

/// <summary>Resultado de reescribir una solución completa a multiplataforma (split total).</summary>
public sealed record RewriteResult
{
    public required string OutputDir { get; init; }
    public required string SolutionFile { get; init; }
    public IReadOnlyList<RewrittenProject> Projects { get; init; } = new List<RewrittenProject>();

    /// <summary>Avisos de portabilidad que requieren intervención manual (p. ej. referencias cruzadas
    /// de un núcleo portable a un proyecto que quedó solo-Windows).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = new List<string>();
}
