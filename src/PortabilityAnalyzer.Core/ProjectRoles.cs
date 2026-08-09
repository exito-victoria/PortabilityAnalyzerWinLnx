namespace PortabilityAnalyzer.Core;

/// <summary>Papel de un proyecto/ensamblado a efectos de la recomendacion multiplataforma.</summary>
public enum ProjectRole
{
    /// <summary>Sin papel especial.</summary>
    Normal,
    /// <summary>Debe convertirse en API multiplataforma (prioridad maxima).</summary>
    ObligatorioMultiplataforma,
    /// <summary>De terceros: no se puede migrar ni modificar (responsabilidad del proveedor).</summary>
    NoModificable,
    /// <summary>Nuestra pero hay que dividirla: extraer lo dependiente de Windows a un proyecto nuevo.</summary>
    DivisiblePorUI
}

/// <summary>
/// Roles de proyecto configurables (fichero <c>--roles</c>). Asocia nombres de proyecto/ensamblado con
/// su papel, para que la recomendacion de arquitectura y las restricciones los tengan en cuenta.
/// </summary>
public sealed record ProjectRoles
{
    public IReadOnlyList<string> ObligatorioMultiplataforma { get; init; } = new List<string>();
    public IReadOnlyList<string> NoModificables { get; init; } = new List<string>();
    public IReadOnlyList<string> DivisiblePorUI { get; init; } = new List<string>();

    public bool IsEmpty =>
        ObligatorioMultiplataforma.Count == 0 && NoModificables.Count == 0 && DivisiblePorUI.Count == 0;

    /// <summary>Papel de un ensamblado/proyecto por nombre (coincidencia flexible, sin distinguir mayusculas).</summary>
    public ProjectRole RoleOf(string name)
    {
        if (Matches(NoModificables, name)) return ProjectRole.NoModificable;
        if (Matches(ObligatorioMultiplataforma, name)) return ProjectRole.ObligatorioMultiplataforma;
        if (Matches(DivisiblePorUI, name)) return ProjectRole.DivisiblePorUI;
        return ProjectRole.Normal;
    }

    private static bool Matches(IReadOnlyList<string> list, string name) =>
        list.Any(x => !string.IsNullOrWhiteSpace(x) &&
                      (name.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                       name.Contains(x, StringComparison.OrdinalIgnoreCase) ||
                       x.Contains(name, StringComparison.OrdinalIgnoreCase)));
}
