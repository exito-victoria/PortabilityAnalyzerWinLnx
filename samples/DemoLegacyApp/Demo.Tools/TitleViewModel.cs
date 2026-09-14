namespace Demo.Tools.ViewModels;

/// <summary>ViewModel concreto: Windows por HERENCIA de ViewModelBase (que es Windows). No tiene
/// hallazgo propio del catálogo; solo la propagación transitiva puede clasificarlo bien.</summary>
public sealed class TitleViewModel : ViewModelBase
{
    public string Title { get; set; } = string.Empty;
}
