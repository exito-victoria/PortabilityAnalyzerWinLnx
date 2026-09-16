namespace Demo.Native;

/// <summary>Usa Console.CapsLock, que está anotada [SupportedOSPlatform("windows")] pero NO está cubierta
/// por el catálogo (Console no es solo-Windows). Sirve para validar el DESCUBRIMIENTO de reglas nuevas.</summary>
public static class WinConsole
{
    public static bool CapsLockOn() => System.Console.CapsLock;
}
