using Mono.Cecil.Cil;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine.Detectors;

/// <summary>
/// Detecta literales de cadena (instrucciones ldstr) que denotan rutas, comandos o ejecutables de Windows.
/// Suele ser de confianza baja: los hallazgos deben revisarse manualmente.
/// </summary>
public sealed class StringLiteralDetector : IAssemblyDetector
{
    public IReadOnlyCollection<PatternKind> Handles { get; } = new[] { PatternKind.StringLiteral };

    public IEnumerable<Finding> Detect(AssemblyContext context, IReadOnlyList<PortabilityRule> rules)
    {
        foreach (var type in context.AllTypes())
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;

            foreach (var instr in method.Body.Instructions)
            {
                if (instr.OpCode.Code != Code.Ldstr || instr.Operand is not string literal)
                    continue;

                foreach (var rule in rules)
                {
                    if (PatternMatcher.Matches(rule.Patron, literal))
                        yield return FindingFactory.From(rule, context.Name, type.FullName, method.Name, instr.Offset, Truncate(literal));
                }
            }
        }
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "...";
}
