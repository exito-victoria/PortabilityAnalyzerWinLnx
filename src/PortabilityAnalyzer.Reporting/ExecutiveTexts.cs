using System.Globalization;
using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>
/// Textos localizados del informe ejecutivo. Hay dos instancias listas: <see cref="Spanish"/> e
/// <see cref="English"/>. Permite generar el mismo informe en varios idiomas sin duplicar el exportador.
/// </summary>
public sealed class ExecTexts
{
    public required CultureInfo Culture { get; init; }
    public required string FileSuffix { get; init; } // sufijo para el nombre de fichero (p. ej. "" o "_EN")

    // Portada
    public required string ReportTitle { get; init; }
    public required Func<string, string> Subtitle { get; init; }      // projectName
    public required string GeneratedLabel { get; init; }

    // Resumen ejecutivo
    public required string HSummary { get; init; }
    public required Func<int, string> Scope { get; init; }            // nº proyectos
    public required Func<string, string> SummaryPara1 { get; init; }  // scope
    public required Func<string, string, string, int, string> SummaryPara2 { get; init; } // media, opt, pes, bloqueantes

    // Cifras clave
    public required string HKeyFigures { get; init; }
    public required string ColMetric { get; init; }
    public required string ColValue { get; init; }
    public required string KFProjects { get; init; }
    public required string KFBlockers { get; init; }
    public required string KFFiles { get; init; }
    public required string KFClasses { get; init; }
    public required string KFOptimistic { get; init; }
    public required string KFMostLikely { get; init; }
    public required string KFPessimistic { get; init; }

    // Estimación por proyecto
    public required string HEstimate { get; init; }
    public required string EstimateIntro { get; init; }
    public required string ClsProject { get; init; }
    public required string ClsThirdParty { get; init; }
    public required string OrigOwn { get; init; }
    public required string OrigOwnObligatorio { get; init; }
    public required string OrigOwnDivisible { get; init; }
    public required Func<string, string> OrigNoMod { get; init; }     // autor
    public required string NotCharged { get; init; }
    public required string TotalRow { get; init; }
    public required string ColProjectDll { get; init; }
    public required string ColClass { get; init; }
    public required string ColAuthorRole { get; init; }
    public required string ColSev { get; init; }
    public required string ColBlk { get; init; }
    public required string ColEffort { get; init; }
    public required string UnknownThirdParty { get; init; }
    public required Func<Severity, string> Sev { get; init; }

    // Coste por bloque
    public required string HCost { get; init; }
    public required string CostIntro { get; init; }
    public required string ColBlock { get; init; }
    public required string ColOptimistic { get; init; }
    public required string ColMostLikely { get; init; }
    public required string ColPct { get; init; }
    public required string CostTotal { get; init; }
    public required Func<CostBucket, string> Bucket { get; init; }

    // Hallazgos principales
    public required string HFindings { get; init; }
    public required string FindingsIntro { get; init; }
    public required string ColDependencyType { get; init; }
    public required string ColUses { get; init; }
    public required Func<string, string> Category { get; init; }

    // Restricciones
    public required string HConstraints { get; init; }
    public required string ConstraintsIntro { get; init; }
    public required string AuthorWord { get; init; }
    public required string ConstraintsOptions { get; init; }
    public required string UnknownShort { get; init; }

    // Recomendación
    public required string HRecommendation { get; init; }
    public required string RecoPara1 { get; init; }
    public required string RecoPara2 { get; init; }

    // -------------------------------------------------------------------------------------------

