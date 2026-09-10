using System.Collections.ObjectModel;
using Demo.Domain;

namespace Demo.App;

/// <summary>ViewModel de presentación: lógica portable (sin tipos de WPF). Usa ObservableCollection,
/// que vive en el BCL común. Es candidato a moverse al núcleo portable reutilizable.</summary>
public sealed class InvoiceViewModel
{
    public ObservableCollection<InvoiceLine> Lines { get; } = new();

    public decimal Subtotal => Lines.Sum(l => l.Total);

    public void Add(string description, int quantity, decimal price) =>
        Lines.Add(new InvoiceLine(description, quantity, price));
}
