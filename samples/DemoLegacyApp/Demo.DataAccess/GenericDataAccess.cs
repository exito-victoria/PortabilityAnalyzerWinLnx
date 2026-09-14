using Microsoft.Win32;

namespace Demo.DataAccess;

/// <summary>Acceso a datos DEPENDIENTE de Windows (lee el DSN del Registro). Es un tipo de Windows:
/// cualquier clase de otro proyecto que lo use como campo/propiedad debe acabar en el lado Windows.</summary>
public sealed class GenericDataAccess
{
    public string ConnectionString { get; }

    public GenericDataAccess()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\DemoLegacyApp");
        ConnectionString = (string?)key?.GetValue("Dsn") ?? "localhost";
    }

    public int Execute(string sql) => sql.Length;
}
