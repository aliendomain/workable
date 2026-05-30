using Workable;

namespace Workable.Tests;

[Trait("Category", "Outcomes")]
public sealed class WorkCompletionStatusTests
{
    [Theory]
    [InlineData(WorkCompletionStatus.Completed, true)]
    [InlineData(WorkCompletionStatus.Failed, true)]
    [InlineData(WorkCompletionStatus.Interrupted, true)]
    [InlineData(WorkCompletionStatus.Canceled, true)]
    [InlineData(WorkCompletionStatus.Invalid, true)]
    [InlineData(WorkCompletionStatus.NotFound, true)]
    [InlineData(WorkCompletionStatus.Executing, false)]
    [InlineData(WorkCompletionStatus.Paused, false)]
    public void IsFinalMatchesDomainExpectation(WorkCompletionStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsFinal());
    }
}
