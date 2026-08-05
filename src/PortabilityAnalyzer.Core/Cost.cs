namespace PortabilityAnalyzer.Core;

/// <summary>
/// Bucket (categoria) de coste del trabajo de hacer la aplicacion multiplataforma en .NET 8.
/// Los cuatro primeros son trabajo de desarrollo (se derivan de la estrategia de separacion de cada
/// regla); <see cref="PruebasCI"/> es transversal (un porcentaje del esfuerzo de desarrollo).
/// </summary>
public enum CostBucket
{
    NucleoComun,
    SeparacionAbstraccion,
    ReemplazoDependencias,
    UILinux,
    PruebasCI,
    SinClasificar
}

/// <summary>Esfuerzo agregado de un bucket de coste.</summary>
public sealed record BucketEffort(CostBucket Bucket, EffortEstimate Effort);

/// <summary>Mapeo y textos de los buckets de coste.</summary>
public static class CostBuckets
{
    /// <summary>Bucket de desarrollo correspondiente a la estrategia de separacion de una regla.</summary>
    public static CostBucket For(SeparationStrategy? s) => s switch
    {
        SeparationStrategy.RedisenoUI => CostBucket.UILinux,
        SeparationStrategy.AbstraerPorPlataforma => CostBucket.SeparacionAbstraccion,
        SeparationStrategy.ReemplazarDependencia => CostBucket.ReemplazoDependencias,
        SeparationStrategy.Comun => CostBucket.NucleoComun,
        _ => CostBucket.SinClasificar
    };

    /// <summary>Nombre legible del bucket.</summary>
    public static string Text(CostBucket b) => b switch
    {
        CostBucket.NucleoComun => "Adaptacion a nucleo comun",
        CostBucket.SeparacionAbstraccion => "Separacion por plataforma (abstraccion)",
        CostBucket.ReemplazoDependencias => "Reemplazo de dependencias",
        CostBucket.UILinux => "UI Linux (Avalonia)",
        CostBucket.PruebasCI => "Pruebas y CI en ambos SO",
        _ => "Sin clasificar"
    };
}
