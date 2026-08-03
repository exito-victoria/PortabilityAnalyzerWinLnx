using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Estimador de esfuerzo. Cuenta el esfuerzo UNA VEZ por regla dentro de cada ensamblado
/// (no multiplica por cada ocurrencia) y aplica un factor de incertidumbre a los ensamblados de terceros.
/// </summary>
public sealed class PertEffortEstimator : IEffortEstimator
{
    private readonly double _thirdPartyFactor;

    public PertEffortEstimator(double thirdPartyFactor = 1.5) => _thirdPartyFactor = thirdPartyFactor;

    public EffortEstimate Aggregate(IEnumerable<Finding> findings, bool isThirdParty)
    {
        var perRule = findings
            .GroupBy(f => f.RuleId)
            .Select(g => g.First().Esfuerzo);

        var total = perRule.Aggregate(EffortEstimate.Zero, (acc, e) => acc.Add(e));
        return isThirdParty ? total.Scale(_thirdPartyFactor) : total;
    }
}
