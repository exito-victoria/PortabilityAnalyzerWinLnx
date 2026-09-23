using System.Text;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Generates the REAL abstraction-layer project (<c>&lt;Base&gt;.Abstractions</c>) that the report recommends:
/// the interfaces the solution needs (by detected category) plus a CROSS-PLATFORM default implementation for
/// each one and a DI registration extension. This is what turns the "abstraction layer" from a paragraph in
/// the Word report into actual, compilable code in the generated solution.
///
/// Category -> interface mapping (mirrors the report's "Capa de abstracción (interfaces por plataforma)"):
///   Registry          -> ISettingsStore        (default: EnvironmentSettingsStore)
///   Identity          -> IUserIdentity          (default: EnvironmentUserIdentity) + IAuthenticationService (interface only; app-specific)
///   ProcessInvocation -> IProcessRunner         (default: ProcessRunner, System.Diagnostics.Process)
///   PInvoke           -> INativePlatform        (default: PortableNativePlatform, managed BCL)
///   Threading         -> IInterProcessLock      (default: MutexInterProcessLock, named Mutex)
///   UI                -> IUserNotifier           (default: ConsoleUserNotifier; a Windows dialog impl can be added in the .Windows project)
/// The default implementations run on Windows AND Linux, so the layer is usable out of the box; swap any of
/// them for your own (e.g. an IConfiguration-backed ISettingsStore) via DI.
/// </summary>
internal static class AbstractionsGenerator
{
    public sealed record GeneratedFile(string Rel, string Content);

    /// <summary>Categories that trigger the abstraction layer (COM/Cryptography are handled elsewhere: crypto
    /// via the generated DPAPI shim, so they are not part of this layer).</summary>
    public static bool IsRelevantCategory(string categoria) => categoria.ToLowerInvariant() switch
    {
        "registry" or "identity" or "processinvocation" or "pinvoke" or "threading" or "ui" => true,
        _ => false
    };

    /// <summary>Builds the abstraction-layer project files (csproj + interfaces + impls + DI extension) for the
    /// given namespace/assembly name and the set of categories present in the solution. Empty if none apply.</summary>
    public static IReadOnlyList<GeneratedFile> Build(string abstractionsName, IEnumerable<string> categories)
    {
        var cats = new HashSet<string>(categories.Select(c => c.ToLowerInvariant()));
        bool reg = cats.Contains("registry");
        bool id = cats.Contains("identity");
        bool proc = cats.Contains("processinvocation");
        bool pinvoke = cats.Contains("pinvoke");
        bool thread = cats.Contains("threading");
        bool ui = cats.Contains("ui");
        if (!(reg || id || proc || pinvoke || thread || ui)) return Array.Empty<GeneratedFile>();

        var files = new List<GeneratedFile>();
        var registrations = new List<(string Interface, string Impl)>();

        if (reg)
        {
            files.Add(new("ISettingsStore.cs", SettingsStore(abstractionsName)));
            registrations.Add(("ISettingsStore", "EnvironmentSettingsStore"));
        }
        if (id)
        {
            files.Add(new("IUserIdentity.cs", UserIdentity(abstractionsName)));
            registrations.Add(("IUserIdentity", "EnvironmentUserIdentity"));
        }
        if (proc)
        {
            files.Add(new("IProcessRunner.cs", ProcessRunner(abstractionsName)));
            registrations.Add(("IProcessRunner", "ProcessRunner"));
        }
        if (pinvoke)
        {
            files.Add(new("INativePlatform.cs", NativePlatform(abstractionsName)));
            registrations.Add(("INativePlatform", "PortableNativePlatform"));
        }
        if (thread)
        {
            files.Add(new("IInterProcessLock.cs", InterProcessLock(abstractionsName)));
            registrations.Add(("IInterProcessLock", "MutexInterProcessLock"));
        }
        if (ui)
        {
            files.Add(new("IUserNotifier.cs", UserNotifier(abstractionsName)));
            registrations.Add(("IUserNotifier", "ConsoleUserNotifier"));
        }

        files.Add(new("AbstractionsRegistration.cs", Registration(abstractionsName, registrations)));
        files.Add(new(abstractionsName + ".csproj", Csproj()));
        return files;
    }

    private static string Header(string ns, string extraUsings = "") =>
        "// [Cross-platform rewrite] Abstraction layer: portable interface + cross-platform default implementation." + NL +
        "// The core depends on the interface; the default implementation runs on Windows and Linux. Replace it via" + NL +
        "// DI with your own (e.g. an IConfiguration-backed settings store) when you need a different behaviour." + NL +
        extraUsings +
        $"namespace {ns};" + NL + NL;

    private const string NL = "\n";

