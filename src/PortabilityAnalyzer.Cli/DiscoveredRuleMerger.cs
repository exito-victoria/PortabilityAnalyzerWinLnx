using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortabilityAnalyzer.Engine;
using PortabilityAnalyzer.Rules;
using Serilog;

namespace PortabilityAnalyzer.Cli;

/// <summary>
/// Fusiona las reglas DESCUBIERTAS en el catálogo JSON: genera una regla COMPLETA (marcada para revisar,
/// confianza Baja) por cada API candidata que no esté ya cubierta, respeta el esquema y revalida el
/// catálogo antes de sobrescribirlo (si la fusión no valida, restaura el original).
/// </summary>
internal static class DiscoveredRuleMerger
{
    public static int Merge(string rulesPath, string? schemaPath, IReadOnlyList<RuleCandidate> candidates, ILogger log)
    {
        var original = File.ReadAllText(rulesPath);
        var root = JsonNode.Parse(original)!;
        var reglas = root["reglas"]!.AsArray();

        // Cobertura existente: ids, valores de patrón (y su nombre simple) y namespaces cubiertos.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coveredValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coveredNamespaces = new List<string>();
        foreach (var r in reglas)
        {
            var id = r?["id"]?.GetValue<string>();
            if (id is not null) ids.Add(id);
            var patron = r?["patron"];
            var tipo = patron?["tipo"]?.GetValue<string>();
            var valor = patron?["valor"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(valor))
            {
                coveredValues.Add(valor!);
                coveredValues.Add(SimpleName(valor!));
                if (tipo == "namespace") coveredNamespaces.Add(valor!);
            }
        }

        int added = 0;
        foreach (var c in candidates)
        {
            if (IsCovered(c, coveredValues, coveredNamespaces)) continue;

            var id = UniqueId("DISC-" + Sanitize(c.Value), ids);
            var (altEs, altEn, pasosEs, pasosEn) = Proposal(c.Categoria);

            var rule = new JsonObject
            {
                ["id"] = id,
                ["categoria"] = c.Categoria,
                ["descripcion"] = $"[DESCUBIERTA] {c.Value} es solo-Windows ({(string.IsNullOrEmpty(c.Platforms) ? "SupportedOSPlatform" : c.Platforms)}). REVISAR y completar.",
                ["patron"] = new JsonObject { ["tipo"] = c.PatternKind, ["valor"] = c.Value, ["matchMode"] = c.MatchMode },
                ["severidad"] = "Medio",
                ["esBloqueante"] = false,
                ["alternativaLinux"] = altEs,
                ["esfuerzo"] = new JsonObject { ["optimista"] = 1, ["masProbable"] = 2, ["pesimista"] = 4 },
                ["confianza"] = "Baja",
                ["pasosRemediacion"] = new JsonArray(pasosEs.Select(p => (JsonNode)p).ToArray()),
                ["estrategiaSeparacion"] = "AbstraerPorPlataforma",
                ["notaComun"] = "Regla DESCUBIERTA automáticamente (--discover-rules, SupportedOSPlatform). REVISAR alternativa, pasos, severidad y esfuerzo antes de darla por buena.",
                ["alternativaLinuxEn"] = altEn,
                ["pasosRemediacionEn"] = new JsonArray(pasosEn.Select(p => (JsonNode)p).ToArray()),
                ["notaComunEn"] = "Rule DISCOVERED automatically (--discover-rules, SupportedOSPlatform). REVIEW alternative, steps, severity and effort before accepting it."
            };
            reglas.Add(rule);
            ids.Add(id);
            coveredValues.Add(c.Value);
            coveredValues.Add(SimpleName(c.Value));
            added++;
        }

        if (added == 0) { log.Information("No hay reglas nuevas que añadir (todas las APIs candidatas ya están cubiertas)."); return 0; }

        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var newText = root.ToJsonString(opts);

        // Escribir y REVALIDAR contra el esquema; si no valida, restaurar el original.
        var backup = rulesPath + ".bak";
        File.Copy(rulesPath, backup, overwrite: true);
        File.WriteAllText(rulesPath, newText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            new RuleCatalogJsonLoader(schemaPath).Load(rulesPath);
            File.Delete(backup);
            return added;
        }
        catch (Exception ex)
        {
            File.Copy(backup, rulesPath, overwrite: true);
            File.Delete(backup);
            log.Error("La fusión de reglas descubiertas NO valida contra el esquema; se restauró el catálogo original: {Error}", ex.Message);
            return -1;
        }
    }

