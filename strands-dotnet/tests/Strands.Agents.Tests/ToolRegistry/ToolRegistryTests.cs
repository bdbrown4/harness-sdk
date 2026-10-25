using System.Text.Json;

namespace Strands.Agents.Tests.ToolRegistries;

public sealed class ToolRegistryTests
{
    [Fact]
    public void MembershipSnapshotsCannotMutateOrFollowLaterChanges()
    {
        var first = new RegistryTool("first"); var second = new RegistryTool("second");
        var registry = new ToolRegistry([first]);
        var snapshot = registry.Items;
        registry.Add(second);
        Assert.Equal(new ITool[] { first }, snapshot);
        Assert.Equal(new ITool[] { first, second }, registry.Items);
        Assert.Throws<NotSupportedException>(() => ((IList<ITool>)snapshot)[0] = second);
        Assert.Same(first, registry.Get("first"));
        Assert.True(registry.TryGet("second", out var found)); Assert.Same(second, found);
        Assert.False(registry.TryGet("missing", out var missing)); Assert.Null(missing);
        Assert.Null(registry.Get("FIRST"));
    }

    [Fact]
    public void AddRangeRejectsTheWholeConflictingBatch()
    {
        var original = new RegistryTool("original"); var registry = new ToolRegistry([original]);
        Assert.Throws<ArgumentException>(() => registry.AddRange([new RegistryTool("new"), new RegistryTool("original")]));
        Assert.Equal(new ITool[] { original }, registry.Items);
        Assert.Throws<ArgumentException>(() => registry.AddRange([new RegistryTool("same"), new RegistryTool("same")]));
        Assert.Equal(new ITool[] { original }, registry.Items);
    }

    [Fact]
    public void ReplacementKeepsSlotsAndRangeUsesItsLastExactReplacement()
    {
        var first = new RegistryTool("first"); var second = new RegistryTool("second"); var replacement = new RegistryTool("first");
        var registry = new ToolRegistry([first, second]);
        registry.Replace(replacement);
        Assert.Equal(new ITool[] { replacement, second }, registry.Items);
        var last = new RegistryTool("first"); var added = new RegistryTool("third");
        registry.AddOrReplace(new ITool[] { new RegistryTool("first"), added, last });
        Assert.Equal(new ITool[] { last, second, added }, registry.Items);
        registry.AddOrReplace(first);
        Assert.Same(first, registry.Get("first"));
        Assert.Throws<KeyNotFoundException>(() => registry.Replace(new RegistryTool("missing")));
        Assert.Equal(3, registry.Items.Count);
    }

    [Fact]
    public void ConflictPolicyIsExplicitAndDefaultMatchesBothUpstreams()
    {
        var strict = new ToolRegistry([new RegistryTool("foo-bar")]);
        Assert.Equal(ToolNameConflictPolicy.RejectNormalized, strict.NameConflictPolicy);
        Assert.Throws<ArgumentException>(() => strict.Add(new RegistryTool("foo_bar")));
        var native = new ToolRegistry(options: new() { NameConflictPolicy = ToolNameConflictPolicy.ExactOnly });
        native.AddRange([new RegistryTool("foo-bar"), new RegistryTool("foo_bar")]);
        Assert.Equal(2, native.Items.Count);
        Assert.Equal(ToolNameConflictPolicy.ExactOnly, native.NameConflictPolicy);
        Assert.Throws<ArgumentException>(() => native.Add(new RegistryTool("foo-bar")));
    }

    [Theory]
    [InlineData(ToolNameResolution.Exact)]
    [InlineData(ToolNameResolution.UnderscoreAlias)]
    [InlineData(ToolNameResolution.TypeScriptDirect)]
    public void ExactWinsAcrossEveryResolutionPolicy(ToolNameResolution mode)
    {
        var exact = new RegistryTool("my_tool");
        var registry = new ToolRegistry([new RegistryTool("my-tool"), new RegistryTool("MY_TOOL"), exact], new() { NameConflictPolicy = ToolNameConflictPolicy.ExactOnly });
        Assert.Same(exact, registry.Resolve("my_tool", mode));
    }

    [Fact]
    public void AliasThenCaseFallbackFollowsThePinnedOrder()
    {
        var firstCase = new RegistryTool("MY_TOOL"); var alias = new RegistryTool("my-tool"); var first = new RegistryTool("Case");
        var registry = new ToolRegistry([firstCase, alias, first, new RegistryTool("CASE")]);
        Assert.Same(alias, registry.Resolve("my_tool", ToolNameResolution.UnderscoreAlias));
        Assert.Same(alias, registry.Resolve("my_tool", ToolNameResolution.TypeScriptDirect));
        Assert.Same(first, registry.Resolve("case", ToolNameResolution.TypeScriptDirect));
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("case", ToolNameResolution.UnderscoreAlias));
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("my_tool"));
    }

    [Fact]
    public void RemoveAndClearNeverDisposeBorrowedTools()
    {
        var first = new RegistryTool("first"); var second = new RegistryTool("second"); var registry = new ToolRegistry([first, second]);
        Assert.False(registry.Remove("FIRST")); Assert.True(registry.Remove("first")); Assert.False(registry.Remove("first"));
        registry.AddOrReplace(new RegistryTool("second")); registry.Clear();
        Assert.Empty(registry.Items); Assert.False(first.Disposed); Assert.False(second.Disposed);
    }
}

internal sealed class RegistryTool(string name) : ITool, IDisposable, IAsyncDisposable
{
    public ToolSpec Spec { get; } = new(name, "Test tool.", JsonSerializer.SerializeToElement(new { type = "object" }));
    public bool Disposed { get; private set; }
    public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("called");
    public void Dispose() => Disposed = true;
    public ValueTask DisposeAsync() { Disposed = true; return default; }
}
