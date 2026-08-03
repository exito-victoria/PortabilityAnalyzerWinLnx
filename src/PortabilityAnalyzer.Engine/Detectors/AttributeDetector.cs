using Mono.Cecil;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine.Detectors;

/// <summary>
/// Detecta atributos de plataforma o de interop (p. ej. [SupportedOSPlatform("windows")], [ComImport], [CoClass]).
/// Si la regla define <see cref="RulePattern.ArgumentoContiene"/>, exige que algun argumento del atributo lo contenga.
/// </summary>
public sealed class AttributeDetector : IAssemblyDetector
{
    public IReadOnlyCollection<PatternKind> Handles { get; } = new[] { PatternKind.Attribute };

    public IEnumerable<Finding> Detect(AssemblyContext context, IReadOnlyList<PortabilityRule> rules)
    {
        foreach (var f in Match(context.Assembly.CustomAttributes, rules, context.Name, null, null))
            yield return f;

        foreach (var type in context.AllTypes())
        {
            foreach (var f in Match(type.CustomAttributes, rules, context.Name, type.FullName, null))
                yield return f;

            foreach (var method in type.Methods)
                foreach (var f in Match(method.CustomAttributes, rules, context.Name, type.FullName, method.Name))
                    yield return f;
        }
    }

    private static IEnumerable<Finding> Match(
        IEnumerable<CustomAttribute> attributes,
        IReadOnlyList<PortabilityRule> rules,
        string assembly, string? type, string? method)
    {
        foreach (var attr in attributes)
        {
            var fullName = attr.AttributeType.FullName;
            foreach (var rule in rules)
            {
                if (!PatternMatcher.Matches(rule.Patron, fullName))
                    continue;

                if (!string.IsNullOrEmpty(rule.Patron.ArgumentoContiene) &&
                    !ArgumentContains(attr, rule.Patron.ArgumentoContiene!))
                    continue;

                yield return FindingFactory.From(rule, assembly, type, method, evidencia: fullName);
            }
        }
    }

    private static bool ArgumentContains(CustomAttribute attr, string needle)
    {
        if (!attr.HasConstructorArguments) return false;
        foreach (var arg in attr.ConstructorArguments)
            if (arg.Value is string s && s.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
