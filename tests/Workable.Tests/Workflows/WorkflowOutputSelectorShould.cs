using System.Text.Json.Serialization;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowOutputSelectorShould
{
    [Fact]
    public void TranslateRootNestedAttributedAndConvertedPaths()
    {
        Assert.Null(WorkflowOutputSelector.Create<SelectorModel, SelectorModel>(model => model).JsonPointer);
        Assert.Equal("/child/display~0~1name", WorkflowOutputSelector
            .Create<SelectorModel, string>(model => model.Child.DisplayName)
            .JsonPointer);
        Assert.Equal("/count", WorkflowOutputSelector
            .Create<SelectorModel, long>(model => checked((long)model.Count))
            .JsonPointer);
        Assert.Equal("/child", WorkflowOutputSelector
            .Create<SelectorModel, object>(model => (object)model.Child)
            .JsonPointer);
    }

    [Fact]
    public void RejectNullUnrootedAndComputedSelectors()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkflowOutputSelector.Create<SelectorModel, string>(null!));
        Assert.Throws<NotSupportedException>(() =>
            WorkflowOutputSelector.Create<SelectorModel, DateTime>(_ => DateTime.Now));
        Assert.Throws<NotSupportedException>(() =>
            WorkflowOutputSelector.Create<SelectorModel, string>(model => model.Child.DisplayName.ToUpperInvariant()));
    }

    private sealed record SelectorModel(SelectorChild Child, int Count);

    private sealed record SelectorChild(
        [property: JsonPropertyName("display~/name")] string DisplayName);
}