    private static bool IsCovered(RuleCandidate c, HashSet<string> coveredValues, List<string> coveredNamespaces)
    {
        if (coveredValues.Contains(c.Value) || coveredValues.Contains(SimpleName(c.Value))) return true;
        if (!string.IsNullOrEmpty(c.Namespace))
            foreach (var ns in coveredNamespaces)
                if (c.Namespace == ns || c.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)) return true;
        return false;
    }

    private static string SimpleName(string value)
    {
        var v = value.Trim();
        var dot = v.LastIndexOf('.');
        return dot >= 0 && dot < v.Length - 1 ? v[(dot + 1)..] : v;
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value) sb.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '-');
        var parts = sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries);
        var s = string.Join("-", parts);
        return string.IsNullOrEmpty(s) ? "RULE" : s;
    }

    private static string UniqueId(string baseId, HashSet<string> ids)
    {
        if (!ids.Contains(baseId)) return baseId;
        for (int i = 2; ; i++) { var cand = $"{baseId}-{i}"; if (!ids.Contains(cand)) return cand; }
    }

    /// <summary>Propuesta ES/EN por categoría (alternativa + pasos), a REVISAR.</summary>
    private static (string AltEs, string AltEn, string[] PasosEs, string[] PasosEn) Proposal(string categoria) => categoria switch
    {
        "Registry" => (
            "Externalizar la configuración (appsettings.json / variables de entorno / almacén de secretos) o aislar el acceso tras una interfaz por SO. REVISAR.",
            "Externalize configuration (appsettings.json / environment variables / secrets store) or isolate the access behind a per-OS interface. REVIEW.",
            new[] { "Identificar el uso concreto.", "Externalizar a configuración portable o aislar tras una interfaz.", "Probar en ambos SO." },
            new[] { "Identify the specific usage.", "Externalize to portable configuration or isolate behind an interface.", "Test on both OSes." }),
        "Cryptography" => (
            "Usar las factorías multiplataforma (RSA.Create()/Aes.Create()/ECDsa.Create()) o cargar claves/certificados desde fichero (PEM/PFX). REVISAR.",
            "Use the cross-platform factories (RSA.Create()/Aes.Create()/ECDsa.Create()) or load keys/certificates from file (PEM/PFX). REVIEW.",
            new[] { "Identificar el algoritmo/tipo usado.", "Sustituir por la factoría multiplataforma equivalente.", "Probar en ambos SO." },
            new[] { "Identify the algorithm/type used.", "Replace with the equivalent cross-platform factory.", "Test on both OSes." }),
        "Identity" => (
            "Identidad multiplataforma: Environment.UserName y, para directorio/AD, System.DirectoryServices.Protocols o Novell.Directory.Ldap (LDAP multiplataforma), tras una interfaz IUserIdentity implementada en el propio código. REVISAR.",
            "Cross-platform identity: Environment.UserName and, for directory/AD, System.DirectoryServices.Protocols or Novell.Directory.Ldap (cross-platform LDAP), behind an IUserIdentity interface implemented in the code itself. REVIEW.",
            new[] { "Localizar el uso de identidad de Windows.", "Definir IUserIdentity y usarla en el núcleo.", "Implementar la versión multiplataforma (Environment.UserName / LDAP) en el propio código." },
            new[] { "Locate the Windows identity usage.", "Define IUserIdentity and use it in the core.", "Implement the cross-platform version (Environment.UserName / LDAP) in the code itself." }),
        "Threading" => (
            "Reemplazar la sincronización con contexto por async/await, IProgress<T> o un SynchronizationContext neutro; el marshalling de UI queda en la capa de presentación. REVISAR.",
            "Replace context synchronization with async/await, IProgress<T> or a neutral SynchronizationContext; UI marshalling stays in the presentation layer. REVIEW.",
            new[] { "Localizar el uso en lógica de negocio.", "Reemplazar por async/await o contexto neutro.", "Probar en ambos SO." },
            new[] { "Locate the usage in business logic.", "Replace with async/await or a neutral context.", "Test on both OSes." }),
        "AssemblyReference" => (
            "La GUI WPF/WinForms es la única excepción: no se migra. Desacoplar la lógica/ViewModels al núcleo multiplataforma; en Linux se construyen solo esas clases y métodos, no la capa gráfica. REVISAR.",
            "The WPF/WinForms GUI is the only exception: it is not migrated. Decouple the logic/ViewModels into the cross-platform core; on Linux only those classes and methods are built, not the graphical layer. REVIEW.",
            new[] { "Separar la lógica de la UI hacia el núcleo multiplataforma.", "Mantener la UI WPF en el ejecutable Windows.", "Validar que el núcleo compila en ambos SO." },
            new[] { "Separate the logic from the UI into the cross-platform core.", "Keep the WPF UI in the Windows executable.", "Validate that the core compiles on both OSes." }),
        "ProcessInvocation" => (
            "Preferir una API gestionada equivalente; si hay que ejecutar el proceso, abstraer la ejecución por SO. REVISAR.",
            "Prefer an equivalent managed API; if the process must run, abstract the execution per OS. REVIEW.",
            new[] { "Localizar la invocación.", "Sustituir por API gestionada o abstraer por SO.", "Probar en ambos SO." },
            new[] { "Locate the invocation.", "Replace with a managed API or abstract per OS.", "Test on both OSes." }),
        "FileSystem" => (
            "Usar Path.Combine / Path.DirectorySeparatorChar y rutas configurables; evitar supuestos de Windows. REVISAR.",
            "Use Path.Combine / Path.DirectorySeparatorChar and configurable paths; avoid Windows assumptions. REVIEW.",
            new[] { "Revisar el manejo de rutas/ficheros.", "Sustituir por APIs portables.", "Probar en ambos SO." },
            new[] { "Review the path/file handling.", "Replace with portable APIs.", "Test on both OSes." }),
        _ => (
            "API solo-Windows (SupportedOSPlatform). Buscar un equivalente multiplataforma o aislar tras una interfaz por SO, guardando con OperatingSystem.IsWindows() si aplica. REVISAR.",
            "Windows-only API (SupportedOSPlatform). Find a cross-platform equivalent or isolate behind a per-OS interface, guarding with OperatingSystem.IsWindows() where applicable. REVIEW.",
            new[] { "Identificar el uso concreto.", "Buscar equivalente multiplataforma o aislar tras una interfaz (seam).", "Guardar con OperatingSystem.IsWindows() donde aplique.", "Probar en ambos SO." },
            new[] { "Identify the specific usage.", "Find a cross-platform equivalent or isolate behind an interface (seam).", "Guard with OperatingSystem.IsWindows() where applicable.", "Test on both OSes." })
    };
}
