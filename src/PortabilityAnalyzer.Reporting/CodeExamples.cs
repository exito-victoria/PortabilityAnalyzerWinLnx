namespace PortabilityAnalyzer.Reporting;

/// <summary>Ejemplo de codigo para hacer PORTABLE una categoria de dependencia hoy atada a Windows.</summary>
public sealed record CodeExample(string Categoria, string Titulo, string Codigo, string Nota);

/// <summary>
/// Ejemplos de patron LIBRARY-FIRST por categoria de dependencia: resolver cada dependencia de Windows con
/// una libreria/NuGet o API gestionada MULTIPLATAFORMA, implementada en el propio codigo y transparente al
/// SO (la misma clase funciona en Windows y Linux), sin dejar nada para otro equipo. La unica excepcion es la
/// GUI WPF, que no se migra. Se muestran en el informe (apendice) solo para las categorias que aparecen.
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
                return Environment.TickCount64;      // rama portable (API gestionada del BCL, multiplataforma)
            #endif
            }
            """,
            "El TFM net8.0-windows define WINDOWS; el núcleo portable (net8.0) compila la rama #else."),

        new("Identity", "Identidad de Windows -> librería multiplataforma",
            """
            // Solo Windows:
            var nombre = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

            // Multiplataforma y transparente: Environment.UserName funciona en Windows y Linux.
            public interface IUserIdentity { string Name { get; } }

            public sealed class PortableUserIdentity : IUserIdentity
            {
                public string Name => Environment.UserName;   // mismo código en todos los SO
            }
            // Para identidad de dominio/Active Directory: System.DirectoryServices.Protocols
            // (cliente LDAP multiplataforma) o Novell.Directory.Ldap. La implementación se aporta aquí,
            // no se deja nada para otro equipo.
            """,
            "La lógica depende solo de IUserIdentity; la implementación multiplataforma (Environment.UserName / LDAP) se aporta en el propio código."),

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
            // WPF/WinForms estan atados a Windows y son la UNICA excepcion: NO se migran.
            //   MiApp.Core         (net8.0)          -> logica y ViewModels (multiplataforma, sin UI)
            //   MiApp.App.Windows  (net8.0-windows)  -> WPF (la UI actual, solo en Windows)
            // Regla clave: el nucleo NO referencia PresentationFramework ni System.Windows.Forms.
            // En Linux se construyen solo esas clases y metodos (el nucleo); la capa grafica no se construye.
            """,
            "La GUI WPF es la única excepción: no se migra. La lógica/ViewModels van al núcleo multiplataforma; en Linux solo se construyen las clases y métodos, no la UI."),

        new("Cryptography", "DPAPI y CNG -> librerías multiplataforma (transparente al SO)",
            """
            // Solo Windows (DPAPI): ProtectedData.Protect/Unprotect + DataProtectionScope.
            byte[] prot = ProtectedData.Protect(datos, null, DataProtectionScope.CurrentUser);

            // Portable y TRANSPARENTE: ASP.NET Core Data Protection (Microsoft.AspNetCore.DataProtection).
            // El reescritor genera un shim Portability.Security.ProtectedData con la MISMA API, respaldado por
            // la librería (funciona en Windows/Linux/macOS). El código de arriba NO cambia: solo se añade
            // 'using Portability.Security;'. Internamente:
            var provider = DataProtectionProvider.Create(new DirectoryInfo(rutaKeyRing));
            var protector = provider.CreateProtector("MiApp");
            byte[] cifrado = protector.Protect(datos);   // clave gestionada por la librería, persistida en disco

            // CNG/CSP solo-Windows -> factorías portables del BCL (misma clase base, implementación por SO):
            using var rsa = RSA.Create(2048);     // en vez de new RSACng(2048) / new RSACryptoServiceProvider()
            """,
            "Data Protection sustituye a DPAPI de forma multiplataforma y transparente. CAVEAT: lo YA cifrado con el DPAPI real de Windows debe re-protegerse una vez (leer con DPAPI, reescribir con el shim); lo nuevo es portable."),

        new("EventLog", "Visor de eventos -> logging portable",
            """
            // Solo Windows:
            new System.Diagnostics.EventLog("Application").WriteEntry("msg");

            // Portable (Serilog / Microsoft.Extensions.Logging): salida a consola/fichero
            ILogger log = loggerFactory.CreateLogger("MiApp");
            log.LogInformation("msg");
            """,
            "Un único framework de logging portable sustituye al Visor de eventos de Windows."),

        new("WMI", "WMI -> RuntimeInformation + librería multiplataforma",
            """
            // Solo Windows (WMI):
            using System.Management;
            var os = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");

            // Multiplataforma: RuntimeInformation para SO/arquitectura...
            var desc = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            // ...y para hardware/inventario, una librería multiplataforma (p. ej. Hardware.Info, NuGet):
            var hw = new Hardware.Info.HardwareInfo();
            hw.RefreshMemoryStatus();
            // La implementación multiplataforma se aporta aquí; no se deja nada para otro equipo.
            """,
            "WMI es exclusivo de Windows; RuntimeInformation + una librería como Hardware.Info cubren la información de forma multiplataforma."),

        new("Threading", "Hilos y temporizadores -> multiplataforma (BCL)",
            """
            // Hilos/tareas YA portables (sin cambios): Thread, Task, Parallel, async/await, ThreadPool,
            // SemaphoreSlim, System.Threading.Timer. Solo lo de WPF/Windows se reemplaza:

            // Solo Windows (WPF): DispatcherTimer.
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (s, e) => Refrescar();
            t.Start();

            // Portable y TRANSPARENTE: el reescritor lo cambia por Portability.Threading.PortableTimer
            // (misma API: Interval/Tick/Start/Stop), respaldado por System.Timers.Timer y marshalling al
            // SynchronizationContext capturado. El código de arriba no cambia (solo el tipo).

            // Marshalling a la UI (Dispatcher.Invoke) -> async/await + IProgress<T> (multiplataforma):
            var progreso = new Progress<int>(p => BarraProgreso = p);  // se entrega en el hilo capturado
            await Task.Run(() => TrabajoPesado(progreso));
            """,
            "Task/Parallel/async ya son multiplataforma; DispatcherTimer -> PortableTimer (temporizador del BCL) y el marshalling a UI -> async/await + IProgress<T> o SynchronizationContext.")
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
                return Environment.TickCount64;      // portable branch (managed BCL API, cross-platform)
            #endif
            }
            """,
            "The net8.0-windows TFM defines WINDOWS; the portable core (net8.0) compiles the #else branch."),
        ["Identity"] = ("Windows identity -> cross-platform library",
            """
            // Windows only:
            var name = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

            // Cross-platform and transparent: Environment.UserName works on Windows and Linux.
            public interface IUserIdentity { string Name { get; } }

            public sealed class PortableUserIdentity : IUserIdentity
            {
                public string Name => Environment.UserName;   // same code on every OS
            }
            // For domain/Active Directory identity: System.DirectoryServices.Protocols (cross-platform LDAP)
            // or Novell.Directory.Ldap. The implementation is provided here; nothing is left for another team.
            """,
            "Business logic depends only on IUserIdentity; the cross-platform implementation (Environment.UserName / LDAP) is provided in the code itself."),
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
            // WPF/WinForms are tied to Windows and are the ONLY exception: they are NOT migrated.
            //   MyApp.Core         (net8.0)          -> logic and ViewModels (cross-platform, no UI)
            //   MyApp.App.Windows  (net8.0-windows)  -> WPF (the current UI, Windows only)
            // Key rule: the core must NOT reference PresentationFramework or System.Windows.Forms.
            // On Linux only those classes and methods (the core) are built; the graphical layer is not built.
            """,
            "The WPF GUI is the only exception: it is not migrated. The logic/ViewModels go to the cross-platform core; on Linux only the classes and methods are built, not the UI."),
        ["Cryptography"] = ("DPAPI and CNG -> cross-platform libraries (OS-transparent)",
            """
            // Windows only (DPAPI): ProtectedData.Protect/Unprotect + DataProtectionScope.
            byte[] prot = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

            // Portable and TRANSPARENT: ASP.NET Core Data Protection (Microsoft.AspNetCore.DataProtection).
            // The rewriter generates a Portability.Security.ProtectedData shim with the SAME API, backed by the
            // library (Windows/Linux/macOS). The code above does NOT change: only 'using Portability.Security;'
            // is added. Internally:
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRingPath));
            var protector = provider.CreateProtector("MyApp");
            byte[] encrypted = protector.Protect(data);   // key managed by the library, persisted on disk

            // Windows-only CNG/CSP -> portable BCL factories (same base class, per-OS implementation):
            using var rsa = RSA.Create(2048);     // instead of new RSACng(2048) / new RSACryptoServiceProvider()
            """,
            "Data Protection replaces DPAPI cross-platform and transparently. CAVEAT: data ALREADY encrypted with the real Windows DPAPI must be re-protected once (read with DPAPI, write back with the shim); new data is portable."),
        ["EventLog"] = ("Event Viewer -> portable logging",
            """
            // Windows only:
            new System.Diagnostics.EventLog("Application").WriteEntry("msg");

            // Portable (Serilog / Microsoft.Extensions.Logging): console/file output
            ILogger log = loggerFactory.CreateLogger("MyApp");
            log.LogInformation("msg");
            """,
            "A single portable logging framework replaces the Windows Event Viewer."),
        ["WMI"] = ("WMI -> RuntimeInformation + cross-platform library",
            """
            // Windows only (WMI):
            using System.Management;
            var os = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");

            // Cross-platform: RuntimeInformation for OS/architecture...
            var desc = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            // ...and for hardware/inventory, a cross-platform library (e.g. Hardware.Info, NuGet):
            var hw = new Hardware.Info.HardwareInfo();
            hw.RefreshMemoryStatus();
            // The cross-platform implementation is provided here; nothing is left for another team.
            """,
            "WMI is Windows-only; RuntimeInformation + a library like Hardware.Info cover the information cross-platform."),
        ["Threading"] = ("Threads and timers -> cross-platform (BCL)",
            """
            // Threads/tasks are ALREADY portable (no change): Thread, Task, Parallel, async/await, ThreadPool,
            // SemaphoreSlim, System.Threading.Timer. Only the WPF/Windows bits are replaced:

            // Windows only (WPF): DispatcherTimer.
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (s, e) => Refresh();
            t.Start();

            // Portable and TRANSPARENT: the rewriter swaps it for Portability.Threading.PortableTimer
            // (same API: Interval/Tick/Start/Stop), backed by System.Timers.Timer and marshalling to the
            // captured SynchronizationContext. The code above does not change (only the type).

            // UI marshalling (Dispatcher.Invoke) -> async/await + IProgress<T> (cross-platform):
            var progress = new Progress<int>(p => ProgressBar = p);  // delivered on the captured thread
            await Task.Run(() => HeavyWork(progress));
            """,
            "Task/Parallel/async are already cross-platform; DispatcherTimer -> PortableTimer (a BCL timer) and UI marshalling -> async/await + IProgress<T> or SynchronizationContext."),
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
