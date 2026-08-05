using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Descompone el esfuerzo total en buckets de coste multiplataforma. Los buckets de desarrollo se
/// derivan de la estrategia de separacion de cada regla; el bucket de Pruebas y CI es transversal y se
/// calcula como un porcentaje del esfuerzo de desarrollo (factor documentado en el informe).
///
/// Reproduce la misma logica que <see cref="PertEffortEstimator"/> (esfuerzo una vez por regla dentro
/// de cada ensamblado y factor de incertidumbre a los terceros), de modo que la suma de los buckets de
/// desarrollo coincide con el esfuerzo total del informe.
/// </summary>
public sealed class CostBucketEstimator
{
    private readonly double _thirdPartyFactor;
    private readonly double _testingFactor;

    public CostBucketEstimator(double thirdPartyFactor = 1.5, double testingFactor = 0.25)
    {
        _thirdPartyFactor = thirdPartyFactor;
        _testingFactor = testingFactor;
    }

    public IReadOnlyList<BucketEffort> Compute(IEnumerable<AssemblyAnalysisResult> results)
    {
        var acc = new Dictionary<CostBucket, EffortEstimate>();

        foreach (var r in results.Where(r => r.Classification.Kind == AssemblyKind.Managed))
        {
            var perRule = r.ConfirmedFindings()
                .GroupBy(f => f.RuleId)
                .Select(g => g.First());

            foreach (var f in perRule)
            {
                var bucket = CostBuckets.For(f.EstrategiaSeparacion);
                var effort = r.IsThirdParty ? f.Esfuerzo.Scale(_thirdPartyFactor) : f.Esfuerzo;
                acc[bucket] = (acc.TryGetValue(bucket, out var cur) ? cur : EffortEstimate.Zero).Add(effort);
            }
        }

        // Bucket transversal de Pruebas y CI: porcentaje del esfuerzo de desarrollo.
        var devTotal = acc.Values.Aggregate(EffortEstimate.Zero, (a, e) => a.Add(e));
        var list = acc.Select(kv => new BucketEffort(kv.Key, kv.Value)).ToList();
        list.Add(new BucketEffort(CostBucket.PruebasCI, devTotal.Scale(_testingFactor)));

        return list.OrderByDescending(b => b.Effort.Media).ToList();
    }
}
