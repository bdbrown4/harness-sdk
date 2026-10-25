using System.Text;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tests;

public sealed class FileToolsTests
{
    [Fact]
    public async Task ReadNumbersPagesThroughTheInjectedSandbox()
    {
        var sandbox = new TextSandbox("one\ntwo\nthree\nfour");
        var result = await Invoke(FileTools.CreateRead(sandbox), new { path = "/project/file.txt", offset = 2, limit = 2 });

        Assert.Equal("     2\ttwo\n     3\tthree\n[Showing lines 2-3 of 4. Use offset/limit to read more.]", result);
        Assert.Equal("/project/file.txt", sandbox.LastPath);
    }

    [Fact]
    public async Task ReadPastTheEndExplainsTheOffset()
    {
        var result = await Invoke(FileTools.CreateRead(new TextSandbox("one\ntwo")), new { path = "/file.txt", offset = 4 });
        Assert.Equal("[File has 2 lines; offset 4 is past the end.]", result);
    }

    [Theory]
    [InlineData("relative/file.txt")]
    [InlineData("/project/../secret")]
    [InlineData("C:\\project\\..\\secret")]
    public async Task RejectsRelativeAndTraversalPathsBeforeSandboxAccess(string path)
    {
        var sandbox = new TextSandbox("text");
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(FileTools.CreateRead(sandbox), new { path }));
        Assert.Null(sandbox.LastPath);
    }

    [Theory]
    [InlineData(0, 1, "     1\ttext")]
    [InlineData(1, 0, "[File has 1 lines; offset 1 is past the end.]")]
    [InlineData(1, -1, "[File has 1 lines; offset 1 is past the end.]")]
    [InlineData(1, 10001, "     1\ttext")]
    public async Task IntegralPagingAcceptsZeroNegativeAndLargeWindows(int offset, int limit, string expected)
    {
        Assert.Equal(expected, await Invoke(FileTools.CreateRead(new TextSandbox("text")), new { path = "/file", offset, limit }));
    }

    [Fact]
    public async Task WriteUsesSandboxAndCountsLines()
    {
        var sandbox = new TextSandbox();
        Assert.Equal("Wrote 2 lines to /file.txt.", await Invoke(FileTools.CreateWrite(sandbox), new { path = "/file.txt", content = "one\ntwo" }));
        Assert.Equal("one\ntwo", sandbox.Content);
        Assert.Equal("Wrote 0 lines to /file.txt.", await Invoke(FileTools.CreateWrite(sandbox), new { path = "/file.txt", content = "" }));
    }

    [Fact]
    public async Task EditReplacesOnlyAnExactUniqueMatch()
    {
        var sandbox = new TextSandbox("first\nneedle\nlast");
        Assert.Equal("Edited /file.txt.", await Invoke(FileTools.CreateEdit(sandbox), new { path = "/file.txt", old_str = "needle", new_str = "new" }));
        Assert.Equal("first\nnew\nlast", sandbox.Content);
    }

    [Theory]
    [InlineData("missing", "needle")]
    [InlineData("needle needle", "needle")]
    [InlineData("text", "")]
    public async Task FailedEditDoesNotWrite(string content, string oldText)
    {
        var sandbox = new TextSandbox(content);
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(FileTools.CreateEdit(sandbox), new { path = "/file.txt", old_str = oldText, new_str = "new" }));
        Assert.Equal(0, sandbox.Writes);
        Assert.Equal(content, sandbox.Content);
    }

    [Fact]
    public async Task CancellationReachesTheSandbox()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(FileTools.CreateRead(new TextSandbox("text")), new { path = "/file" }, source.Token));
    }

    [Fact]
    public async Task LocalSandboxRoundTripsTextAndRejectsOversizedReads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-harness-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "nested", "file.txt");
            var sandbox = new LocalFileSandbox(maxReadBytes: 8);
            await sandbox.WriteTextAsync(path, "small");
            Assert.Equal("small", await sandbox.ReadTextAsync(path));
            await sandbox.WriteTextAsync(path, "larger than eight bytes");
            await Assert.ThrowsAsync<IOException>(() => sandbox.ReadTextAsync(path).AsTask());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedEncodingPreservesTheExistingFileAndCleansTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-harness-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "file.txt");
        try
        {
            var sandbox = new LocalFileSandbox();
            await sandbox.WriteTextAsync(path, "original content");

            await Assert.ThrowsAsync<EncoderFallbackException>(() => sandbox.WriteTextAsync(path, new string('x', 20000) + "\uD800").AsTask());

            Assert.Equal("original content", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SuccessfulOverwriteReplacesContentsAndCleansTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-harness-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "file.txt");
        try
        {
            var sandbox = new LocalFileSandbox();
            await sandbox.WriteTextAsync(path, "original content");
            await sandbox.WriteTextAsync(path, "replacement");

            Assert.Equal("replacement", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledWritePreservesTheExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-harness-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "file.txt");
        try
        {
            var sandbox = new LocalFileSandbox();
            await sandbox.WriteTextAsync(path, "original content");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.WriteTextAsync(path, "replacement", cancellation.Token).AsTask());

            Assert.Equal("original content", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<string> Invoke(ITool tool, object value, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.SerializeToElement(value);
        return tool.InvokeAsync(input, new ToolContext(null!, new("test", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken).AsTask();
    }

    private sealed class TextSandbox(string content = "") : ISandbox
    {
        public string Content { get; private set; } = content;
        public string? LastPath { get; private set; }
        public int Writes { get; private set; }

        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPath = path;
            return ValueTask.FromResult(Content);
        }

        public ValueTask WriteTextAsync(string path, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPath = path;
            Writes++;
            Content = value;
            return ValueTask.CompletedTask;
        }
    }
}
