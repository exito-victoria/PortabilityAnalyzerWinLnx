using System.Runtime.InteropServices;

namespace Demo.Native;

/// <summary>Obtiene el uptime del sistema vía P/Invoke a kernel32. DEPENDENCIA WINDOWS: el P/Invoke a
/// kernel32 no es portable; existe un equivalente gestionado (Environment.TickCount64).</summary>
public static class NativeUptime
{
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    public static TimeSpan Get() => TimeSpan.FromMilliseconds(GetTickCount64());
}
