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

    /// <summary>Proyectos adicionales que se quieren separar en Windows/Multi aunque no encajen en un rol
    /// concreto. Deja el generador de division "preparado para cualquier proyecto que pueda hacerse portable".</summary>
    public IReadOnlyList<string> Separables { get; init; } = new List<string>();

    public bool IsEmpty =>
        ObligatorioMultiplataforma.Count == 0 && NoModificables.Count == 0 &&
        DivisiblePorUI.Count == 0 && Separables.Count == 0;

    /// <summary>Papel de un ensamblado/proyecto por nombre (coincidencia flexible, sin distinguir mayusculas).</summary>
    public ProjectRole RoleOf(string name)
    {
        if (Matches(NoModificables, name)) return ProjectRole.NoModificable;
        if (Matches(ObligatorioMultiplataforma, name)) return ProjectRole.ObligatorioMultiplataforma;
        if (Matches(DivisiblePorUI, name)) return ProjectRole.DivisiblePorUI;
        return ProjectRole.Normal;
    }

    /// <summary>Indica si un proyecto debe separarse en Windows/Multi: los <c>divisiblePorUI</c> y
    /// <c>obligatorioMultiplataforma</c>, mas cualquiera listado en <c>separables</c>. Los de terceros
    /// (<c>noModificables</c>) nunca se separan.</summary>
    public bool IsSeparable(string name)
    {
        if (Matches(NoModificables, name)) return false;
        return RoleOf(name) is ProjectRole.DivisiblePorUI or ProjectRole.ObligatorioMultiplataforma
               || Matches(Separables, name);
    }

    private static bool Matches(IReadOnlyList<string> list, string name) =>
        list.Any(x => !string.IsNullOrWhiteSpace(x) &&
                      (name.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                       name.Contains(x, StringComparison.OrdinalIgnoreCase) ||
                       x.Contains(name, StringComparison.OrdinalIgnoreCase)));
}
