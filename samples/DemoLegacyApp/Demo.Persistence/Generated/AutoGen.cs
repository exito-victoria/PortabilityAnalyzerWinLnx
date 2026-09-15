using Microsoft.Win32;

namespace Demo.Persistence.Generated;

/// <summary>Fichero en una carpeta IGNORADA por .gitignore (Generated/). Aunque usa API de Windows, el
/// reescritor debe OMITIRLO por completo (ni Core ni Windows), porque no forma parte de lo que se separa.</summary>
public static class AutoGen
{
    public static string? Read() =>
        (string?)Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Demo")?.GetValue("x");
}
