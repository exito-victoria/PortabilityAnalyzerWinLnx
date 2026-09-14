using Demo.Tools.ViewModels;

namespace Demo.Tools;

/// <summary>Utilidad que USA un ViewModel (TitleViewModel). Windows por USO transitivo, aunque no tenga
/// hallazgo propio ni herede de nada de Windows.</summary>
public static class BDUtils
{
    public static TitleViewModel BuildTitle(string title) => new TitleViewModel { Title = title };
}
