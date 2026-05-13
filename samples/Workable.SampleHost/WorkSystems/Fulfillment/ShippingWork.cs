using Workable;

namespace Workable.SampleHost.Fulfillment;

public sealed record ShipmentLabelInput(
    string OrderId,
    Address ShipTo,
    PackageDimensions Package,
    ShippingServiceLevel ServiceLevel = ShippingServiceLevel.Ground);

public sealed record Address(
    string Name,
    string Line1,
    string City,
    string Region,
    string PostalCode,
    string CountryCode);

public sealed record PackageDimensions(
    decimal WeightOunces,
    decimal LengthInches,
    decimal WidthInches,
    decimal HeightInches);

public sealed record ShipmentLabelOutput(
    string TrackingNumber,
    string LabelUrl,
    ShippingServiceLevel ServiceLevel,
    DateTimeOffset PurchasedAt);

public enum ShippingServiceLevel
{
    Ground,
    TwoDay,
    Overnight,
}

[WorkMetadata("shipping.label.purchase", "Fulfillment:Shipping", "Purchases a shipping label for a package.")]
public sealed class ShipmentLabelWork : IWorkExecutor<ShipmentLabelInput, ShipmentLabelOutput>
{
    public Task<WorkExecutionResult<ShipmentLabelOutput>> Execute(
        IWorkExecutionContext context,
        ShipmentLabelInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult<ShipmentLabelOutput>.Success(new ShipmentLabelOutput(
            $"1Z{Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999)}",
            $"/labels/{input.OrderId}/{Guid.NewGuid():N}.pdf",
            input.ServiceLevel,
            DateTimeOffset.UtcNow)));
}

public sealed record CarrierRateShopInput(
    Address Origin,
    Address Destination,
    PackageDimensions Package,
    IReadOnlyList<string> CarrierCodes);

public sealed record CarrierRateShopOutput(
    string SelectedCarrier,
    ShippingServiceLevel SelectedService,
    decimal EstimatedCost,
    DateTimeOffset RatedAt);

[WorkMetadata("shipping.rate.shop", "Fulfillment:Shipping", "Compares carrier rates and selects a sample shipping service.")]
public sealed class CarrierRateShopWork : IWorkExecutor<CarrierRateShopInput, CarrierRateShopOutput>
{
    public Task<WorkExecutionResult<CarrierRateShopOutput>> Execute(
        IWorkExecutionContext context,
        CarrierRateShopInput input,
        CancellationToken cancellationToken)
    {
        var carrier = input.CarrierCodes.Count == 0
            ? "sample-carrier"
            : input.CarrierCodes[Random.Shared.Next(input.CarrierCodes.Count)];

        return Task.FromResult(WorkExecutionResult<CarrierRateShopOutput>.Success(new CarrierRateShopOutput(
            carrier,
            ShippingServiceLevel.Ground,
            Math.Round(6 + input.Package.WeightOunces * 0.11m, 2),
            DateTimeOffset.UtcNow)));
    }
}

public sealed record PackageManifestInput(
    string TrailerId,
    IReadOnlyList<string> TrackingNumbers,
    DateTimeOffset DepartureTime);

public sealed record PackageManifestOutput(
    string ManifestId,
    int PackageCount,
    DateTimeOffset ClosedAt);

[WorkMetadata("shipping.manifest.close", "Fulfillment:Shipping", "Closes a trailer manifest for outbound packages.")]
public sealed class PackageManifestWork : IWorkExecutor<PackageManifestInput, PackageManifestOutput>
{
    public Task<WorkExecutionResult<PackageManifestOutput>> Execute(
        IWorkExecutionContext context,
        PackageManifestInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult<PackageManifestOutput>.Success(
            new PackageManifestOutput(
                $"manifest_{Guid.NewGuid():N}"[..22],
                input.TrackingNumbers.Count,
                DateTimeOffset.UtcNow)));
}