    private static string SettingsStore(string ns) => Header(ns,
        "using System;" + NL) +
        """
        /// <summary>External configuration store (replaces the Windows Registry). Portable.</summary>
        public interface ISettingsStore
        {
            /// <summary>Returns the value for a key, or null if it is not set.</summary>
            string? Get(string key);
            /// <summary>Sets (or overwrites) the value for a key.</summary>
            void Set(string key, string value);
            /// <summary>True if the key exists; the value is returned via <paramref name="value"/>.</summary>
            bool TryGet(string key, out string value);
        }

        /// <summary>Cross-platform default backed by environment variables. For richer scenarios replace it with
        /// an implementation over Microsoft.Extensions.Configuration (appsettings.json + environment variables).</summary>
        public sealed class EnvironmentSettingsStore : ISettingsStore
        {
            public string? Get(string key) => Environment.GetEnvironmentVariable(key);

            public void Set(string key, string value) => Environment.SetEnvironmentVariable(key, value);

            public bool TryGet(string key, out string value)
            {
                var v = Environment.GetEnvironmentVariable(key);
                value = v ?? string.Empty;
                return v is not null;
            }
        }
        """ + NL;

    private static string UserIdentity(string ns) => Header(ns,
        "using System;" + NL) +
        """
        /// <summary>Current user identity (replaces System.Security.Principal.WindowsIdentity). Portable.</summary>
        public interface IUserIdentity
        {
            /// <summary>The current user's name.</summary>
            string Name { get; }
            /// <summary>True if there is an authenticated user.</summary>
            bool IsAuthenticated { get; }
        }

        /// <summary>App-specific authentication. The interface lives in the portable core; provide your own
        /// implementation (LDAP via System.DirectoryServices.Protocols, an identity provider, tokens...).</summary>
        public interface IAuthenticationService
        {
            /// <summary>Validates the given credentials.</summary>
            bool Authenticate(string user, string password);
            /// <summary>The identity resolved after a successful authentication.</summary>
            IUserIdentity Current { get; }
        }

        /// <summary>Cross-platform default: the OS user name (works on Windows and Linux). For domain/AD identity,
        /// implement IUserIdentity/IAuthenticationService with System.DirectoryServices.Protocols (LDAP).</summary>
        public sealed class EnvironmentUserIdentity : IUserIdentity
        {
            public string Name => Environment.UserName;
            public bool IsAuthenticated => !string.IsNullOrEmpty(Environment.UserName);
        }
        """ + NL;

    private static string ProcessRunner(string ns) => Header(ns,
        "using System;" + NL + "using System.Diagnostics;" + NL + "using System.Threading;" + NL + "using System.Threading.Tasks;" + NL) +
        """
        /// <summary>Runs OS commands/processes. Portable (System.Diagnostics.Process works on every OS).</summary>
        public interface IProcessRunner
        {
            /// <summary>Runs a process and waits for it, returning its exit code.</summary>
            int Run(string fileName, string arguments = "");
            /// <summary>Runs a process asynchronously, returning its exit code.</summary>
            Task<int> RunAsync(string fileName, string arguments = "", CancellationToken cancellationToken = default);
            /// <summary>Runs a process capturing its standard output and error.</summary>
            (int ExitCode, string Output, string Error) Capture(string fileName, string arguments = "");
        }

        /// <summary>Cross-platform default over System.Diagnostics.Process.</summary>
        public sealed class ProcessRunner : IProcessRunner
        {
            public int Run(string fileName, string arguments = "")
            {
                using var p = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false })
                              ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
                p.WaitForExit();
                return p.ExitCode;
            }

            public async Task<int> RunAsync(string fileName, string arguments = "", CancellationToken cancellationToken = default)
            {
                using var p = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false })
                              ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
                await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return p.ExitCode;
            }

            public (int ExitCode, string Output, string Error) Capture(string fileName, string arguments = "")
            {
                using var p = Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
                var output = p.StandardOutput.ReadToEnd();
                var error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, output, error);
            }
        }
        """ + NL;

    private static string NativePlatform(string ns) => Header(ns,
        "using System;" + NL) +
        """
        /// <summary>Common OS/native information behind a portable interface (replaces ad-hoc P/Invoke to Win32).
        /// Extend it with the specific native operations your P/Invoke calls used; back each one with the managed
        /// BCL equivalent or a cross-platform library so the core stays OS-agnostic.</summary>
        public interface INativePlatform
        {
            /// <summary>Machine name.</summary>
            string MachineName { get; }
            /// <summary>Milliseconds since the system started (managed equivalent of GetTickCount64).</summary>
            long TickCountMs { get; }
            /// <summary>True when running on Windows.</summary>
            bool IsWindows { get; }
        }

        /// <summary>Cross-platform default using the managed BCL (no P/Invoke).</summary>
        public sealed class PortableNativePlatform : INativePlatform
        {
            public string MachineName => Environment.MachineName;
            public long TickCountMs => Environment.TickCount64;
            public bool IsWindows => OperatingSystem.IsWindows();
        }
        """ + NL;

