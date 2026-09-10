namespace Demo.Domain;

/// <summary>Lógica de negocio pura y portable: calcula totales e impuestos.</summary>
public static class InvoiceCalculator
{
    public static decimal Subtotal(Invoice invoice) =>
        invoice.Lines.Sum(l => l.Total);

    public static decimal Tax(Invoice invoice, decimal rate) =>
        Math.Round(Subtotal(invoice) * rate, 2);

    public static decimal GrandTotal(Invoice invoice, decimal taxRate) =>
        Subtotal(invoice) + Tax(invoice, taxRate);
}
