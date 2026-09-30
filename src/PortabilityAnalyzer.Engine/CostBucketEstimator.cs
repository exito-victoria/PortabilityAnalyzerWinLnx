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
///
/// Ademas, para no dejar proyectos con esfuerzo CERO cuando SI hay cambios: si un proyecto tiene usos de
/// Windows detectados en el CODIGO FUENTE (Roslyn) pero NO aporta esfuerzo por la via IL (su ensamblado no
/// existe, no esta compilado, o el analisis IL no capta ese uso), se le imputa un esfuerzo SEMILLA por
/// (proyecto, categoria) para que "hay cambios" implique esfuerzo &gt; 0. No se duplica: solo se costean los
/// proyectos que no tienen ya esfuerzo por la via IL.
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

    public IReadOnlyList<BucketEffort> Compute(
        IEnumerable<AssemblyAnalysisResult> results,
        IEnumerable<SourceFinding>? sourceFindings = null,
        ProjectRoles? roles = null)
    {
        var acc = new Dictionary<CostBucket, EffortEstimate>();
        var managed = results.Where(r => r.Classification.Kind == AssemblyKind.Managed).ToList();

        // 1) Esfuerzo por la via IL: una vez por regla dentro de cada ensamblado, factor a los terceros.
        foreach (var r in managed)
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

        // 2) Fallback por CODIGO FUENTE: proyectos con usos Windows en fuente pero sin esfuerzo IL. Se costean
        //    una vez por (proyecto, categoria) con una semilla PERT. Se excluyen los proyectos de terceros
        //    (noModificables) y los que ya aportan esfuerzo por la via IL (para no duplicar).
        if (sourceFindings is not null)
        {
            var projectsWithIlEffort = managed
                .Where(r => r.Effort.Media > 0)
                .Select(r => r.Classification.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var perProjectCategory = sourceFindings
                .Where(f => roles is null || roles.RoleOf(f.Project) != ProjectRole.NoModificable)
                .Where(f => !projectsWithIlEffort.Contains(f.Project))
                .GroupBy(f => (f.Project, f.Categoria), StringTupleComparer)
                .Select(g => g.First());

            foreach (var f in perProjectCategory)
            {
                var (bucket, seed) = SourceSeed(f.Categoria);
                acc[bucket] = (acc.TryGetValue(bucket, out var cur) ? cur : EffortEstimate.Zero).Add(seed);
            }
        }

        // 3) Bucket transversal de Pruebas y CI: porcentaje del esfuerzo de desarrollo (IL + fuente).
        var devTotal = acc.Values.Aggregate(EffortEstimate.Zero, (a, e) => a.Add(e));
        var list = acc.Select(kv => new BucketEffort(kv.Key, kv.Value)).ToList();
        list.Add(new BucketEffort(CostBucket.PruebasCI, devTotal.Scale(_testingFactor)));

        return list.OrderByDescending(b => b.Effort.Media).ToList();
    }

    private static readonly IEqualityComparer<(string, string)> StringTupleComparer =
        new TupleOrdinalIgnoreCase();

    private sealed class TupleOrdinalIgnoreCase : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) a, (string, string) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) t) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(t.Item1 ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(t.Item2 ?? string.Empty));
    }

    /// <summary>
    /// Semilla PERT (orientativa) del esfuerzo de un uso Windows detectado en fuente, por categoria, y su
    /// bucket de coste. Valores por (proyecto, categoria); recalibrar con datos reales. La UI (separar la
    /// capa de presentacion) es la mas costosa; el modelo de hilos del BCL, la mas barata.
    /// </summary>
    private static (CostBucket Bucket, EffortEstimate Seed) SourceSeed(string categoria) => categoria switch
    {
        "UI" => (CostBucket.UILinux, Pert(24, 60, 120)),
        "Database" => (CostBucket.ReemplazoDependencias, Pert(8, 16, 40)),
        "Cryptography" => (CostBucket.ReemplazoDependencias, Pert(8, 20, 48)),
        "EventLog" => (CostBucket.ReemplazoDependencias, Pert(6, 14, 32)),
        "PerformanceCounter" => (CostBucket.ReemplazoDependencias, Pert(6, 14, 32)),
        "Registry" => (CostBucket.SeparacionAbstraccion, Pert(8, 18, 40)),
        "Identity" => (CostBucket.SeparacionAbstraccion, Pert(8, 20, 48)),
        "WMI" => (CostBucket.SeparacionAbstraccion, Pert(10, 24, 56)),
        "COM" => (CostBucket.SeparacionAbstraccion, Pert(12, 28, 64)),
        "ServiceProcess" => (CostBucket.SeparacionAbstraccion, Pert(8, 18, 40)),
        "PInvoke" => (CostBucket.SeparacionAbstraccion, Pert(8, 20, 48)),
        "PlatformAttribute" => (CostBucket.SeparacionAbstraccion, Pert(4, 10, 24)),
        "Threading" => (CostBucket.NucleoComun, Pert(2, 6, 16)),
        _ => (CostBucket.SinClasificar, Pert(4, 12, 32))
    };

    private static EffortEstimate Pert(double o, double m, double p) =>
        new() { Optimista = o, MasProbable = m, Pesimista = p };
}
