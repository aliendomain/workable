using System.Reflection;
using System.Text.Json;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workers")]
public sealed class WorkerConfigurationDifferenceCounterShould
{
    [Fact]
    public void CountMissingObjectPropertiesAndArrayElementDifferences()
    {
        Assert.Equal(2, Count("""{"current":1}""", """{"default":1}"""));
        Assert.Equal(2, Count("""[1,2,3]""", """[1,4]"""));
        Assert.Equal(1, Count("""[true]""", """{"value":true}"""));
    }

    private static int Count(string currentJson, string defaultJson)
    {
        using var current = JsonDocument.Parse(currentJson);
        using var defaults = JsonDocument.Parse(defaultJson);
        var method = typeof(WorkerConfigurationDifferenceCounter).GetMethod(
            "CountDifferences",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(JsonElement), typeof(JsonElement)],
            modifiers: null);

        Assert.NotNull(method);
        return Assert.IsType<int>(method.Invoke(null, [current.RootElement, defaults.RootElement]));
    }
}
