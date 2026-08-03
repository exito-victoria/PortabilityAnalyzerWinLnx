namespace PortabilityAnalyzer.Cli;

/// <summary>
/// Descubrimiento simple de ensamblados (fichero .dll/.exe o directorio, recursivo).
/// Excluye las carpetas intermedias de compilacion (<c>obj/</c>, con sus reference assemblies
/// <c>ref/</c> y <c>refint/</c>): son duplicados solo-metadatos de la salida real de <c>bin/</c>
/// y contarlos multiplica de forma artificial el esfuerzo y los bloqueantes.
/// TODO produccion: sustituir por un IProjectDiscovery que lea el .sln, resuelva cada .csproj,
/// sus carpetas bin de salida y los paquetes NuGet, y marque cuales son de terceros.
/// </summary>
internal static class AssemblyDiscovery
{
    // Segmentos de directorio que denotan artefactos intermedios y no la salida a analizar.
    private static readonly string[] ExcludedDirSegments = { "obj" };

    // Un ensamblado gestionado puede ser .dll o .exe (WinForms/WPF de .NET Framework compilan a .exe).
    private static readonly string[] AssemblyExtensions = { ".dll", ".exe" };

    public static IReadOnlyList<string> Discover(string path)
    {
        if (File.Exists(path) && HasAssemblyExtension(path))
            return new[] { path };

        if (Directory.Exists(path))
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(HasAssemblyExtension)
                .Where(p => !IsInExcludedDirectory(p, path))
                .ToList();
            return DropApphostExes(files);
        }

        throw new FileNotFoundException($"Ruta no valida para descubrimiento: {path}");
    }

    private static bool HasAssemblyExtension(string path) =>
        AssemblyExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Descarta un <c>X.exe</c> cuando junto a el hay un <c>X.dll</c>: en .NET moderno el .exe es un
    /// apphost nativo (lanzador) y el codigo gestionado esta en el .dll. En .NET Framework solo existe
    /// el .exe (sin .dll hermano), por lo que se conserva y se analiza.
    /// </summary>
    private static IReadOnlyList<string> DropApphostExes(IReadOnlyList<string> files)
    {
        var dllStems = new HashSet<string>(
            files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(Stem),
            StringComparer.OrdinalIgnoreCase);

        return files
            .Where(f => !(f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && dllStems.Contains(Stem(f))))
            .ToList();

        static string Stem(string p) =>
            Path.Combine(Path.GetDirectoryName(p)!, Path.GetFileNameWithoutExtension(p));
    }

    /// <summary>Devuelve true si algun segmento de directorio de <paramref name="filePath"/>
    /// (relativo a <paramref name="root"/>) esta en la lista de exclusion.</summary>
    private static bool IsInExcludedDirectory(string filePath, string root)
    {
        var relative = Path.GetRelativePath(root, filePath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Se comprueban los segmentos de directorio, no el nombre del fichero (ultimo segmento).
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (ExcludedDirSegments.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
