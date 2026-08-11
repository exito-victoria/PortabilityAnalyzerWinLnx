namespace PortabilityAnalyzer.Reporting;

/// <summary>Ejemplo de codigo para hacer multiplataforma una categoria de dependencia Windows.</summary>
public sealed record CodeExample(string Categoria, string Titulo, string Codigo, string Nota);

/// <summary>
/// Ejemplos de equivalencia Linux / compilacion condicional por SO para las categorias de dependencia
/// mas habituales. Se muestran en el informe (apendice) solo para las categorias que aparecen.
/// </summary>
public static class CodeExamples
{
    private static readonly CodeExample[] All =
    {
        new("Registry", "Registro de Windows -> configuracion multiplataforma",
            """
            // Solo Windows:
            using Microsoft.Win32;
            var ruta = (string?)Registry.GetValue(@"HKLM\SOFTWARE\MiApp", "Ruta", null);

            // Multiplataforma: externalizar a configuracion (appsettings.json / variables de entorno)
            IConfiguration cfg = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            var ruta = cfg["MiApp:Ruta"];

            // Si hay que leer el Registro SOLO en Windows, aislar por SO en tiempo de ejecucion:
            var valor = OperatingSystem.IsWindows()
                ? (string?)Registry.GetValue(@"HKLM\SOFTWARE\MiApp", "Ruta", null)
                : cfg["MiApp:Ruta"];
            """,
            "OperatingSystem.IsWindows() evita PlatformNotSupportedException al ejecutar en Linux."),

        new("PInvoke", "P/Invoke -> API gestionada o compilacion condicional",
            """
            // Solo Windows (P/Invoke a kernel32):
            [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

            // Equivalente gestionado multiplataforma (preferible):
            long ms = Environment.TickCount64;

            // Si NO hay equivalente, compilacion condicional (net8.0-windows define el simbolo WINDOWS):
            public static long Uptime()
            {
            #if WINDOWS
                return (long)GetTickCount64();       // P/Invoke solo se compila en Windows
            #else
                return Environment.TickCount64;      // implementacion Linux (o P/Invoke a libc)
            #endif
            }
            """,
            "El TFM net8.0-windows define WINDOWS; en net8.0 (Linux) se compila la rama #else."),

        new("Identity", "Identidad de Windows -> abstraccion por SO",
            """
            // Solo Windows:
            var nombre = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

            // Multiplataforma: abstraer la identidad tras una interfaz
            public interface IUserIdentity { string Name { get; } }

            public sealed class UserIdentity : IUserIdentity
            {
                public string Name => OperatingSystem.IsWindows()
                    ? System.Security.Principal.WindowsIdentity.GetCurrent().Name
                    : Environment.UserName;   // en Linux: Kerberos/GSSAPI, LDAP o el token de la peticion
            }
            """,
            "La logica de negocio depende de IUserIdentity; cada SO aporta su implementacion (DI)."),

        new("Database", "Oracle: System.Data.OracleClient -> Oracle.ManagedDataAccess.Core",
            """
            // Antes (eliminado en .NET moderno, solo Windows):
            using System.Data.OracleClient;
            using var c = new OracleConnection(cadena);

            // Multiplataforma (paquete NuGet Oracle.ManagedDataAccess.Core):
            using Oracle.ManagedDataAccess.Client;
            using var c = new OracleConnection(cadena);
            // API casi identica; revisar la cadena de conexion (TNS/EZConnect) y los tipos Oracle.
            """,
            "Oracle.ManagedDataAccess.Core es 100% gestionado y corre en Windows y Linux."),

        new("UI", "UI (WPF/WinForms) -> core comun + Avalonia en Linux",
            """
            // WPF/WinForms no se ejecutan en Linux. Estructura recomendada:
            //   MiApp.Core         (net8.0)          -> logica y ViewModels (portable, sin UI)
            //   MiApp.App.Windows  (net8.0-windows)  -> WPF (la UI actual)
            //   MiApp.App.Linux    (net8.0)          -> Avalonia (MVVM), reutiliza los ViewModels del core
            // Regla clave: el core NO debe referenciar PresentationFramework ni System.Windows.Forms.
            """,
            "Avalonia usa XAML/MVVM (cercano a WPF): se reutiliza gran parte de la UI."),

        new("Cryptography", "DPAPI -> cifrado gestionado multiplataforma",
            """
            // Solo Windows (DPAPI):
            byte[] prot = ProtectedData.Protect(datos, null, DataProtectionScope.CurrentUser);

            // Multiplataforma: AES con clave gestionada externamente (KMS / gestor de secretos)
            using var aes = Aes.Create();
            aes.Key = claveDesdeGestorDeSecretos;   // no derivar de DPAPI

            // Tambien: RSA.Create()/ECDsa.Create() en vez de las variantes *Cng/*CryptoServiceProvider.
            """,
            "IMPORTANTE: lo ya protegido con DPAPI NO se puede descifrar en Linux; planificar re-cifrado."),

        new("EventLog", "Visor de eventos -> logging multiplataforma",
            """
            // Solo Windows:
            new System.Diagnostics.EventLog("Application").WriteEntry("msg");

            // Multiplataforma (Serilog / Microsoft.Extensions.Logging): salida a consola/fichero/syslog
            ILogger log = loggerFactory.CreateLogger("MiApp");
            log.LogInformation("msg");
            """,
            "Un unico framework de logging sirve para Windows y Linux."),

        new("WMI", "WMI -> abstraccion (Linux: /proc, /sys)",
            """
            // Solo Windows (WMI):
            using System.Management;
            var os = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");

            // Multiplataforma: abstraer la consulta del sistema
            public interface ISystemInfo { string OsDescription { get; } }
            // Windows: WMI;  Linux: leer /proc, /sys o RuntimeInformation.OSDescription.
            """,
            "WMI es exclusivo de Windows; en Linux la informacion equivalente esta en /proc y /sys.")
    };

    /// <summary>Ejemplos correspondientes a las categorias indicadas (en el orden de la tabla interna).</summary>
    public static IReadOnlyList<CodeExample> ForCategories(IEnumerable<string> categorias)
    {
        var set = categorias.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(e => set.Contains(e.Categoria)).ToList();
    }
}
