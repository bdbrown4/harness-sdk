using System.Net;
using System.Text;
using Strands.Agents.Skills;

namespace Strands.Agents.Tests.Skills;

public sealed class SkillHttpsTests
{
    [Fact]
    public async Task ReadsSplitUtf8FromStreamingResponseAndRetainsCallerClient()
    {
        using var bytes = new ChunkedStream(Encoding.UTF8.GetBytes("---\nname: pdf-processing\ndescription: Process PDFs\n---\nCafé ☕"), 1);
        using var handler = new Handler(() => new(HttpStatusCode.OK) { Content = new StreamContent(bytes) });
        using var client = new HttpClient(handler);

        var skill = await Skill.FromUrlAsync("https://skills.example.test/SKILL.md", client, strict: true);

        Assert.Equal("Café ☕", skill.Instructions);
        Assert.True(bytes.Disposed);
        Assert.False(handler.Disposed);
        Assert.Null(skill.Path);
    }

    [Fact]
    public async Task CancellationWhileReadingDisposesResponseAndDoesNotPoisonTheCatalog()
    {
        using var blocked = new BlockedStream();
        var requests = 0;
        using var handler = new Handler(() => new(HttpStatusCode.OK)
        {
            Content = Interlocked.Increment(ref requests) == 1
                ? new StreamContent(blocked)
                : new StringContent("---\nname: pdf-processing\ndescription: Process PDFs\n---\nInstructions"),
        });
        using var client = new HttpClient(handler);
        var plugin = new AgentSkills(new() { Skills = ["https://skills.example.test/SKILL.md"], HttpClient = client });
        using var cancellation = new CancellationTokenSource();
        var loading = plugin.GetAvailableSkillsAsync(cancellationToken: cancellation.Token);
        await blocked.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(blocked.Disposed);
        Assert.Equal("pdf-processing", Assert.Single(await plugin.GetAvailableSkillsAsync().WaitAsync(TimeSpan.FromSeconds(10))).Name);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("http://skills.example.test/SKILL.md")]
    [InlineData("file:///SKILL.md")]
    [InlineData("https://")]
    public async Task RejectsInvalidOrNonHttpsUrlsBeforeSendingARequest(string url)
    {
        using var handler = new Handler(() => throw new InvalidOperationException("No request may be sent."));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Skill.FromUrlAsync(url, client));
    }

    [Fact]
    public async Task RejectsHttpFailuresAndDisposesTheResponseContent()
    {
        using var bytes = new ChunkedStream([], 1);
        using var handler = new Handler(() => new(HttpStatusCode.NotFound) { Content = new StreamContent(bytes) });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => Skill.FromUrlAsync("https://skills.example.test/SKILL.md", client));

        Assert.True(bytes.Disposed);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnforcesReadBoundForDeclaredAndStreamingLengths(bool declaredLength)
    {
        using var bytes = new ChunkedStream(new byte[5 * 1024 * 1024 + 1], 8192);
        using var handler = new Handler(() =>
        {
            var content = new StreamContent(bytes);
            if (declaredLength) content.Headers.ContentLength = 5 * 1024 * 1024 + 1;
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<IOException>(() => Skill.FromUrlAsync("https://skills.example.test/SKILL.md", client));

        Assert.True(bytes.Disposed);
    }

    [Fact]
    public async Task RejectsInvalidUtf8RatherThanSilentlyReplacingBytes()
    {
        using var bytes = new ChunkedStream([0xC3, 0x28], 1);
        using var handler = new Handler(() => new(HttpStatusCode.OK) { Content = new StreamContent(bytes) });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<DecoderFallbackException>(() => Skill.FromUrlAsync("https://skills.example.test/SKILL.md", client));

        Assert.True(bytes.Disposed);
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response());
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private class ChunkedStream(byte[] bytes, int chunkLength) : Stream
    {
        private int _position;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(Math.Min(count, chunkLength), bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, available);
            _position += available;
            return available;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BlockedStream() : ChunkedStream([], 1)
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
