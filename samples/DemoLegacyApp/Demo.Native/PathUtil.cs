namespace Demo.Native;

/// <summary>Utilidad de rutas portable: usa Path.Combine, sin letras de unidad ni separadores fijos.</summary>
public static class PathUtil
{
    public static string DataFile(string baseDir, string name) =>
        Path.Combine(baseDir, "data", name);

    public static string NormalizeSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
}
