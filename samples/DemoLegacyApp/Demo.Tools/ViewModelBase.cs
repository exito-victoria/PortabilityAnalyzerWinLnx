using System.Windows;

namespace Demo.Tools.ViewModels;

/// <summary>Base de ViewModels que hereda de un tipo de WPF (DependencyObject). Es de Windows por
/// herencia de un tipo base del framework: debe sembrar la propagación (sin hallazgo propio del catálogo
/// necesariamente). Todo lo que herede o use esto acaba en el lado Windows.</summary>
public abstract class ViewModelBase : DependencyObject
{
    public bool IsBusy { get; set; }
}
