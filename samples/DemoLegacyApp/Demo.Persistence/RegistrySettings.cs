using Microsoft.Win32;

namespace Demo.Persistence;

/// <summary>Lee la cadena de conexión del Registro de Windows. DEPENDENCIA WINDOWS: el acceso al
/// Registro no existe fuera de Windows; debería aislarse tras una interfaz de configuración.</summary>
public sealed class RegistrySettings
{
    private const string KeyPath = @"SOFTWARE\DemoLegacyApp";

    public string GetConnectionString()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return (string?)key?.GetValue("ConnectionString") ?? "Data Source=localhost";
    }

    public void SetConnectionString(string value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
        key.SetValue("ConnectionString", value);
    }
}
