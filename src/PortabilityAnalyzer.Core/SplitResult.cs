namespace PortabilityAnalyzer.Core;

/// <summary>Resultado de dividir un proyecto (rol divisiblePorUI) en una parte multiplataforma y otra Windows.</summary>
public sealed record SplitResult
{
    public required string OriginalProject { get; init; }
    public required string MultiProject { get; init; }     // proyecto .NET 8 multiplataforma
    public required string WindowsProject { get; init; }   // proyecto net8.0-windows
    public required string OutputDir { get; init; }         // carpeta donde se generaron
    public required int PortableFiles { get; init; }
    public required int WindowsFiles { get; init; }

    /// <summary>Referencias de codigo portable a tipos que quedaron en la parte Windows (requieren abstraccion).</summary>
    public IReadOnlyList<string> CrossReferences { get; init; } = new List<string>();

    /// <summary>Acciones manuales pendientes para que el scaffold compile / quede limpio.</summary>
    public IReadOnlyList<string> ManualNotes { get; init; } = new List<string>();
}
