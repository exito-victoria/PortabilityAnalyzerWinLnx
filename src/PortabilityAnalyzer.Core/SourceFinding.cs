namespace PortabilityAnalyzer.Core;

/// <summary>
/// Hallazgo a nivel de codigo fuente: un uso de una API propia de Windows localizado en un fichero
/// <c>.cs</c>, con su ubicacion exacta, el segmento de codigo, el simbolo implicado (using/tipo/metodo)
/// y como corregirlo para multiplataforma.
/// </summary>
public sealed record SourceFinding
{
    public required string Project { get; init; }
    public required string File { get; init; }          // ruta relativa al proyecto
    public required int Line { get; init; }
    public required string Kind { get; init; }          // "using" | "P/Invoke" | "Tipo" | "Atributo"
    public required string Symbol { get; init; }        // el using / tipo / metodo / DLL implicado
    public required string Categoria { get; init; }
    public string? Clase { get; init; }                 // clase contenedora
    public string? Metodo { get; init; }                // metodo contenedor
    public required string Segmento { get; init; }      // la linea/sentencia de codigo
    public required string ComoCorregir { get; init; }  // guia de correccion multiplataforma
}
