using Workable;

namespace Workable.SampleHost.Operations;

public sealed record InvoiceGenerateInput(
    CustomerReference Customer,
    IReadOnlyList<InvoiceLineInput> Lines,
    CurrencyCode Currency,
    decimal TaxRate,
    DateOnly? DueDate,
    bool SendReceipt = false);

public sealed record CustomerReference(
    string CustomerId,
    string Name,
    string? BillingEmail);

public sealed record InvoiceLineInput(
    string Description,
    int Quantity,
    decimal UnitPrice,
    bool Taxable = true);

public sealed record InvoiceGenerateOutput(
    string InvoiceNumber,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    DateOnly DueDate);

public enum CurrencyCode
{
    USD,
    EUR,
    GBP,
}

[WorkMetadata("billing.invoice.generate", "Billing:Invoices", "Generates an invoice from nested customer and line-item input.")]
public sealed class InvoiceGenerateWork : IWorkExecutor<InvoiceGenerateInput, InvoiceGenerateOutput>
{
    public Task<WorkExecutionResult<InvoiceGenerateOutput>> Execute(
        IWorkExecutionContext context,
        InvoiceGenerateInput input,
        CancellationToken cancellationToken)
    {
        var subtotal = input.Lines.Sum(line => line.Quantity * line.UnitPrice);
        var taxableSubtotal = input.Lines.Where(line => line.Taxable).Sum(line => line.Quantity * line.UnitPrice);
        var tax = Math.Round(taxableSubtotal * input.TaxRate, 2);

        return Task.FromResult(WorkExecutionResult<InvoiceGenerateOutput>.Success(new InvoiceGenerateOutput(
            $"INV-{DateTimeOffset.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
            subtotal,
            tax,
            subtotal + tax,
            input.DueDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)))));
    }
}
