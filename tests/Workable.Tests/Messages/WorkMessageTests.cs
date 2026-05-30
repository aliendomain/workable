using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkMessages")]
public sealed class WorkMessageTests
{
    [Fact]
    public void InfoCreatesInformationalMessage()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var message = WorkMessage.Info("sample.info", "Everything is fine.", "sample");
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.Equal("sample.info", message.Code);
        Assert.Equal(WorkMessageSeverity.Information, message.Severity);
        Assert.Equal("Everything is fine.", message.Text);
        Assert.Equal("sample", message.Target);
        Assert.Null(message.Metadata);
        Assert.InRange(message.OccurredAt, before, after);
    }

    [Fact]
    public void WarningCreatesWarningMessage()
    {
        var message = WorkMessage.Warning("sample.warning", "Something needs attention.", "sample");

        Assert.Equal("sample.warning", message.Code);
        Assert.Equal(WorkMessageSeverity.Warning, message.Severity);
        Assert.Equal("Something needs attention.", message.Text);
        Assert.Equal("sample", message.Target);
        Assert.Null(message.Metadata);
    }

    [Fact]
    public void ErrorCreatesErrorMessage()
    {
        var message = WorkMessage.Error("sample.error", "Something failed.", "sample");

        Assert.Equal("sample.error", message.Code);
        Assert.Equal(WorkMessageSeverity.Error, message.Severity);
        Assert.Equal("Something failed.", message.Text);
        Assert.Equal("sample", message.Target);
        Assert.Null(message.Metadata);
    }

    [Fact]
    public void AdditionalFactoriesCreateExpectedSeverities()
    {
        Assert.Equal(WorkMessageSeverity.Trace, WorkMessage.Trace("sample.trace", "Trace.", "sample").Severity);
        Assert.Equal(WorkMessageSeverity.Debug, WorkMessage.Debug("sample.debug", "Debug.", "sample").Severity);
        Assert.Equal(WorkMessageSeverity.Information, WorkMessage.Information("sample.information", "Information.", "sample").Severity);
        Assert.Equal(WorkMessageSeverity.Critical, WorkMessage.Critical("sample.critical", "Critical.", "sample").Severity);
    }

    [Fact]
    public void WorkExecutionResultSuccessUsesOutputAndMessages()
    {
        var output = WorkOutput.FromValue(new ResultPayload("ok"));
        var messages = new[] { WorkMessage.Info("sample.info", "Finished.") };

        var result = WorkExecutionResult.Success(output, messages);

        Assert.Same(output, result.Output);
        Assert.Equal(messages, result.Messages);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void WorkExecutionResultFailureUsesOutputAndMessages()
    {
        var output = WorkOutput.FromValue(new ResultPayload("failed"));
        var messages = new[] { WorkMessage.Error("sample.error", "Failed.") };

        var result = WorkExecutionResult.Failure(messages, output);

        Assert.Same(output, result.Output);
        Assert.Equal(messages, result.Messages);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void WorkExecutionResultHasErrorsOnlyWhenErrorMessagesExist()
    {
        var warningOnly = WorkExecutionResult.Success(messages:
        [
            WorkMessage.Info("sample.info", "Info."),
            WorkMessage.Warning("sample.warning", "Warning."),
        ]);
        var withError = WorkExecutionResult.Success(messages:
        [
            WorkMessage.Warning("sample.warning", "Warning."),
            WorkMessage.Error("sample.error", "Error."),
        ]);
        var withCritical = WorkExecutionResult.Success(messages:
        [
            WorkMessage.Debug("sample.debug", "Debug."),
            WorkMessage.Critical("sample.critical", "Critical."),
        ]);

        Assert.False(warningOnly.HasErrors);
        Assert.True(withError.HasErrors);
        Assert.True(withCritical.HasErrors);
    }

    private sealed record ResultPayload(string Value);
}
