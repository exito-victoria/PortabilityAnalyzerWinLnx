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
        new("Registry", "Registro de Windows -> configuracion portable",
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

        new("PInvoke", "P/Invoke -> API gestionada o compilacion condicional",
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
            "El TFM net8.0-windows define WINDOWS; el nucleo portable (net8.0) compila la rama #else."),

        new("Identity", "Identidad de Windows -> abstraccion (seam)",
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
            "La logica de negocio depende solo de IUserIdentity; Windows aporta su implementacion (DI). El seam no-Windows se deja preparado."),

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

        new("UI", "UI (WPF/WinForms) -> nucleo portable + UI Windows aislada",
            """
            // WPF/WinForms estan atados a Windows. Estructura PORTABLE-FIRST:
            //   MiApp.Core         (net8.0)          -> logica y ViewModels (portable, sin UI)
            //   MiApp.App.Windows  (net8.0-windows)  -> WPF (la UI actual)
            // Regla clave: el nucleo NO debe referenciar PresentationFramework ni System.Windows.Forms,
            // para que los ViewModels/logica sean reutilizables por cualquier UI futura.
            // La UI no-Windows NO se desarrolla aqui: queda preparada para que otro equipo la aporte
            // reutilizando los ViewModels del nucleo.
            """,
            "Separar UI de logica deja el nucleo portable y reutilizable; la UI no-Windows queda como trabajo de otro equipo."),

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
            "Un unico framework de logging portable sustituye al Visor de eventos de Windows."),

        new("WMI", "WMI -> abstraccion (seam)",
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
            "WMI es exclusivo de Windows; se aisla tras una interfaz y parte de la informacion ya la da RuntimeInformation (portable).")
    };

    /// <summary>Ejemplos correspondientes a las categorias indicadas (en el orden de la tabla interna).</summary>
    public static IReadOnlyList<CodeExample> ForCategories(IEnumerable<string> categorias)
    {
        var set = categorias.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(e => set.Contains(e.Categoria)).ToList();
    }
}
