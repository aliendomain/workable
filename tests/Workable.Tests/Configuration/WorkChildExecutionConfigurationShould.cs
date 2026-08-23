using System.Collections.Frozen;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Configuration")]
public sealed class WorkChildExecutionConfigurationShould
{
    [Fact]
    public void ValidateNamesAndCanonicalizeSnapshots()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkChildExecutionConfiguration.Default.AllowAdditional(null!));
        Assert.Throws<ArgumentException>(() =>
            WorkChildExecutionConfiguration.Default.AllowAdditional("valid", " "));

        var emptyCopy = new WorkChildExecutionConfiguration
        {
            AllowedDefinitionNames = new HashSet<string>(),
        };
        Assert.Same(WorkChildExecutionConfiguration.Default, emptyCopy.Snapshot());
        Assert.Same(
            WorkChildExecutionConfiguration.Default,
            WorkChildExecutionConfiguration.Default.Snapshot());

        var frozen = WorkChildExecutionConfiguration.Default.AllowAdditional("child");
        Assert.IsAssignableFrom<FrozenSet<string>>(frozen.AllowedDefinitionNames);
        Assert.Same(frozen, frozen.Snapshot());

        var mutable = new WorkChildExecutionConfiguration
        {
            AllowedDefinitionNames = new HashSet<string>(StringComparer.Ordinal) { "Child" },
        };
        var snapshot = mutable.Snapshot();
        Assert.NotSame(mutable, snapshot);
        Assert.IsAssignableFrom<FrozenSet<string>>(snapshot.AllowedDefinitionNames);
        Assert.True(snapshot.Allows("child"));
    }
}