    public static readonly ExecTexts Spanish = new()
    {
        Culture = CultureInfo.GetCultureInfo("es-ES"),
        FileSuffix = "",
        ReportTitle = "Informe ejecutivo",
        Subtitle = p => $"Análisis de portabilidad multiplataforma (.NET 8) — {p}",
        GeneratedLabel = "Generado",

        HSummary = "Resumen ejecutivo",
        Scope = n => n == 1 ? "el proyecto analizado" : $"los {n} proyectos/ensamblados analizados",
        SummaryPara1 = scope =>
            $"Este informe resume el trabajo necesario para hacer multiplataforma (portable a .NET 8) {scope}. " +
            "El enfoque es portable-first: llevar todo lo posible a un núcleo portable y aislar únicamente lo que " +
            "depende obligatoriamente de Windows, dejándolo preparado para que otro equipo aporte la parte no-Windows.",
        SummaryPara2 = (media, opt, pes, blk) =>
            $"Esfuerzo estimado total: {media} horas-persona como valor más probable (rango {opt}–{pes} h), con {blk} " +
            "hallazgo(s) bloqueante(s) a resolver en nuestros proyectos. Las cifras son una estimación de planificación " +
            "y deben calibrarse con datos reales del equipo.",

        HKeyFigures = "Cifras clave",
        ColMetric = "Métrica",
        ColValue = "Valor",
        KFProjects = "Proyectos/ensamblados analizados",
        KFBlockers = "Hallazgos bloqueantes (nuestros proyectos)",
        KFFiles = "Ficheros afectados (código fuente)",
        KFClasses = "Clases afectadas",
        KFOptimistic = "Esfuerzo optimista (h)",
        KFMostLikely = "Esfuerzo más probable (h)",
        KFPessimistic = "Esfuerzo pesimista (h)",

        HEstimate = "Estimación por proyecto",
        EstimateIntro = "Esfuerzo estimado (horas) diferenciando los PROYECTOS propios (código nuestro) de las DLL de " +
                        "TERCEROS (de un autor/proveedor externo). Los componentes de terceros no modificables no imputan " +
                        "esfuerzo a nuestro total: su adaptación corresponde a su autor.",
        ClsProject = "Proyecto",
        ClsThirdParty = "DLL de terceros",
        OrigOwn = "Propio",
        OrigOwnObligatorio = "Propio (obligatorio multiplataforma)",
        OrigOwnDivisible = "Propio (divisible por UI)",
        OrigNoMod = a => $"{a} (no modificable)",
        NotCharged = "no imputado",
        TotalRow = "TOTAL (imputado)",
        ColProjectDll = "Proyecto / DLL",
        ColClass = "Clase",
        ColAuthorRole = "Autor / rol",
        ColSev = "Sev.",
        ColBlk = "Bloq.",
        ColEffort = "Esfuerzo O / M / P (h)",
        UnknownThirdParty = "Tercero (autor desconocido)",
        Sev = s => s.ToString(),

        HCost = "Coste por bloque de trabajo",
        CostIntro = "Se muestra primero la estimación optimista (escenario favorable, mejor caso) y después la más " +
                    "probable (media). El % es el peso de cada bloque sobre el total.",
        ColBlock = "Bloque",
        ColOptimistic = "Optimista (h)",
        ColMostLikely = "Media (h)",
        ColPct = "%",
        CostTotal = "Total (con Pruebas y CI)",
        Bucket = CostBuckets.Text,

        HFindings = "Hallazgos principales",
        FindingsIntro = "Tipos de dependencia de Windows más frecuentes (a resolver o aislar):",
        ColDependencyType = "Tipo de dependencia",
        ColUses = "Nº de usos",
        Category = CategoryEs,

        HConstraints = "Restricciones (componentes de terceros)",
        ConstraintsIntro = "Los siguientes componentes son de proveedores externos y NO se pueden migrar ni modificar por " +
                           "nuestra parte (es responsabilidad de su autor); su esfuerzo no se imputa a nuestro total:",
        AuthorWord = "autor",
        ConstraintsOptions = "Para ejecutarlos en el entorno destino existen vías viables (versión multiplataforma del " +
                             "proveedor, aislarlos en un host Windows con un contrato de servicio, capa de compatibilidad o " +
                             "sustitución), detalladas en el informe general.",
        UnknownShort = "desconocido",

        HRecommendation = "Recomendación",
        RecoPara1 = "Adoptar una arquitectura portable-first: un núcleo .NET 8 multiplataforma lo más grande posible, una " +
                    "capa de interfaces (el «seam») para lo que dependa del sistema operativo, y una única pieza aislada con " +
                    "lo obligatoriamente Windows. Priorizar la resolución de los puntos bloqueantes y de los proyectos " +
                    "marcados como obligatorios. La implementación de la plataforma no-Windows queda preparada tras las " +
                    "interfaces, para que otro equipo la desarrolle.",
        RecoPara2 = "Un seam (o punto de unión/corte) no es propiamente una capa física de la aplicación, sino un lugar en " +
                    "el código donde puedes alterar el comportamiento del programa sin modificar el código fuente de ese " +
                    "lugar, el punto de extensión —una interfaz— por el que el núcleo portable llama a una capacidad que " +
                    "depende del sistema operativo, sin conocer su implementación. Cada plataforma (Windows, y en el futuro " +
                    "otras) aporta su propia implementación de esa interfaz; así el núcleo se mantiene portable y lo " +
                    "específico de cada SO queda encapsulado y sustituible.",
    };

