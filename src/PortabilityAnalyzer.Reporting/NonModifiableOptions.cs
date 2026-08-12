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
    public static IReadOnlyList<NonModifiableProvider> Analyze(AnalysisReport report)
    {
        if (report.Roles.IsEmpty) return Array.Empty<NonModifiableProvider>();

        var result = new List<NonModifiableProvider>();
        foreach (var a in report.Assemblies.Where(x => x.Classification.Kind == AssemblyKind.Managed))
        {
            if (report.Roles.RoleOf(a.Classification.Name) != ProjectRole.NoModificable) continue;

            var hasNative = a.ConfirmedFindings().Any(f =>
                string.Equals(f.Categoria, "PInvoke", StringComparison.OrdinalIgnoreCase));

            result.Add(new NonModifiableProvider(
                a.Classification.Name,
                $"{a.Classification.Name} es de un proveedor externo: NO podemos migrarlo ni modificarlo " +
                "(es responsabilidad del proveedor) y su esfuerzo NO se imputa a nuestro total. Aun así, " +
                "estas son las vías viables para poder ejecutarlo en el entorno destino:",
                hasNative,
                BuildOptions(a.Classification.Name, hasNative)));
        }
        return result;
    }

    private static IReadOnlyList<ProviderOption> BuildOptions(string name, bool hasNative)
    {
        return new List<ProviderOption>
        {
            new("Versión multiplataforma del proveedor (preferente si existe)",
                $"Solicitar/gestionar con el proveedor de {name} una build .NET multiplataforma (o nativa para el SO destino) del componente y validar que el contrato de API se mantiene. Es la vía más limpia; el coste recae en el proveedor. Documentar versión, licencia y soporte.",
                Recomendada: !hasNative),

            new("Aislar en un host Windows out-of-process + contrato IPC/REST (recomendado si no hay build multiplataforma)",
                $"Mantener {name} ejecutándose en un proceso/servicio Windows (equipo dedicado o contenedor Windows) y que la parte portable de la aplicación hable con él mediante un contrato bien definido (REST/gRPC o cola de mensajes). La aplicación portable NO enlaza el ensamblado: solo consume el contrato, con lo que TODO lo demás puede ser multiplataforma. Coste: diseñar el contrato y operar/monitorizar el host Windows.",
                Recomendada: hasNative),

            new("Capa de compatibilidad (Wine u similar)",
                $"Ejecutar el binario Windows de {name} bajo una capa de compatibilidad en el entorno destino. Solo si el proveedor no ofrece alternativa y el componente no usa APIs no soportadas por la capa; requiere pruebas de compatibilidad exhaustivas y el soporte del proveedor no está garantizado. Considerar como último recurso.",
                Recomendada: false),

            new("Sustitución por un equivalente multiplataforma",
                $"Reemplazar la funcionalidad de {name} por otra librería/servicio portable equivalente y reescribir la integración. Elimina por completo la atadura a Windows, pero es la opción de mayor coste y hay que verificar paridad funcional.",
                Recomendada: false)
        };
    }
}
