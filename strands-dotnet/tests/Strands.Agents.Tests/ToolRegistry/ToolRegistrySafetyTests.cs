using System.Globalization;
using System.Text.Json;

namespace Strands.Agents.Tests.ToolRegistries;

public sealed class ToolRegistrySafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEnumerationFailureCannotPublishAnyPartOfABatch(bool replace)
    {
        var first = new RegistryTool("first"); var registry = new ToolRegistry([first]);
        var error = new IOException("enumeration failure");
        IEnumerable<ITool> Pending() { yield return new RegistryTool("first"); yield return new RegistryTool("new"); throw error; }
        var observed = Assert.Throws<IOException>(() => { if (replace) registry.AddOrReplace(Pending()); else registry.AddRange(Pending()); });
        Assert.Same(error, observed); Assert.Equal(new ITool[] { first }, registry.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGetterFailureCannotPublishAnyPartOfABatch(bool replace)
    {
        var first = new RegistryTool("first"); var registry = new ToolRegistry([first]);
        var error = new IOException("specification failure");
        var pending = new ITool[] { new RegistryTool("new"), new GetterTool(() => throw error) };
        var observed = Assert.Throws<IOException>(() => { if (replace) registry.AddOrReplace(pending); else registry.AddRange(pending); });
        Assert.Same(error, observed); Assert.Equal(new ITool[] { first }, registry.Items);
    }

    [Fact]
    public void ReplacingThenEncounteringANormalizedConflictRollsBackTheEntireRange()
    {
        var first = new RegistryTool("first"); var alias = new RegistryTool("with-hyphen"); var registry = new ToolRegistry([first, alias]);
        Assert.Throws<ArgumentException>(() => registry.AddOrReplace(new ITool[] { new RegistryTool("first"), new RegistryTool("with_hyphen") }));
        Assert.Equal(new ITool[] { first, alias }, registry.Items);
    }

    [Fact]
    public void MixedHyphensAndUnderscoresUseTheSourceOneWayAliasRule()
    {
        var mixed = new RegistryTool("one-two_three"); var registry = new ToolRegistry([mixed]);
        Assert.Same(mixed, registry.Resolve("one_two_three", ToolNameResolution.UnderscoreAlias));
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("one-two-three", ToolNameResolution.UnderscoreAlias));
        Assert.Throws<ArgumentException>(() => registry.Add(new RegistryTool("one_two-three")));
        var underscores = new ToolRegistry([new RegistryTool("one_two")]);
        Assert.Throws<KeyNotFoundException>(() => underscores.Resolve("one-two", ToolNameResolution.TypeScriptDirect));
    }

    [Fact]
    public void RemovingAndReaddingChangesCaseFallbackOrderButReplacementDoesNot()
    {
        var first = new RegistryTool("case"); var second = new RegistryTool("CASE"); var registry = new ToolRegistry([first, second]);
        var replacement = new RegistryTool("case"); registry.Replace(replacement);
        Assert.Same(replacement, registry.Resolve("Case", ToolNameResolution.TypeScriptDirect));
        registry.Remove("case"); registry.Add(first);
        Assert.Same(second, registry.Resolve("Case", ToolNameResolution.TypeScriptDirect));
        Assert.Same(first, registry.Resolve("case", ToolNameResolution.TypeScriptDirect));
    }

    [Fact]
    public void CaseFallbackDoesNotCombineCaseAndAliasRewriting()
    {
        var registry = new ToolRegistry([new RegistryTool("MY-TOOL")]);
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("my_tool", ToolNameResolution.TypeScriptDirect));
        Assert.Equal("MY-TOOL", registry.Resolve("my-tool", ToolNameResolution.TypeScriptDirect).Spec.Name);
    }

    [Fact]
    public void CaseFallbackIsIndependentOfTheCallingCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var tool = new RegistryTool("IDENTIFIER"); var registry = new ToolRegistry([tool]);
            Assert.Same(tool, registry.Resolve("identifier", ToolNameResolution.TypeScriptDirect));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void QueriesAndSnapshotsDoNotReenterCustomSpecificationGetters()
    {
        var calls = 0; var spec = new RegistryTool("captured").Spec;
        var tool = new GetterTool(() => { calls++; return spec; }); var registry = new ToolRegistry([tool]);
        Assert.Same(tool, registry.Get("captured")); Assert.Same(tool, registry.Resolve("CAPTURED", ToolNameResolution.TypeScriptDirect));
        Assert.Same(tool, Assert.Single(registry.Items)); registry.Remove("captured");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ApplicationEnumerationAndSpecCodeRunOutsideThePublicationLock()
    {
        var registry = new ToolRegistry();
        var tool = new GetterTool(() =>
        {
            // A separate thread may safely query/mutate while this getter is being captured.
            var work = Task.Run(() => registry.Add(new RegistryTool("nested")));
            Assert.True(work.Wait(TimeSpan.FromSeconds(5)));
            return new RegistryTool("outer").Spec;
        });
        registry.Add(tool);
        Assert.Equal(2, registry.Items.Count); Assert.Same(tool, registry.Items[1]);
        Assert.Equal("nested", registry.Items[0].Spec.Name);
    }

    [Fact]
    public async Task ConcurrentDuplicateAddsPublishExactlyOneCompleteRegistration()
    {
        var registry = new ToolRegistry();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> Add(ITool tool) { await start.Task.ConfigureAwait(false); try { registry.Add(tool); return null; } catch (Exception error) { return error; } }
        var first = new RegistryTool("same"); var second = new RegistryTool("same");
        var pending = new[] { Task.Run(() => Add(first)), Task.Run(() => Add(second)) }; start.SetResult(true);
        var errors = await Task.WhenAll(pending);
        Assert.Single(errors, value => value is null); Assert.Single(errors.OfType<ArgumentException>());
        var registered = Assert.Single(registry.Items); Assert.True(ReferenceEquals(registered, first) || ReferenceEquals(registered, second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullBatchMembersAndNullSpecificationsFailAtomically(bool nullSpec)
    {
        var existing = new RegistryTool("existing"); var registry = new ToolRegistry([existing]);
        ITool invalid = nullSpec ? new GetterTool(() => null!) : null!;
        Assert.ThrowsAny<ArgumentException>(() => registry.AddRange([new RegistryTool("new"), invalid]));
        Assert.Equal(new ITool[] { existing }, registry.Items);
    }

    [Fact]
    public void UnknownPoliciesFailEvenWhenAnExactNameExists()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolRegistry(options: new() { NameConflictPolicy = (ToolNameConflictPolicy)999 }));
        var registry = new ToolRegistry([new RegistryTool("tool")]);
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Resolve("tool", (ToolNameResolution)999));
    }

    [Fact]
    public void NullQueriesFailButEmptyOrWhitespaceNamesSimplyDoNotMatch()
    {
        var registry = new ToolRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Get(null!)); Assert.Throws<ArgumentNullException>(() => registry.Remove(null!));
        Assert.Throws<ArgumentNullException>(() => registry.Resolve(null!));
        Assert.Null(registry.Get("")); Assert.False(registry.Remove(" "));
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve(""));
    }

    [Fact]
    public void ToolSpecDescriptionAndNameValidationRemainTheNativeContract()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        Assert.Throws<ArgumentException>(() => new ToolSpec("name", " ", schema));
        Assert.Throws<ArgumentException>(() => new ToolSpec(new string('x', 65), "description", schema));
        Assert.Throws<ArgumentException>(() => new ToolSpec("invalid.name", "description", schema));
        var longName = new RegistryTool(new string('x', 64));
        Assert.Same(longName, new ToolRegistry([longName]).Get(longName.Spec.Name));
    }

    private sealed class GetterTool(Func<ToolSpec> get) : ITool
    {
        public ToolSpec Spec => get();
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("called");
    }
}
