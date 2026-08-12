namespace PortabilityAnalyzer.Core;

/// <summary>
/// Un paso del orden de compilacion de la solucion. <see cref="Level"/> agrupa los proyectos que se
/// pueden compilar en paralelo (mismo nivel = sin dependencias entre si); dentro de un nivel se ordenan
/// por nombre. <see cref="DependsOn"/> son las referencias de proyecto directas (dentro de la solucion).
/// </summary>
public sealed record BuildOrderStep(int Level, string Project, IReadOnlyList<string> DependsOn);

/// <summary>
/// Orden de compilacion resuelto por topologia de <c>ProjectReference</c>. Si hay un ciclo de
/// referencias, <see cref="HasCycle"/> es true y <see cref="CycleProjects"/> lista los proyectos
/// implicados (no se puede dar un orden lineal para ellos).
/// </summary>
public sealed record BuildOrder(
    IReadOnlyList<BuildOrderStep> Steps,
    bool HasCycle,
    IReadOnlyList<string> CycleProjects)
{
    public static readonly BuildOrder Empty = new(new List<BuildOrderStep>(), false, new List<string>());
}
