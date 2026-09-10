namespace Demo.Domain;

/// <summary>Modelo de dominio puro. Sin dependencias del sistema operativo (portable).</summary>
public sealed class Invoice
{
    public string Number { get; init; } = string.Empty;
    public string Customer { get; init; } = string.Empty;
    public IReadOnlyList<InvoiceLine> Lines { get; init; } = new List<InvoiceLine>();
}

public sealed record InvoiceLine(string Description, int Quantity, decimal UnitPrice)
{
    public decimal Total => Quantity * UnitPrice;
}
