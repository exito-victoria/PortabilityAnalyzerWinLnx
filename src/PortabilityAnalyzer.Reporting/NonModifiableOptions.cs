using PortabilityAnalyzer.Core;

namespace PortabilityAnalyzer.Reporting;

/// <summary>Una via viable (con su detalle) para dar salida multiplataforma a un componente no modificable.</summary>
public sealed record ProviderOption(string Titulo, string Detalle, bool Recomendada);

/// <summary>Restriccion y opciones viables detalladas de un componente de terceros no modificable.</summary>
public sealed record NonModifiableProvider(
    string Assembly,
    string Restriccion,
    bool HasNativeWindowsDeps,
    IReadOnlyList<ProviderOption> Opciones);

/// <summary>
/// Para los ensamblados con rol <c>noModificable</c> (ACRA, XMA, Safran...): no se pueden migrar ni
/// modificar (es responsabilidad del proveedor y su esfuerzo no se imputa), pero SI se ofrecen vias
/// viables y detalladas para poder ejecutarlos en el entorno destino. La via recomendada depende de si
/// el componente arrastra dependencias nativas de Windows (P/Invoke): si las tiene, aislarlo en un host
/// Windows out-of-process suele ser lo mas realista.
/// </summary>
public static class NonModifiableOptions
{
    public static IReadOnlyList<NonModifiableProvider> Analyze(AnalysisReport report, Lang lang = Lang.Es)
    {
        if (report.Roles.IsEmpty) return Array.Empty<NonModifiableProvider>();

        var result = new List<NonModifiableProvider>();
        foreach (var a in report.Assemblies.Where(x => x.Classification.Kind == AssemblyKind.Managed))
        {
            if (report.Roles.RoleOf(a.Classification.Name) != ProjectRole.NoModificable) continue;

            var hasNative = a.ConfirmedFindings().Any(f =>
                string.Equals(f.Categoria, "PInvoke", StringComparison.OrdinalIgnoreCase));
            var n = a.Classification.Name;

            result.Add(new NonModifiableProvider(
                n,
                Loc.T(lang,
                    $"{n} es de un proveedor externo: NO podemos migrarlo ni modificarlo (es responsabilidad del proveedor) y su esfuerzo NO se imputa a nuestro total. Aun así, estas son las vías viables para poder ejecutarlo en el entorno destino:",
                    $"{n} comes from an external vendor: we CANNOT migrate or modify it (that is the vendor's responsibility) and its effort is NOT charged to our total. Even so, these are the viable ways to run it on the target environment:"),
                hasNative,
                BuildOptions(n, hasNative, lang)));
        }
        return result;
    }

    private static IReadOnlyList<ProviderOption> BuildOptions(string name, bool hasNative, Lang lang)
    {
        return new List<ProviderOption>
        {
            new(Loc.T(lang, "Versión multiplataforma del proveedor (preferente si existe)",
                            "Cross-platform version from the vendor (preferred if available)"),
                Loc.T(lang,
                    $"Solicitar/gestionar con el proveedor de {name} una build .NET multiplataforma (o nativa para el SO destino) del componente y validar que el contrato de API se mantiene. Es la vía más limpia; el coste recae en el proveedor. Documentar versión, licencia y soporte.",
                    $"Request/manage with {name}'s vendor a cross-platform .NET build (or a native one for the target OS) and validate that the API contract is preserved. It is the cleanest path; the cost falls on the vendor. Document version, license and support."),
                Recomendada: !hasNative),

            new(Loc.T(lang, "Aislar en un host Windows out-of-process + contrato IPC/REST (recomendado si no hay build multiplataforma)",
                            "Isolate in an out-of-process Windows host + IPC/REST contract (recommended if there is no cross-platform build)"),
                Loc.T(lang,
                    $"Mantener {name} ejecutándose en un proceso/servicio Windows (equipo dedicado o contenedor Windows) y que la parte portable de la aplicación hable con él mediante un contrato bien definido (REST/gRPC o cola de mensajes). La aplicación portable NO enlaza el ensamblado: solo consume el contrato, con lo que TODO lo demás puede ser multiplataforma. Coste: diseñar el contrato y operar/monitorizar el host Windows.",
                    $"Keep {name} running in a Windows process/service (a dedicated machine or a Windows container) and have the portable part of the application talk to it through a well-defined contract (REST/gRPC or a message queue). The portable app does NOT link the assembly: it only consumes the contract, so EVERYTHING else can be cross-platform. Cost: design the contract and operate/monitor the Windows host."),
                Recomendada: hasNative),

            new(Loc.T(lang, "Capa de compatibilidad (Wine u similar)", "Compatibility layer (Wine or similar)"),
                Loc.T(lang,
                    $"Ejecutar el binario Windows de {name} bajo una capa de compatibilidad en el entorno destino. Solo si el proveedor no ofrece alternativa y el componente no usa APIs no soportadas por la capa; requiere pruebas de compatibilidad exhaustivas y el soporte del proveedor no está garantizado. Considerar como último recurso.",
                    $"Run {name}'s Windows binary under a compatibility layer on the target environment. Only if the vendor offers no alternative and the component does not use APIs unsupported by the layer; it requires thorough compatibility testing and vendor support is not guaranteed. Consider it a last resort."),
                Recomendada: false),

            new(Loc.T(lang, "Sustitución por un equivalente multiplataforma", "Replacement with a cross-platform equivalent"),
                Loc.T(lang,
                    $"Reemplazar la funcionalidad de {name} por otra librería/servicio portable equivalente y reescribir la integración. Elimina por completo la atadura a Windows, pero es la opción de mayor coste y hay que verificar paridad funcional.",
                    $"Replace {name}'s functionality with another equivalent portable library/service and rewrite the integration. It fully removes the Windows dependency, but it is the costliest option and functional parity must be verified."),
                Recomendada: false)
        };
    }
}
