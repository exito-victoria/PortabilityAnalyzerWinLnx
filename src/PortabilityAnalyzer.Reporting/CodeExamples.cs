namespace PortabilityAnalyzer.Reporting;

/// <summary>Ejemplo de codigo para hacer PORTABLE una categoria de dependencia hoy atada a Windows.</summary>
public sealed record CodeExample(string Categoria, string Titulo, string Codigo, string Nota);

/// <summary>
/// Ejemplos de patron PORTABLE-FIRST por categoria de dependencia: usar la API gestionada portable cuando
/// existe, o AISLAR lo que hoy exige Windows tras una interfaz / compilacion condicional
/// (<c>OperatingSystem.IsWindows()</c> / <c>#if</c>), dejando el hueco preparado. NO se desarrolla ni
/// prescribe la implementacion de otra plataforma: eso queda a cargo de otro equipo. Se muestran en el
/// informe (apendice) solo para las categorias que aparecen.
/// </summary>
public static class CodeExamples
{
    private static readonly CodeExample[] All =
    {
        new("Registry", "Registro de Windows -> configuración portable",
            """
            // Solo Windows:
            using Microsoft.Win32;
            var ruta = (string?)Registry.GetValue(@"HKLM\SOFTWARE\MiApp", "Ruta", null);

            // Portable: externalizar a configuracion (appsettings.json / variables de entorno)
            IConfiguration cfg = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            var ruta = cfg["MiApp:Ruta"];

            // Si hay que leer el Registro SOLO en Windows, aislarlo por SO en tiempo de ejecucion:
            var valor = OperatingSystem.IsWindows()
                ? (string?)Registry.GetValue(@"HKLM\SOFTWARE\MiApp", "Ruta", null)
                : cfg["MiApp:Ruta"];
            """,
            "OperatingSystem.IsWindows() evita PlatformNotSupportedException al ejecutar fuera de Windows."),

        new("PInvoke", "P/Invoke -> API gestionada o compilación condicional",
            """
            // Solo Windows (P/Invoke a kernel32):
            [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

            // Equivalente gestionado y portable (preferible):
            long ms = Environment.TickCount64;

            // Si NO hay equivalente, compilacion condicional (net8.0-windows define el simbolo WINDOWS):
            public static long Uptime()
            {
            #if WINDOWS
                return (long)GetTickCount64();       // P/Invoke solo se compila en Windows
            #else
                return Environment.TickCount64;      // rama portable (implementacion no-Windows si hiciera falta: otro equipo)
            #endif
            }
            """,
            "El TFM net8.0-windows define WINDOWS; el núcleo portable (net8.0) compila la rama #else."),

        new("Identity", "Identidad de Windows -> abstracción (el «seam»)",
            """
            // Solo Windows:
            var nombre = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

            // Portable: abstraer la identidad tras una interfaz (el nucleo depende de IUserIdentity)
            public interface IUserIdentity { string Name { get; } }

            // Implementacion Windows (la unica que se desarrolla aqui):
            public sealed class WindowsUserIdentity : IUserIdentity
            {
                public string Name => System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            }
            // La implementacion no-Windows de IUserIdentity queda como seam, a cargo de otro equipo.
            """,
            "La lógica de negocio depende solo de IUserIdentity; Windows aporta su implementación (DI). El «seam» no-Windows se deja preparado."),

        new("Database", "Oracle: System.Data.OracleClient -> Oracle.ManagedDataAccess.Core",
            """
            // Antes (eliminado en .NET moderno, solo Windows):
            using System.Data.OracleClient;
            using var c = new OracleConnection(cadena);

            // Portable (paquete NuGet Oracle.ManagedDataAccess.Core):
            using Oracle.ManagedDataAccess.Client;
            using var c = new OracleConnection(cadena);
            // API casi identica; revisar la cadena de conexion (TNS/EZConnect) y los tipos Oracle.
            """,
            "Oracle.ManagedDataAccess.Core es 100% gestionado y portable (no depende del SO)."),

        new("UI", "UI (WPF/WinForms) -> núcleo portable + UI Windows aislada",
            """
            // WPF/WinForms estan atados a Windows. Estructura PORTABLE-FIRST:
            //   MiApp.Core         (net8.0)          -> logica y ViewModels (portable, sin UI)
            //   MiApp.App.Windows  (net8.0-windows)  -> WPF (la UI actual)
            // Regla clave: el nucleo NO debe referenciar PresentationFramework ni System.Windows.Forms,
            // para que los ViewModels/logica sean reutilizables por cualquier UI futura.
            // La UI no-Windows NO se desarrolla aqui: queda preparada para que otro equipo la aporte
            // reutilizando los ViewModels del nucleo.
            """,
            "Separar UI de lógica deja el núcleo portable y reutilizable; la UI no-Windows queda como trabajo de otro equipo."),

        new("Cryptography", "DPAPI -> cifrado gestionado portable",
            """
            // Solo Windows (DPAPI):
            byte[] prot = ProtectedData.Protect(datos, null, DataProtectionScope.CurrentUser);

            // Portable: AES con clave gestionada externamente (KMS / gestor de secretos)
            using var aes = Aes.Create();
            aes.Key = claveDesdeGestorDeSecretos;   // no derivar de DPAPI

            // Tambien: RSA.Create()/ECDsa.Create() en vez de las variantes *Cng/*CryptoServiceProvider.
            """,
            "IMPORTANTE: lo ya protegido con DPAPI NO se puede descifrar fuera de Windows; planificar re-cifrado."),

        new("EventLog", "Visor de eventos -> logging portable",
            """
            // Solo Windows:
            new System.Diagnostics.EventLog("Application").WriteEntry("msg");

            // Portable (Serilog / Microsoft.Extensions.Logging): salida a consola/fichero
            ILogger log = loggerFactory.CreateLogger("MiApp");
            log.LogInformation("msg");
            """,
            "Un único framework de logging portable sustituye al Visor de eventos de Windows."),

        new("WMI", "WMI -> abstracción (el «seam»)",
            """
            // Solo Windows (WMI):
            using System.Management;
            var os = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");

            // Portable: abstraer la consulta del sistema tras una interfaz
            public interface ISystemInfo { string OsDescription { get; } }
            // Parte portable disponible: RuntimeInformation.OSDescription.
            // Los datos que hoy solo da WMI se aislan tras ISystemInfo; su implementacion no-Windows,
            // si se necesita, queda como seam a cargo de otro equipo.
            """,
            "WMI es exclusivo de Windows; se aísla tras una interfaz y parte de la información ya la da RuntimeInformation (portable).")
    };

