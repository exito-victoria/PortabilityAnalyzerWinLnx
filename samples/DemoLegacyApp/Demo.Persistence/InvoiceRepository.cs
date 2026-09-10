using Demo.Domain;

namespace Demo.Persistence;

/// <summary>Repositorio en memoria: lógica de datos portable, sin dependencias de Windows.</summary>
public sealed class InvoiceRepository
{
    private readonly Dictionary<string, Invoice> _store = new(StringComparer.OrdinalIgnoreCase);

    public void Save(Invoice invoice) => _store[invoice.Number] = invoice;

    public Invoice? Find(string number) => _store.TryGetValue(number, out var i) ? i : null;

    public IReadOnlyCollection<Invoice> All() => _store.Values.ToList();
}