    public static readonly ExecTexts English = new()
    {
        Culture = CultureInfo.InvariantCulture,
        FileSuffix = "_EN",
        ReportTitle = "Executive summary",
        Subtitle = p => $"Cross-platform portability analysis (.NET 8) — {p}",
        GeneratedLabel = "Generated",

        HSummary = "Executive summary",
        Scope = n => n == 1 ? "the analyzed project" : $"the {n} analyzed projects/assemblies",
        SummaryPara1 = scope =>
            $"This report summarizes the work required to make {scope} cross-platform (portable to .NET 8). " +
            "The approach is portable-first: move as much as possible to a portable core and isolate only what strictly " +
            "depends on Windows, leaving it ready for another team to provide the non-Windows part.",
        SummaryPara2 = (media, opt, pes, blk) =>
            $"Total estimated effort: {media} person-hours as the most likely value (range {opt}–{pes} h), with {blk} " +
            "blocking finding(s) to resolve in our projects. Figures are a planning estimate and should be calibrated with " +
            "the team's real data.",

        HKeyFigures = "Key figures",
        ColMetric = "Metric",
        ColValue = "Value",
        KFProjects = "Projects/assemblies analyzed",
        KFBlockers = "Blocking findings (our projects)",
        KFFiles = "Affected files (source code)",
        KFClasses = "Affected classes",
        KFOptimistic = "Optimistic effort (h)",
        KFMostLikely = "Most likely effort (h)",
        KFPessimistic = "Pessimistic effort (h)",

        HEstimate = "Estimate per project",
        EstimateIntro = "Estimated effort (hours), distinguishing our own PROJECTS (our code) from THIRD-PARTY DLLs " +
                        "(from an external author/vendor). Non-modifiable third-party components are not charged to our " +
                        "total: adapting them is the author's responsibility.",
        ClsProject = "Project",
        ClsThirdParty = "Third-party DLL",
        OrigOwn = "Own",
        OrigOwnObligatorio = "Own (must be cross-platform)",
        OrigOwnDivisible = "Own (splittable by UI)",
        OrigNoMod = a => $"{a} (non-modifiable)",
        NotCharged = "not charged",
        TotalRow = "TOTAL (charged)",
        ColProjectDll = "Project / DLL",
        ColClass = "Kind",
        ColAuthorRole = "Author / role",
        ColSev = "Sev.",
        ColBlk = "Blk.",
        ColEffort = "Effort O / M / P (h)",
        UnknownThirdParty = "Third party (unknown author)",
        Sev = SeverityEn,

        HCost = "Cost by work block",
        CostIntro = "The optimistic estimate (favorable, best case) is shown first, then the most likely (mean). The % is " +
                    "each block's weight over the total.",
        ColBlock = "Block",
        ColOptimistic = "Optimistic (h)",
        ColMostLikely = "Most likely (h)",
        ColPct = "%",
        CostTotal = "Total (with Testing & CI)",
        Bucket = BucketEn,

        HFindings = "Main findings",
        FindingsIntro = "Most frequent Windows dependency types (to resolve or isolate):",
        ColDependencyType = "Dependency type",
        ColUses = "Uses",
        Category = CategoryEn,

        HConstraints = "Constraints (third-party components)",
        ConstraintsIntro = "The following components come from external vendors and CANNOT be migrated or modified by us " +
                           "(that is the author's responsibility); their effort is not charged to our total:",
        AuthorWord = "author",
        ConstraintsOptions = "To run them on the target environment there are viable options (a cross-platform version " +
                             "from the vendor, isolating them in a Windows host behind a service contract, a compatibility " +
                             "layer, or replacement), detailed in the general report.",
        UnknownShort = "unknown",

        HRecommendation = "Recommendation",
        RecoPara1 = "Adopt a portable-first architecture: a .NET 8 cross-platform core as large as possible, an interface " +
                    "layer (the \"seam\") for anything OS-dependent, and a single isolated piece with what strictly requires " +
                    "Windows. Prioritize resolving the blocking points and the projects marked as mandatory. The non-Windows " +
                    "platform implementation is left ready behind the interfaces for another team to develop.",
        RecoPara2 = "A seam is not really a physical layer of the application, but a place in the code where you can alter " +
                    "the program's behavior without modifying the source code at that place: the extension point —an " +
                    "interface— through which the portable core calls an OS-dependent capability without knowing its " +
                    "implementation. Each platform (Windows, and others in the future) provides its own implementation of " +
                    "that interface; thus the core stays portable and the OS-specific parts remain encapsulated and replaceable.",
    };

