using System.Windows;
using System.Windows.Controls;
using Demo.Domain;

namespace Demo.App;

/// <summary>Punto de entrada WPF. DEPENDENCIA WINDOWS: WPF (System.Windows) y el apartamento STA solo
/// existen en Windows; esta es la capa de presentación, que se mantiene en el ejecutable Windows.</summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        var vm = new InvoiceViewModel();
        vm.Add("Licencia", 2, 100m);

        var app = new Application();
        var window = new Window
        {
            Title = "Demo Legacy App",
            Width = 480,
            Height = 320,
            Content = new TextBlock { Text = $"Subtotal: {vm.Subtotal:C}" }
        };
        app.Run(window);
    }
}