    /// <summary>Titulo, bloque de codigo y nota en INGLES por categoria (traduccion completa del apendice).</summary>
    private static readonly Dictionary<string, (string Titulo, string Codigo, string Nota)> En = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Registry"] = ("Windows Registry -> portable configuration",
            """
            // Windows only:
            using Microsoft.Win32;
            var path = (string?)Registry.GetValue(@"HKLM\SOFTWARE\MyApp", "Path", null);

            // Portable: externalize to configuration (appsettings.json / environment variables)
            IConfiguration cfg = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            var path = cfg["MyApp:Path"];

            // If the Registry must be read ONLY on Windows, isolate it per OS at run time:
            var value = OperatingSystem.IsWindows()
                ? (string?)Registry.GetValue(@"HKLM\SOFTWARE\MyApp", "Path", null)
                : cfg["MyApp:Path"];
            """,
            "OperatingSystem.IsWindows() avoids PlatformNotSupportedException when running outside Windows."),
        ["PInvoke"] = ("P/Invoke -> managed API or conditional compilation",
            """
            // Windows only (P/Invoke to kernel32):
            [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

            // Managed, portable equivalent (preferred):
            long ms = Environment.TickCount64;

            // If there is NO equivalent, conditional compilation (net8.0-windows defines the WINDOWS symbol):
            public static long Uptime()
            {
            #if WINDOWS
                return (long)GetTickCount64();       // P/Invoke compiles only on Windows
            #else
                return Environment.TickCount64;      // portable branch (non-Windows impl if ever needed: another team)
            #endif
            }
            """,
            "The net8.0-windows TFM defines WINDOWS; the portable core (net8.0) compiles the #else branch."),
        ["Identity"] = ("Windows identity -> abstraction (the \"seam\")",
            """
            // Windows only:
            var name = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

            // Portable: abstract identity behind an interface (the core depends on IUserIdentity)
            public interface IUserIdentity { string Name { get; } }

            // Windows implementation (the only one developed here):
            public sealed class WindowsUserIdentity : IUserIdentity
            {
                public string Name => System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            }
            // The non-Windows implementation of IUserIdentity is left as a seam, up to another team.
            """,
            "Business logic depends only on IUserIdentity; Windows provides its implementation (DI). The non-Windows seam is left ready."),
        ["Database"] = ("Oracle: System.Data.OracleClient -> Oracle.ManagedDataAccess.Core",
            """
            // Before (removed in modern .NET, Windows only):
            using System.Data.OracleClient;
            using var c = new OracleConnection(connStr);

            // Portable (NuGet package Oracle.ManagedDataAccess.Core):
            using Oracle.ManagedDataAccess.Client;
            using var c = new OracleConnection(connStr);
            // Almost identical API; review the connection string (TNS/EZConnect) and the Oracle types.
            """,
            "Oracle.ManagedDataAccess.Core is 100% managed and portable (no OS dependency)."),
        ["UI"] = ("UI (WPF/WinForms) -> portable core + isolated Windows UI",
            """
            // WPF/WinForms are tied to Windows. PORTABLE-FIRST structure:
            //   MyApp.Core         (net8.0)          -> logic and ViewModels (portable, no UI)
            //   MyApp.App.Windows  (net8.0-windows)  -> WPF (the current UI)
            // Key rule: the core must NOT reference PresentationFramework or System.Windows.Forms,
            // so the ViewModels/logic are reusable by any future UI.
            // The non-Windows UI is NOT developed here: it is left ready for another team to provide,
            // reusing the core ViewModels.
            """,
            "Separating UI from logic keeps the core portable and reusable; the non-Windows UI is left for another team."),
        ["Cryptography"] = ("DPAPI -> portable managed encryption",
            """
            // Windows only (DPAPI):
            byte[] prot = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

            // Portable: AES with an externally managed key (KMS / secrets manager)
            using var aes = Aes.Create();
            aes.Key = keyFromSecretsManager;   // do not derive from DPAPI

            // Also: RSA.Create()/ECDsa.Create() instead of the *Cng/*CryptoServiceProvider variants.
            """,
            "IMPORTANT: data already protected with DPAPI CANNOT be decrypted outside Windows; plan a re-encryption."),
        ["EventLog"] = ("Event Viewer -> portable logging",
            """
            // Windows only:
            new System.Diagnostics.EventLog("Application").WriteEntry("msg");

            // Portable (Serilog / Microsoft.Extensions.Logging): console/file output
            ILogger log = loggerFactory.CreateLogger("MyApp");
            log.LogInformation("msg");
            """,
            "A single portable logging framework replaces the Windows Event Viewer."),
        ["WMI"] = ("WMI -> abstraction (the \"seam\")",
            """
            // Windows only (WMI):
            using System.Management;
            var os = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");

            // Portable: abstract the system query behind an interface
            public interface ISystemInfo { string OsDescription { get; } }
            // Portable part available: RuntimeInformation.OSDescription.
            // The data that today only WMI provides is isolated behind ISystemInfo; its non-Windows
            // implementation, if needed, is left as a seam up to another team.
            """,
            "WMI is Windows-only; it is isolated behind an interface and part of the info is already provided by RuntimeInformation (portable)."),
    };

    /// <summary>Ejemplos correspondientes a las categorias indicadas, en el idioma solicitado.</summary>
    public static IReadOnlyList<CodeExample> ForCategories(IEnumerable<string> categorias, Lang lang = Lang.Es)
    {
        var set = categorias.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(e => set.Contains(e.Categoria))
            .Select(e => lang == Lang.En && En.TryGetValue(e.Categoria, out var t)
                ? e with { Titulo = t.Titulo, Codigo = t.Codigo, Nota = t.Nota }
                : e)
            .ToList();
    }
}
