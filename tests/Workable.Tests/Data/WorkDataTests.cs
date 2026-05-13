using System.Text.Json;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkData")]
public sealed class WorkDataTests
{
    [Fact]
    public void WorkInputEmptyHasNoJsonPayload()
    {
        Assert.Null(WorkInput.Empty.Json);
        Assert.Null(WorkInput.Empty.ClrType);
        Assert.Equal("application/json", WorkInput.Empty.ContentType);
        Assert.Null(WorkInput.Empty.ToValue<SamplePayload>());
    }

    [Fact]
    public void WorkInputFromJsonStoresJsonTypeAndIdentityMetadata()
    {
        var subject = new WorkSubjectId("user", "user-123");
        var concurrencyKey = new WorkConcurrencyKey("tenant", "tenant-456");
        var input = WorkInput.FromJson(
            """{"message":"hello"}""",
            typeof(SamplePayload),
            subject,
            concurrencyKey);

        Assert.Equal("""{"message":"hello"}""", input.Json);
        Assert.Equal(typeof(SamplePayload).AssemblyQualifiedName, input.ClrType);
        Assert.Equal(subject, input.SubjectId);
        Assert.Equal(concurrencyKey, input.ConcurrencyKey);
        Assert.Equal("hello", input.ToValue<SamplePayload>()?.Message);
    }

    [Fact]
    public void WorkInputFromValueUsesWebJsonDefaultsAndCanRoundTrip()
    {
        var input = WorkInput.FromValue(new SamplePayload("hello"));

        Assert.Equal("""{"message":"hello"}""", input.Json);
        Assert.Equal(typeof(SamplePayload).AssemblyQualifiedName, input.ClrType);
        Assert.Equal("hello", input.ToValue<SamplePayload>()?.Message);
    }

    [Fact]
    public void WorkInputFromValueCanUseCustomJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
        };

        var input = WorkInput.FromValue(new SamplePayload("hello"), options);

        Assert.Equal("""{"Message":"hello"}""", input.Json);
        Assert.Equal("hello", input.ToValue<SamplePayload>(options)?.Message);
    }

    [Fact]
    public void WorkInputWithSubjectAndConcurrencyKeyReturnUpdatedCopies()
    {
        var subject = new WorkSubjectId("order", "order-123");
        var concurrencyKey = new WorkConcurrencyKey("store", "store-456");

        var input = WorkInput.Empty
            .WithSubject(subject)
            .WithConcurrencyKey(concurrencyKey);

        Assert.Null(WorkInput.Empty.SubjectId);
        Assert.Null(WorkInput.Empty.ConcurrencyKey);
        Assert.Equal(subject, input.SubjectId);
        Assert.Equal(concurrencyKey, input.ConcurrencyKey);
    }

    [Fact]
    public void WorkIdentityTypesShareWorkKeyShapeWithoutSharingSemantics()
    {
        IWorkKey subject = new WorkSubjectId("customer", "customer-123");
        IWorkKey concurrencyKey = new WorkConcurrencyKey("tenant", "tenant-456");
        IWorkKey identifier = new WorkIdentifier("invoice", "invoice-789");

        Assert.Equal("customer", subject.Type);
        Assert.Equal("customer-123", subject.Value);
        Assert.IsType<WorkSubjectId>(subject);
        Assert.IsType<WorkConcurrencyKey>(concurrencyKey);
        Assert.IsType<WorkIdentifier>(identifier);
    }

    [Fact]
    public void WorkOutputEmptyHasNoJsonPayload()
    {
        Assert.Null(WorkOutput.Empty.Json);
        Assert.Null(WorkOutput.Empty.ClrType);
        Assert.Equal("application/json", WorkOutput.Empty.ContentType);
        Assert.Null(WorkOutput.Empty.ToValue<SamplePayload>());
    }

    [Fact]
    public void WorkOutputFromJsonStoresJsonAndTypeMetadata()
    {
        var output = WorkOutput.FromJson("""{"message":"done"}""", typeof(SamplePayload));

        Assert.Equal("""{"message":"done"}""", output.Json);
        Assert.Equal(typeof(SamplePayload).AssemblyQualifiedName, output.ClrType);
        Assert.Equal("done", output.ToValue<SamplePayload>()?.Message);
    }

    [Fact]
    public void WorkOutputFromValueUsesWebJsonDefaultsAndCanRoundTrip()
    {
        var output = WorkOutput.FromValue(new SamplePayload("done"));

        Assert.Equal("""{"message":"done"}""", output.Json);
        Assert.Equal(typeof(SamplePayload).AssemblyQualifiedName, output.ClrType);
        Assert.Equal("done", output.ToValue<SamplePayload>()?.Message);
    }

    [Fact]
    public void WorkOutputFromDataCopiesPayloadMetadata()
    {
        var input = new WorkInput(
            """{"message":"copied"}""",
            typeof(SamplePayload).AssemblyQualifiedName,
            "application/custom+json");

        var output = WorkOutput.FromData(input);

        Assert.Equal(input.Json, output.Json);
        Assert.Equal(input.ClrType, output.ClrType);
        Assert.Equal(input.ContentType, output.ContentType);
    }

    [Fact]
    public void WorkOutputFromDataRejectsNullData()
    {
        Assert.Throws<ArgumentNullException>(() => WorkOutput.FromData(null!));
    }

    [Fact]
    public void WorkSchemaNoneHasNoSchemaPayload()
    {
        Assert.Null(WorkSchema.None.JsonSchema);
        Assert.Equal("application/schema+json", WorkSchema.None.ContentType);
        Assert.Null(WorkSchema.None.SchemaDialect);
    }

    [Fact]
    public void WorkSchemaFromTypeUsesModernDialectAndWebJsonNaming()
    {
        var schema = WorkSchema.FromType<SamplePayload>();

        Assert.Equal(WorkSchema.JsonSchemaDialect202012, schema.SchemaDialect);
        Assert.NotNull(schema.JsonSchema);

        using var document = JsonDocument.Parse(schema.JsonSchema);
        var root = document.RootElement;

        Assert.Equal(WorkSchema.JsonSchemaDialect202012, root.GetProperty("$schema").GetString());
        Assert.True(root.GetProperty("properties").TryGetProperty("message", out _));
        Assert.False(root.GetProperty("properties").TryGetProperty("Message", out _));
    }

    private sealed record SamplePayload(string Message);
}
