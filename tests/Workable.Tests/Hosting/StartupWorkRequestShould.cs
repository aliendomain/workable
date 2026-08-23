using System.Text.Json;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class StartupWorkRequestShould
{
    [Fact]
    public void PreserveNullRawAndTypedInputsForBothAddressingForms()
    {
        var definitionId = WorkDefinitionId.New();
        var rawInput = WorkInput.FromValue(new { Value = "raw" }, WorkData.DefaultJsonOptions);

        Assert.Null(StartupWorkRequest.ForDefinition<object?>(definitionId, null).Input);
        Assert.Same(rawInput, StartupWorkRequest.ForDefinition(definitionId, rawInput).Input);
        Assert.Equal(
            "typed",
            JsonSerializer.Deserialize<StartupInput>(
                StartupWorkRequest.ForDefinition(definitionId, new StartupInput("typed")).Input!.Json!,
                WorkData.DefaultJsonOptions)!
                .Value);

        Assert.Null(StartupWorkRequest.ForName<object?>("startup", null).Input);
        Assert.Same(rawInput, StartupWorkRequest.ForName("startup", rawInput).Input);
        Assert.Equal(
            "typed",
            JsonSerializer.Deserialize<StartupInput>(
                StartupWorkRequest.ForName("startup", new StartupInput("typed")).Input!.Json!,
                WorkData.DefaultJsonOptions)!
                .Value);
    }

    private sealed record StartupInput(string Value);
}
