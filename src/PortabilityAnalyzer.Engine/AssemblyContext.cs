using Mono.Cecil;

namespace PortabilityAnalyzer.Engine;

/// <summary>Ensamblado cargado con Mono.Cecil, compartido por los detectores durante el analisis.</summary>
public sealed class AssemblyContext : IDisposable
{
    public AssemblyDefinition Assembly { get; }
    public string Path { get; }
    public string Name => Assembly.Name.Name;

    public AssemblyContext(AssemblyDefinition assembly, string path)
    {
        Assembly = assembly;
        Path = path;
    }

    /// <summary>Todos los tipos del ensamblado, incluidos los anidados.</summary>
    public IEnumerable<TypeDefinition> AllTypes() =>
        Assembly.Modules.SelectMany(m => m.Types).SelectMany(Flatten);

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
            foreach (var t in Flatten(nested))
                yield return t;
    }

    public void Dispose() => Assembly.Dispose();
}