    private static string CategoryEs(string cat) => cat switch
    {
        "UI" => "Interfaz de usuario (WPF/WinForms)",
        "Database" => "Acceso a datos (Oracle/SQL nativo)",
        "Registry" => "Registro de Windows",
        "Identity" => "Identidad / autenticación de Windows",
        "PInvoke" => "Llamadas nativas (P/Invoke)",
        "COM" => "Componentes COM",
        "WMI" => "WMI (información del sistema)",
        "Cryptography" => "Criptografía (DPAPI/CNG)",
        "EventLog" => "Registro de eventos de Windows",
        "ServiceProcess" => "Servicios de Windows",
        "Threading" => "Sincronización / hilos de UI",
        _ => cat
    };

    private static string CategoryEn(string cat) => cat switch
    {
        "UI" => "User interface (WPF/WinForms)",
        "Database" => "Data access (native Oracle/SQL)",
        "Registry" => "Windows Registry",
        "Identity" => "Windows identity / authentication",
        "PInvoke" => "Native calls (P/Invoke)",
        "COM" => "COM components",
        "WMI" => "WMI (system information)",
        "Cryptography" => "Cryptography (DPAPI/CNG)",
        "EventLog" => "Windows Event Log",
        "ServiceProcess" => "Windows Services",
        "Threading" => "Synchronization / UI threads",
        _ => cat
    };

    private static string SeverityEn(Severity s) => s switch
    {
        Severity.Bajo => "Low",
        Severity.Medio => "Medium",
        Severity.Alto => "High",
        Severity.Bloqueante => "Blocking",
        _ => "Info"
    };

    private static string BucketEn(CostBucket b) => b switch
    {
        CostBucket.NucleoComun => "Common-core adaptation",
        CostBucket.SeparacionAbstraccion => "Platform separation (abstraction)",
        CostBucket.ReemplazoDependencias => "Dependency replacement",
        CostBucket.UILinux => "UI separation (portable presentation layer)",
        CostBucket.PruebasCI => "Cross-platform testing & CI",
        _ => "Unclassified"
    };
}
