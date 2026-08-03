using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine.Detectors;

/// <summary>Detecta llamadas P/Invoke (DllImport) y las coteja con las DLL nativas Windows del catalogo.</summary>
public sealed class PInvokeDetector : IAssemblyDetector
{
    public IReadOnlyCollection<PatternKind> Handles { get; } = new[] { PatternKind.PInvokeDll };

    public IEnumerable<Finding> Detect(AssemblyContext context, IReadOnlyList<PortabilityRule> rules)
    {
        foreach (var type in context.AllTypes())
        foreach (var method in type.Methods)
        {
            if (!method.IsPInvokeImpl || method.PInvokeInfo?.Module is null)
                continue;

            var dll = method.PInvokeInfo.Module.Name ?? string.Empty;
            foreach (var rule in rules)
            {
                if (PatternMatcher.Matches(rule.Patron, dll))
                    yield return FindingFactory.From(rule, context.Name, type.FullName, method.Name, evidencia: dll);
            }
        }
    }
}
