using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Detector enchufable. Cada detector atiende uno o varios tipos de patron (<see cref="PatternKind"/>)
/// sobre un ensamblado gestionado ya cargado con Mono.Cecil, y emite <see cref="Finding"/> con
/// trazabilidad completa (ensamblado -> tipo -> metodo -> offset IL).
/// </summary>
public interface IAssemblyDetector
{
    /// <summary>Tipos de patron que este detector sabe evaluar.</summary>
    IReadOnlyCollection<PatternKind> Handles { get; }

    /// <summary>Aplica las reglas indicadas (todas de un tipo que este detector atiende) y devuelve los hallazgos.</summary>
    IEnumerable<Finding> Detect(AssemblyContext context, IReadOnlyList<PortabilityRule> rules);
}
