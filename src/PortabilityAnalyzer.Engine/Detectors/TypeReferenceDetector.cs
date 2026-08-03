using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine.Detectors;

/// <summary>
/// Detecta uso de tipos/namespaces solo-Windows a partir de las referencias de tipo del ensamblado.
/// Los patrones de tipo (equals) y de namespace (startsWith) se cotejan contra el nombre completo del tipo referenciado.
/// </summary>
public sealed class TypeReferenceDetector : IAssemblyDetector
{
    public IReadOnlyCollection<PatternKind> Handles { get; } = new[] { PatternKind.Type, PatternKind.Namespace };

    public IEnumerable<Finding> Detect(AssemblyContext context, IReadOnlyList<PortabilityRule> rules)
    {
        foreach (var module in context.Assembly.Modules)
        foreach (var typeRef in module.GetTypeReferences())
        {
            var fullName = typeRef.FullName;
            foreach (var rule in rules)
            {
                if (PatternMatcher.Matches(rule.Patron, fullName))
                    yield return FindingFactory.From(rule, context.Name, type: fullName, evidencia: fullName);
            }
        }
    }
}
