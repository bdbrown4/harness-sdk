using System.Diagnostics;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Strands.Agents.Files;
using Strands.Agents.Models;

namespace Strands.Harness.Tests;

public sealed class RichReadTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aE+0AAAAASUVORK5CYII=");

    [Fact]
    public async Task BoundedReadOnlySandboxDoesNotRequireWriteOrDeleteAuthority()
    {
        var files = new BoundedReadOnlyFiles();
        Assert.False((object)files is IFileSystem);
        var input = JsonSerializer.SerializeToElement(new { path = "/remote/pixel.png" });
        var context = new ToolContext(null!, new("bounded", "read", input), new Dictionary<string, object?>());
        var bounded = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(files, new() { MaximumMediaBytes = Png.Length }));
        var result = await bounded.InvokeResultAsync(input, context);
        Assert.Equal(Png, Convert.FromBase64String(Assert.IsType<ImageBlock>(Assert.Single(result.ContentBlocks)).Data));
        var unbounded = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(files));
        await Assert.ThrowsAsync<NotSupportedException>(() => unbounded.InvokeResultAsync(input, context).AsTask());
        await using var agent = await Harness.CreateAsync(Minimal(new EmptyModel()) with
        { Sandbox = new TextOnly(), Read = new() { MaximumMediaBytes = Png.Length }, AgentOptions = new() { Model = new EmptyModel(), FileSystem = files } });
        await AssertMediaAsync(agent, true);
        Assert.Equal(2, files.Reads);
    }

    [Fact]
    public async Task OmittedMainModelEnablesMediaThroughTheLazyBedrockDefault()
    {
        await using var agent = await Harness.CreateAsync(Minimal(new EmptyModel()) with { Model = null });
        Assert.Equal("global.anthropic.claude-opus-5", Assert.IsType<BedrockModel>(agent.Model).ModelId);
        await AssertMediaAsync(agent, true);
    }

    [Fact]
    public async Task ExplicitCoreModelWinsOverTheConvenienceIdentifier()
    {
        var model = new EmptyModel();
        await using var agent = await Harness.CreateAsync(Minimal(new EmptyModel()) with
        { ModelId = "bedrock/openai.gpt-oss-120b", AgentOptions = new() { Model = model } });
        Assert.Same(model, agent.Model);
        await AssertMediaAsync(agent, true);
    }

    [Fact]
    public async Task DisabledDocumentUsesTheExactSourcePlaceholder()
    {
        var files = new SelectedFiles();
        var tool = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(files, new() { Media = false }));
        const string path = "/selected/Report.PDF";
        var input = JsonSerializer.SerializeToElement(new { path });
        var result = await tool.InvokeResultAsync(input, new(null!, new("pdf", "read", input), new Dictionary<string, object?>()));
        Assert.Equal($"[Document file: {path} (pdf, {Png.Length} bytes). This model cannot view document files, so its contents are not shown. Use shell tools if the file has a text-extractable form.]", result.Content);
        Assert.Empty(result.ContentBlocks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidMediaLimitFailsBeforeResolvingAModel(int maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FileTools.CreateRead(new LocalFileSandbox(), new() { MaximumMediaBytes = maximum }));
        var called = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Harness.CreateAsync(new() { Read = new() { MaximumMediaBytes = maximum }, ModelResolver = _ => { called = true; return new EmptyModel(); } }));
        Assert.False(called);
    }

    [Fact]
    public async Task ExplicitMediaLimitRejectsOversizeWithoutChangingGenericStorageReads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-rich-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "large.pdf");
            var bytes = new byte[5 * 1024 * 1024 + 1]; bytes[0] = 0xef; bytes[1] = 0xbb; bytes[2] = 0xbf; bytes[bytes.Length - 1] = 255;
            File.WriteAllBytes(path, bytes);
            var sandbox = new LocalFileSandbox(maxReadBytes: 1);
            var input = JsonSerializer.SerializeToElement(new { path });
            var context = new ToolContext(null!, new("large", "read", input), new Dictionary<string, object?>());
            var bounded = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(sandbox, new() { MaximumMediaBytes = 16 }));
            await Assert.ThrowsAsync<IOException>(() => bounded.InvokeResultAsync(input, context).AsTask());
            var unbounded = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(sandbox));
            var result = await unbounded.InvokeResultAsync(input, context);
            Assert.Equal(bytes, Convert.FromBase64String(Assert.IsType<DocumentBlock>(Assert.Single(result.ContentBlocks)).Data));
            Assert.Equal(bytes, await ((IFileSystem)sandbox).ReadBytesAsync(path));
            var exact = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(sandbox, new() { MaximumMediaBytes = bytes.Length }));
            Assert.Equal(ToolResultStatus.Success, (await exact.InvokeResultAsync(input, context)).Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BoundedReaderMatchesGenericReaderWhenAnAncestorIsAnAlias()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-rich-alias-" + Guid.NewGuid().ToString("N"));
        var actual = Path.Combine(directory, "actual");
        var alias = Path.Combine(directory, "alias");
        Directory.CreateDirectory(Path.Combine(actual, "nested"));
        var linked = false;
        try
        {
            File.WriteAllBytes(Path.Combine(actual, "nested", "file.png"), Png);
            var windows = Path.DirectorySeparatorChar == '\\';
            var executable = windows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/ln";
            var arguments = windows ? $"/d /c mklink /J \"{alias}\" \"{actual}\"" : $"-s \"{actual}\" \"{alias}\"";
            using var process = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(); throw new TimeoutException("The owned alias fixture did not exit."); }
            Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
            linked = true;
            var path = Path.Combine(alias, "nested", "file.png");
            var files = new LocalFileSystem();
            Assert.Equal(Png, await files.ReadBytesAsync(path));
            Assert.Equal(Png, await files.ReadBytesAsync(path, Png.Length));
        }
        finally
        {
            if (linked) Directory.Delete(alias);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BinaryPagingIsIgnoredByTheRegisteredToolBeforeTextPagingValidation()
    {
        var files = new SelectedFiles();
        await using var agent = new Agent(new() { Model = new EmptyModel(), Tools = [FileTools.CreateRead(files)] });
        var result = await agent.InvokeToolAsync(new("binary", "read", JsonSerializer.SerializeToElement(new { path = "/remote/pixel.png", offset = -5, limit = 0 })));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.IsType<ImageBlock>(Assert.Single(result.ContentBlocks));
        Assert.Equal(1, files.Reads);
    }

    [Theory]
    [InlineData("bedrock/openai.gpt-oss-120b", false)]
    [InlineData("bedrock/us.openai.gpt-oss-20b", false)]
    [InlineData("bedrock/global.anthropic.claude-opus-5", true)]
    [InlineData("openai/gpt-5", true)]
    public async Task IdentifierPolicyUsesThePinnedBedrockFamilyRule(string identifier, bool media)
    {
        var options = Minimal(new EmptyModel()) with { Model = null, ModelId = identifier, ModelResolver = _ => new EmptyModel() };
        await using var agent = await Harness.CreateAsync(options);
        await AssertMediaAsync(agent, media);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RoutersRequireEveryCandidateAndExplicitOverrideWins(bool incapable, bool explicitMedia)
    {
        using var bedrock = new BedrockModel(new() { ModelId = incapable ? "eu.openai.gpt-oss-120b" : "global.anthropic.claude-opus-5" });
        var nested = new RoutingModel([new(new EmptyModel()), new(bedrock)]);
        var router = new RoutingModel([new(new EmptyModel()), new(nested)]);
        await using var agent = await Harness.CreateAsync(Minimal(router) with { Read = new() { Media = explicitMedia ? true : null } });
        await AssertMediaAsync(agent, explicitMedia || !incapable);
    }

    [Fact]
    public async Task ExplicitCoreFileSystemWinsAndTextOnlySandboxCannotReadHostMedia()
    {
        var files = new SelectedFiles();
        await using var agent = await Harness.CreateAsync(Minimal(new EmptyModel()) with { Sandbox = new TextOnly(), AgentOptions = new() { Model = new EmptyModel(), FileSystem = files } });
        await AssertMediaAsync(agent, true);
        Assert.Equal(1, files.Reads);
        var input = JsonSerializer.SerializeToElement(new { path = "/remote/pixel.png" });
        var textOnly = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(new TextOnly()));
        Assert.Contains("binary", (await Assert.ThrowsAsync<NotSupportedException>(() => textOnly.InvokeResultAsync(input, new(null!, new("id", "read", input), new Dictionary<string, object?>())).AsTask())).Message);
        var limited = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(files, new() { MaximumMediaBytes = 16 }));
        await Assert.ThrowsAsync<NotSupportedException>(() => limited.InvokeResultAsync(input, new(null!, new("id", "read", input), new Dictionary<string, object?>())).AsTask());
        Assert.Equal(1, files.Reads);
    }

    [Fact]
    public async Task BinaryReaderReceivesExactCancellationAndPreservesItsError()
    {
        using var source = new CancellationTokenSource();
        var error = new IOException("selected backend failure");
        var files = new SelectedFiles { Read = (_, token) => { Assert.Equal(source.Token, token); source.Cancel(); throw new OperationCanceledException(source.Token); } };
        var tool = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(files));
        var input = JsonSerializer.SerializeToElement(new { path = "/remote/pixel.png" });
        var context = new ToolContext(null!, new("id", "read", input), new Dictionary<string, object?>());
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeResultAsync(input, context, source.Token).AsTask());
        Assert.Equal(source.Token, canceled.CancellationToken);
        files.Read = (_, _) => throw error;
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => tool.InvokeResultAsync(input, context).AsTask()));
    }

    internal static HarnessOptions Minimal(IModel model) => new()
    {
        Model = model,
        BuiltinTools = ["read"],
        BuiltinPlugins = [],
        BackgroundTasks = null,
        Skills = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
        Sandbox = new SelectedFiles(),
    };
    private static async Task AssertMediaAsync(Agent agent, bool media)
    {
        var input = JsonSerializer.SerializeToElement(new { path = "/remote/pixel.png" });
        var result = await agent.InvokeToolAsync(new("media", "read", input));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        if (media) Assert.IsType<ImageBlock>(Assert.Single(result.ContentBlocks));
        else { Assert.Empty(result.ContentBlocks); Assert.Contains("cannot view image files", result.Content); }
    }

    private class TextOnly : ISandbox
    {
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => new("remote text");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class BoundedReadOnlyFiles : TextOnly, IReadOnlyFileSystem, IBoundedFileReader
    {
        internal int Reads;
        public ValueTask<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken = default)
        { Reads++; Assert.Equal(Png.Length, maximumBytes); cancellationToken.ThrowIfCancellationRequested(); return new((byte[])Png.Clone()); }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => new(Array.Empty<FileEntry>());
    }
    private sealed class SelectedFiles : TextOnly, IFileSystem
    {
        internal int Reads;
        internal Func<string, CancellationToken, byte[]> Read = (_, _) => (byte[])Png.Clone();
        public ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) { Reads++; return new(Read(path, cancellationToken)); }
        public ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => new(Array.Empty<FileEntry>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableMediaOverrideIsConsumedByTheRealReadTool(bool media)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-rich-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "pixel.JPG");
            File.WriteAllBytes(path, Png);
            var config = HarnessAgentConfig.Parse("{\"builtinTools\":{\"*\":false,\"read\":{\"media\":" + (media ? "true" : "false") + "}},\"builtinPlugins\":[],\"skills\":false,\"memory\":false,\"contextManager\":false,\"session\":false,\"agentConfig\":{\"backgroundTasks\":false}}");
            await using var agent = await Harness.CreateAsync(config.ToOptions(new() { ModelResolver = _ => new EmptyModel() }));
            var tool = Assert.IsAssignableFrom<IRichTool>(Assert.Single(agent.Tools));
            var input = JsonSerializer.SerializeToElement(new { path });
            var result = await tool.InvokeResultAsync(input, new(agent, new("media", "read", input), new Dictionary<string, object?>()));
            if (media) Assert.IsType<ImageBlock>(Assert.Single(result.ContentBlocks));
            else
            {
                Assert.Equal($"[Image file: {path} (jpeg, {Png.Length} bytes). This model cannot view image files, so its contents are not shown. Use shell tools if the file has a text-extractable form.]", result.Content);
                Assert.Empty(result.ContentBlocks);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class EmptyModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    [Fact]
    public async Task RealPngIsReturnedAsCorrelatedImageBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-rich-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "pixel.png");
            File.WriteAllBytes(path, Png);
            var tool = FileTools.CreateRead(new LocalFileSandbox());
            var input = JsonSerializer.SerializeToElement(new { path });
            var context = new ToolContext(null!, new ToolUseBlock("read-id", "read", input), new Dictionary<string, object?>());

            var result = await Assert.IsAssignableFrom<IRichTool>(tool).InvokeResultAsync(input, context);

            Assert.Equal("read-id", result.ToolUseId);
            Assert.Equal(ToolResultStatus.Success, result.Status);
            var image = Assert.IsType<ImageBlock>(Assert.Single(result.ContentBlocks));
            Assert.Equal("image/png", image.MediaType);
            Assert.Equal(Png, Convert.FromBase64String(image.Data));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("file.PNG", "image/png", false)]
    [InlineData("file.JpG", "image/jpeg", false)]
    [InlineData("file.JPEG", "image/jpeg", false)]
    [InlineData("file.Gif", "image/gif", false)]
    [InlineData("file.WEBP", "image/webp", false)]
    [InlineData("file.PDF", "application/pdf", true)]
    [InlineData("file.DOC", "application/msword", true)]
    [InlineData("file.DOCX", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", true)]
    [InlineData("file.XLS", "application/vnd.ms-excel", true)]
    [InlineData("file.XLSX", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true)]
    public async Task ExtensionDispatchRetainsOpaqueBytes(string name, string mediaType, bool document)
    {
        var bytes = new byte[] { 0, 0xef, 0xbb, 0xbf, 0xff, 42 };
        var result = await ReadFileAsync(name, bytes);
        var block = Assert.Single(result.ContentBlocks);
        if (document)
        {
            var value = Assert.IsType<DocumentBlock>(block);
            Assert.Equal(mediaType, value.MediaType);
            Assert.Equal(bytes, Convert.FromBase64String(value.Data));
        }
        else
        {
            var value = Assert.IsType<ImageBlock>(block);
            Assert.Equal(mediaType, value.MediaType);
            Assert.Equal(bytes, Convert.FromBase64String(value.Data));
        }
    }

    [Fact]
    public async Task PdfUsesTheSourceSanitizedDocumentNameAndExactBytes()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF\n");
        var result = await ReadFileAsync("Report_[copy]   (v1).PDF", bytes);
        var document = Assert.IsType<DocumentBlock>(Assert.Single(result.ContentBlocks));
        Assert.Equal("Report [copy] (v1) PDF", document.Name);
        Assert.Equal(bytes, Convert.FromBase64String(document.Data));
    }

    [Theory]
    [InlineData("file.csv")]
    [InlineData("file.html")]
    [InlineData("file.md")]
    [InlineData("file.mp4")]
    public async Task TextRepresentableDocumentsAndVideoStayOnTheTextPath(string name)
    {
        var result = await ReadFileAsync(name, System.Text.Encoding.UTF8.GetBytes("one\ntwo"));
        Assert.Equal("     1\tone\n     2\ttwo", result.Content);
        Assert.Empty(result.ContentBlocks);
    }

    private static async Task<ToolResultBlock> ReadFileAsync(string name, byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-rich-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, bytes);
            var tool = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(new LocalFileSandbox()));
            var input = JsonSerializer.SerializeToElement(new { path });
            return await tool.InvokeResultAsync(input, new(null!, new("read-id", "read", input), new Dictionary<string, object?>()));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
