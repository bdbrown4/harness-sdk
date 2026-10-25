using System.Reflection;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class StateStoreKeySnapshotTests
{
    [Fact]
    public void KeysIsAPublicParameterlessReadonlySnapshotOperation()
    {
        var method = Operation();
        Assert.Equal(typeof(IReadOnlyList<string>), method.ReturnType);
        Assert.False(method.IsStatic);
    }

    [Fact]
    public void KeysRetainsLiteralOrdinalNamesWithoutReadingOrConvertingValues()
    {
        var store = new StateStore();
        string[] names = ["", "2", "10", "Case", "case", "__proto__", "constructor", "雪", "a.b/c"];
        foreach (var name in names) store.Set(name, new { nested = new[] { "value" } });
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), Keys(store).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("value", store.Get<JsonElement>("雪").GetProperty("nested")[0].GetString());
    }

    [Fact]
    public void ReturnedKeysAreReadonlyAndDetachedFromLaterWritesDeletesAndClear()
    {
        var store = new StateStore();
        store.Set("original", 7);
        var first = Keys(store);
        Assert.Equal("original", Assert.Single(first));
        var mutableView = Assert.IsAssignableFrom<IList<string>>(first);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = "rewritten");
        store.Set("later", 9);
        store.Delete("original");
        Assert.Equal("original", Assert.Single(first));
        Assert.Equal("later", Assert.Single(Keys(store)));
        store.Clear();
        Assert.Equal("original", Assert.Single(first));
        Assert.Empty(Keys(store));
    }

    [Fact]
    public void EmptyKeySnapshotsAreIndependentValues()
    {
        var store = new StateStore();
        var first = Keys(store);
        var second = Keys(store);
        Assert.Empty(first);
        Assert.Empty(second);
        Assert.NotSame(first, second);
        store.Set("next", true);
        Assert.Empty(first);
    }

    [Fact]
    public void ExistingDetachedValueAccessAndFailedSerializationRemainControls()
    {
        var store = new StateStore();
        var value = new[] { "before" };
        store.Set("value", value);
        value[0] = "changed";
        Assert.Equal("before", store.Get<string[]>("value")![0]);
        Assert.Throws<JsonException>(() => store.Set("value", new Cyclic()));
        Assert.Equal("before", store.Get<string[]>("value")![0]);
    }

    [Fact]
    public void ExistingLiteralKeyDeletionAndClearRemainControls()
    {
        var store = new StateStore();
        store.Set("Case", 1);
        store.Set("case", 2);
        Assert.True(store.Delete("Case"));
        Assert.False(store.Delete("Case"));
        Assert.Null(store.Get("Case"));
        Assert.Equal(2, store.Get<int>("case"));
        store.Clear();
        Assert.Empty(store.GetAll());
    }

    private static MethodInfo Operation()
    {
        var method = typeof(StateStore).GetMethod("Keys", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        Assert.NotNull(method);
        return method;
    }
    private static IReadOnlyList<string> Keys(StateStore store) => Assert.IsAssignableFrom<IReadOnlyList<string>>(Operation().Invoke(store, null));
    private sealed class Cyclic { public Cyclic Self => this; }
}
