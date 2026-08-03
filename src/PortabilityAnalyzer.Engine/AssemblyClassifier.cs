using System.Reflection.PortableExecutable;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Clasifica un ensamblado antes de decompilarlo: gestionado (analizable), nativo (PE sin metadatos CLI),
/// Windows/BCL conocido (no se decompila) o ilegible.
/// </summary>
public sealed class AssemblyClassifier : IAssemblyClassifier
{
    // Prefijos de ensamblados de framework/Windows que no aporta decompilar (configurable / externalizable).
    private static readonly string[] KnownWindowsPrefixes =
    {
        "System.", "Microsoft.CSharp", "Microsoft.VisualBasic", "Microsoft.Win32.",
        "mscorlib", "netstandard", "WindowsBase", "PresentationCore",
        "PresentationFramework", "System.Xaml", "DirectWriteForwarder", "UIAutomation"
    };

    public AssemblyClassification Classify(string assemblyPath)
    {
        var name = Path.GetFileNameWithoutExtension(assemblyPath);

        if (KnownWindowsPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return new AssemblyClassification(assemblyPath, name, AssemblyKind.KnownWindows,
                "En lista de ensamblados Windows/BCL conocidos");

        try
        {
            using var fs = File.OpenRead(assemblyPath);
            using var pe = new PEReader(fs);
            return pe.HasMetadata
                ? new AssemblyClassification(assemblyPath, name, AssemblyKind.Managed)
                : new AssemblyClassification(assemblyPath, name, AssemblyKind.Native, "PE sin metadatos CLI (nativo)");
        }
        catch (Exception ex)
        {
            return new AssemblyClassification(assemblyPath, name, AssemblyKind.Unreadable, ex.Message);
        }
    }
}
