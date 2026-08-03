using Mono.Cecil;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine.Detectors;

/// <summary>
/// Detecta llamadas a APIs concretas recorriendo el IL. El candidato tiene el formato
/// "Namespace.Tipo::Metodo" (por ejemplo "System.Console::Beep", "System.Threading.Mutex::.ctor").
/// TODO: para reglas de constructores "con nombre" (mutex/semaforo con nombre), refinar inspeccionando
/// los argumentos de la llamada (ldstr previo o firma con parametro string) y reducir falsos positivos.
/// </summary>
public sealed class ApiCallDetector : IAssemblyDetector
{
    public IReadOnlyCollection<PatternKind> Handles { get; } = new[] { PatternKind.ApiCall, PatternKind.Method };

    public IEnumerable<Finding> Detect(AssemblyContext context, IReadOnlyList<PortabilityRule> rules)
    {
        foreach (var type in context.AllTypes())
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;

            foreach (var instr in method.Body.Instructions)
            {
                if (instr.Operand is not MethodReference callee)
                    continue;

                var candidate = $"{callee.DeclaringType.FullName}::{callee.Name}";
                foreach (var rule in rules)
                {
                    if (PatternMatcher.Matches(rule.Patron, candidate))
                        yield return FindingFactory.From(rule, context.Name, type.FullName, method.Name, instr.Offset, candidate);
                }
            }
        }
    }
}