    private static string InterProcessLock(string ns) => Header(ns,
        "using System;" + NL + "using System.Threading;" + NL) +
        """
        /// <summary>Inter-process synchronization/signaling (replaces Win32 named events/mutexes). Portable.</summary>
        public interface IInterProcessLock
        {
            /// <summary>Acquires the named lock, blocking until it is available; dispose the handle to release it.</summary>
            IDisposable Acquire(string name);
            /// <summary>Tries to acquire the named lock within a timeout; the handle (to release) is returned via
            /// <paramref name="handle"/> when it succeeds.</summary>
            bool TryAcquire(string name, TimeSpan timeout, out IDisposable? handle);
        }

        /// <summary>Cross-platform default over a named System.Threading.Mutex. Note: on non-Windows the named
        /// mutex is process-local to the .NET runtime; for machine-wide locking across arbitrary processes use a
        /// file lock or an OS-specific mechanism.</summary>
        public sealed class MutexInterProcessLock : IInterProcessLock
        {
            public IDisposable Acquire(string name)
            {
                var mutex = new Mutex(initiallyOwned: false, name);
                mutex.WaitOne();
                return new Handle(mutex);
            }

            public bool TryAcquire(string name, TimeSpan timeout, out IDisposable? handle)
            {
                var mutex = new Mutex(initiallyOwned: false, name);
                if (mutex.WaitOne(timeout)) { handle = new Handle(mutex); return true; }
                mutex.Dispose();
                handle = null;
                return false;
            }

            private sealed class Handle : IDisposable
            {
                private readonly Mutex _mutex;
                public Handle(Mutex mutex) => _mutex = mutex;
                public void Dispose() { try { _mutex.ReleaseMutex(); } catch { /* not owned */ } _mutex.Dispose(); }
            }
        }
        """ + NL;

    private static string UserNotifier(string ns) => Header(ns,
        "using System;" + NL) +
        """
        /// <summary>Shows a message/confirmation to the user WITHOUT depending on WPF/WinForms in the core. The
        /// core calls this interface; the Windows app can register a dialog-based implementation (MessageBox) and
        /// non-GUI hosts use the console default. This keeps the portable core free of the Windows UI.</summary>
        public interface IUserNotifier
        {
            /// <summary>Shows an informational message.</summary>
            void Notify(string message);
            /// <summary>Asks the user to confirm; returns true if accepted.</summary>
            bool Confirm(string message);
        }

        /// <summary>Cross-platform default over the console. In the Windows GUI app you can register instead an
        /// implementation that shows a WPF/WinForms MessageBox (System.Windows.MessageBox).</summary>
        public sealed class ConsoleUserNotifier : IUserNotifier
        {
            public void Notify(string message) => Console.WriteLine(message);

            public bool Confirm(string message)
            {
                Console.Write(message + " [y/N] ");
                var line = Console.ReadLine();
                return line is not null && (line.Equals("y", StringComparison.OrdinalIgnoreCase)
                                         || line.Equals("yes", StringComparison.OrdinalIgnoreCase));
            }
        }
        """ + NL;

    private static string Registration(string ns, IReadOnlyList<(string Interface, string Impl)> regs)
    {
        var sb = new StringBuilder();
        sb.Append("// [Cross-platform rewrite] DI registration of the abstraction layer's cross-platform default").Append(NL);
        sb.Append("// implementations. Call services.AddPortableAbstractions() at startup (the generated CompositionRoot").Append(NL);
        sb.Append("// already does). Override any of these with your own implementation by registering it afterwards.").Append(NL);
        sb.Append("using Microsoft.Extensions.DependencyInjection;").Append(NL).Append(NL);
        sb.Append($"namespace {ns};").Append(NL).Append(NL);
        sb.Append("public static class AbstractionsRegistration").Append(NL);
        sb.Append("{").Append(NL);
        sb.Append("    /// <summary>Registers the portable default implementation of every abstraction in this layer.</summary>").Append(NL);
        sb.Append("    public static IServiceCollection AddPortableAbstractions(this IServiceCollection services)").Append(NL);
        sb.Append("    {").Append(NL);
        foreach (var (iface, impl) in regs)
            sb.Append($"        services.AddSingleton<{iface}, {impl}>();").Append(NL);
        sb.Append("        return services;").Append(NL);
        sb.Append("    }").Append(NL);
        sb.Append("}").Append(NL);
        return sb.ToString();
    }

    private static string Csproj() =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">" + NL + NL +
        "  <PropertyGroup>" + NL +
        "    <TargetFramework>net8.0</TargetFramework>" + NL +
        "    <ImplicitUsings>enable</ImplicitUsings>" + NL +
        "    <Nullable>enable</Nullable>" + NL +
        "  </PropertyGroup>" + NL + NL +
        "  <ItemGroup>" + NL +
        "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection.Abstractions\" Version=\"8.0.2\" />" + NL +
        "  </ItemGroup>" + NL + NL +
        "</Project>" + NL;
}
