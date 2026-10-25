namespace Strands.Agents.Tests;

public sealed class ModelSystemTokenCountTests
{
    [Theory]
    [InlineData("a", "b", 2L)]
    [InlineData("aaaa", "bbbb", 2L)]
    [InlineData("aaaaa", "b", 3L)]
    [InlineData("", "", 0L)]
    public async Task StructuredSystemTextIsRoundedPerBlock(string first, string second, long expected)
    {
        IModel model = new NoRequestModel();
        var request = new ModelRequest([], "ignored legacy text")
        {
            SystemPromptContent = [new TextBlock(first), new CachePointBlock(), new TextBlock(second)]
        };

        Assert.Equal(expected, await model.CountTokensAsync(request));
    }

    [Fact]
    public async Task LegacySystemStringRetainsItsWholeStringEstimate()
    {
        IModel model = new NoRequestModel();

        Assert.Equal(3L, await model.CountTokensAsync(new ModelRequest([], "aaaa\nbbbb")));
    }

    [Fact]
    public async Task ExplicitEmptyTypedSystemRetainsNativePrecedenceOverLegacyText()
    {
        IModel model = new NoRequestModel();
        var request = new ModelRequest([], "ignored legacy text") { SystemPromptContent = [] };

        Assert.Equal(0L, await model.CountTokensAsync(request));
    }

    private sealed class NoRequestModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Fallback token estimation must not invoke a model.");
    }
}
