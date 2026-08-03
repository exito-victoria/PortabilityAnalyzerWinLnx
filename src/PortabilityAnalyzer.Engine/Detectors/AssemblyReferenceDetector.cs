using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine.Detectors;

/// <summary>Detecta referencias a ensamblados solo compatibles con Windows (WPF, WinForms, WMI, Oracle unmanaged...).</summary>
public sealed class AssemblyReferenceDetector : IAssemblyDetector
{
    public IReadOnlyCollection<PatternKind> Handles { get; } = new[] { PatternKind.AssemblyReference };

    public IEnumerable<Finding> Detect(AssemblyContext context, IReadOnlyList<PortabilityRule> rules)
    {
        foreach (var module in context.Assembly.Modules)
        foreach (var reference in module.AssemblyReferences)
        foreach (var rule in rules)
        {
            if (PatternMatcher.Matches(rule.Patron, reference.Name))
                yield return FindingFactory.From(rule, context.Name, evidencia: reference.FullName);
        }
    }
}
