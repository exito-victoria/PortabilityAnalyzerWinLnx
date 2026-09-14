namespace Demo.Tools;

/// <summary>Utilidad PORTABLE pura (no usa ni referencia nada de Windows): debe quedar en Demo.Tools.Core.</summary>
public static class Helper
{
    public static string Clean(string s) => (s ?? string.Empty).Trim();
}
