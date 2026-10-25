using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Strands.Agents.Tests.Sessions;

public sealed class FileSessionPathTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(217)]
    [InlineData(300)]
    public async Task LongRootsRoundTripAndReplaceCompleteSnapshots(int directoryLength)
    {
        using var directory = new TestDirectory(directoryLength);
        var store = new FileSessionStore(directory.Path);
        const string session = "../../opaque\\session";
        output.WriteLine($"Directory length={directory.Path.Length}; final filename path length={directory.Path.Length + 70}; temporary path length={directory.Path.Length + 37}.");
        await store.SaveAsync(session, Snapshot("first"));
        Assert.Equal("first", (await store.LoadAsync(session))!.Messages[0].Text);
        await store.SaveAsync(session, Snapshot("replacement"));
        Assert.Equal("replacement", (await new FileSessionStore(directory.Path).LoadAsync(session))!.Messages[0].Text);
        Assert.Single(Directory.GetFiles(directory.FileSystemPath, "*.json"));
        Assert.Empty(Directory.GetFiles(directory.FileSystemPath, "*.tmp"));
    }

    [Fact]
    public void DirectMoveCharacterizationPreservesBytesUnderEitherHostPathPolicy()
    {
        using var directory = new TestDirectory(217);
        Directory.CreateDirectory(directory.FileSystemPath);
        var temporary = System.IO.Path.Combine(directory.Path, new string('a', 32) + ".tmp");
        var destination = System.IO.Path.Combine(directory.Path, new string('b', 64) + ".json");
        File.WriteAllText(TestDirectory.ForFileSystem(temporary), "complete bytes");
        var failure = Record.Exception(() => File.Move(temporary, destination));
        output.WriteLine($"Direct File.Move: source length={temporary.Length}; destination length={destination.Length}; result={failure?.GetType().FullName ?? "success"}; HResult={failure?.HResult}.");
        if (failure is not null)
        {
            Assert.Equal('\\', System.IO.Path.DirectorySeparatorChar);
            Assert.IsAssignableFrom<IOException>(failure);
            Assert.True(File.Exists(TestDirectory.ForFileSystem(temporary)));
            File.Move(TestDirectory.ForFileSystem(temporary), TestDirectory.ForFileSystem(destination));
        }
        Assert.Equal("complete bytes", File.ReadAllText(TestDirectory.ForFileSystem(destination)));
        Assert.False(File.Exists(TestDirectory.ForFileSystem(temporary)));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(217)]
    [InlineData(300)]
    public async Task UnicodeRootsContentAndTrackingIdentitySurviveReplacement(int length)
    {
        using var directory = new TestDirectory(length, unicode: true);
        var message = new Message(Role.User, [new TextBlock("保存 😃"), new ImageBlock("image/png", "AAEC/w=="), new JsonBlock(JsonSerializer.SerializeToElement(new { value = "é 東京" }))]);
        var snapshot = new SessionSnapshot(1, [message]) { AgentId = "agent-α", HasSystemPrompt = true, SystemPrompt = "指示" };
        var store = new FileSessionStore(directory.Path);
        await store.SaveAsync("会話/../opaque", snapshot);
        var restored = (await store.LoadAsync("会話/../opaque"))!;
        Assert.Equal(message.TrackingId, restored.Messages[0].TrackingId);
        Assert.Equal("保存 😃", Assert.IsType<TextBlock>(restored.Messages[0].Content[0]).Text);
        Assert.Equal("AAEC/w==", Assert.IsType<ImageBlock>(restored.Messages[0].Content[1]).Data);
        Assert.Equal("é 東京", Assert.IsType<JsonBlock>(restored.Messages[0].Content[2]).Json.GetProperty("value").GetString());
        Assert.Equal("agent-α", restored.AgentId);
        Assert.Equal("指示", restored.SystemPrompt);
    }

    [Fact]
    public async Task RelativeRootsAndParentSegmentsResolveBeforeFileSystemUse()
    {
        var current = System.IO.Path.GetFullPath(Environment.CurrentDirectory);
        var temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        var comparison = System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var parent = string.Equals(System.IO.Path.GetPathRoot(current), System.IO.Path.GetPathRoot(temporary), comparison) ? temporary : current;
        using var directory = new TestDirectory(Math.Max(300, current.Length + 120), unicode: true, parent: parent);
        var absolute = System.IO.Path.Combine(directory.Path, "not-created", "..", "selected");
        var relativeDirectory = new Uri(current.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(directory.Path + System.IO.Path.DirectorySeparatorChar));
        Assert.False(relativeDirectory.IsAbsoluteUri);
        var relative = System.IO.Path.Combine(".", Uri.UnescapeDataString(relativeDirectory.ToString()).Replace('/', System.IO.Path.DirectorySeparatorChar), "not-created", "..", "selected");
        Assert.False(System.IO.Path.IsPathRooted(relative));
        Assert.Equal(System.IO.Path.GetFullPath(absolute), System.IO.Path.GetFullPath(relative));
        await new FileSessionStore(relative).SaveAsync("relative", Snapshot("resolved once"));
        var normalized = System.IO.Path.GetFullPath(absolute);
        Assert.Equal("resolved once", (await new FileSessionStore(normalized).LoadAsync("relative"))!.Messages[0].Text);
        Assert.False(Directory.Exists(TestDirectory.ForFileSystem(System.IO.Path.Combine(directory.Path, "not-created"))));
        Assert.Single(Directory.GetFiles(TestDirectory.ForFileSystem(normalized), "*.json"));
    }

    [Fact]
    public async Task AnAlreadyExtendedRootIsNotPrefixedTwice()
    {
        using var directory = new TestDirectory(300);
        await new FileSessionStore(directory.FileSystemPath).SaveAsync("extended", Snapshot("same physical directory"));
        Assert.Equal("same physical directory", (await new FileSessionStore(directory.Path).LoadAsync("extended"))!.Messages[0].Text);
        Assert.Single(Directory.GetFiles(directory.FileSystemPath, "*.json"));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("../../escape")]
    [InlineData(@"..\..\escape")]
    [InlineData(@"C:\outside\session")]
    [InlineData("a/b/../c")]
    [InlineData("会話😃")]
    public async Task OpaqueSessionIdsRemainHashedAndCannotSelectFilesystemPaths(string session)
    {
        using var directory = new TestDirectory(300);
        var store = new FileSessionStore(directory.Path);
        await store.SaveAsync(session, Snapshot(session));
        var file = Assert.Single(Directory.GetFiles(directory.FileSystemPath));
        Assert.Equal(Key(session), System.IO.Path.GetFileName(file));
        Assert.Equal(session, (await store.LoadAsync(session))!.Messages[0].Text);
        Assert.Empty(Directory.GetDirectories(directory.FileSystemPath));
    }

    [Fact]
    public async Task ConcurrentSavesSharingAnApplicationLeasePublishCompleteSnapshots()
    {
        using var directory = new TestDirectory(300);
        using var lease = new SemaphoreSlim(1, 1);
        var values = Enumerable.Range(0, 24).Select(index => index + ":" + new string((char)('a' + index), 16000)).ToArray();
        await Task.WhenAll(values.Select(async value =>
        {
            await lease.WaitAsync();
            try { await new FileSessionStore(directory.Path).SaveAsync("shared", Snapshot(value)); }
            finally { lease.Release(); }
        }));
        var result = (await new FileSessionStore(directory.Path).LoadAsync("shared"))!;
        Assert.Contains(result.Messages[0].Text, values);
        Assert.Single(Directory.GetFiles(directory.FileSystemPath, "*.json"));
        Assert.Empty(Directory.GetFiles(directory.FileSystemPath, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentIndependentSessionsDoNotLoseOrCrossTheirPayloads()
    {
        using var directory = new TestDirectory(300);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => new FileSessionStore(directory.Path).SaveAsync("id-" + index, Snapshot("payload-" + index)).AsTask()));
        var reader = new FileSessionStore(directory.Path);
        for (var index = 0; index < 16; index++)
            Assert.Equal("payload-" + index, (await reader.LoadAsync("id-" + index))!.Messages[0].Text);
        Assert.Equal(16, Directory.GetFiles(directory.FileSystemPath, "*.json").Length);
        Assert.Empty(Directory.GetFiles(directory.FileSystemPath, "*.tmp"));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(300)]
    public async Task CancelledReplacementKeepsThePreviousSnapshotAndRemovesTemporaryFiles(int length)
    {
        using var directory = new TestDirectory(length);
        var store = new FileSessionStore(directory.Path);
        await store.SaveAsync("saved", Snapshot("accepted"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync("saved", Snapshot(new string('x', 32000)), cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal("accepted", (await store.LoadAsync("saved"))!.Messages[0].Text);
        Assert.Empty(Directory.GetFiles(directory.FileSystemPath, "*.tmp"));
    }

    [Fact]
    public async Task MissingSessionDoesNotCreateTheLongRoot()
    {
        using var directory = new TestDirectory(300);
        Assert.Null(await new FileSessionStore(directory.Path).LoadAsync("missing"));
        Assert.False(Directory.Exists(directory.FileSystemPath));
    }

    [Fact]
    public async Task ValidationFailureLeavesBothMissingAndExistingRecordsIntact()
    {
        using var directory = new TestDirectory(300);
        var store = new FileSessionStore(directory.Path);
        var invalid = new SessionSnapshot(2, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("saved", invalid).AsTask());
        Assert.False(Directory.Exists(directory.FileSystemPath));
        await store.SaveAsync("saved", Snapshot("accepted"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("saved", invalid).AsTask());
        Assert.Equal("accepted", (await store.LoadAsync("saved"))!.Messages[0].Text);
        Assert.Empty(Directory.GetFiles(directory.FileSystemPath, "*.tmp"));
    }

    [Fact]
    public async Task ExistingNativeJsonKeepsItsFilenameAndMalformedJsonStillFails()
    {
        using var directory = new TestDirectory(300);
        Directory.CreateDirectory(directory.FileSystemPath);
        var file = System.IO.Path.Combine(directory.FileSystemPath, Key("legacy"));
        File.WriteAllText(file, "{\"schemaVersion\":1,\"messages\":[]}");
        var store = new FileSessionStore(directory.Path);
        Assert.Empty((await store.LoadAsync("legacy"))!.Messages);
        await store.SaveAsync("legacy", Snapshot("replaced"));
        Assert.Equal(Key("legacy"), System.IO.Path.GetFileName(Assert.Single(Directory.GetFiles(directory.FileSystemPath))));
        File.WriteAllText(file, "{broken-json");
        await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync("legacy").AsTask());
    }

    [Fact]
    public async Task ABlockedDestinationPreservesOtherRecordsAndCleansTheFailedTemporaryFile()
    {
        using var directory = new TestDirectory(300);
        var store = new FileSessionStore(directory.Path);
        await store.SaveAsync("accepted", Snapshot("durable"));
        var blocker = System.IO.Path.Combine(directory.FileSystemPath, Key("blocked"));
        Directory.CreateDirectory(blocker);
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync("blocked", Snapshot("cannot publish")).AsTask());
        Assert.Equal("durable", (await store.LoadAsync("accepted"))!.Messages[0].Text);
        Assert.Empty(Directory.GetFiles(directory.FileSystemPath, "*.tmp"));
        Directory.Delete(blocker);
        await store.SaveAsync("blocked", Snapshot("retry succeeds"));
        Assert.Equal("retry succeeds", (await store.LoadAsync("blocked"))!.Messages[0].Text);
    }

    [Fact]
    public async Task LongPathsDoNotChangeProcessWidePathSwitches()
    {
        var legacyKnown = AppContext.TryGetSwitch("Switch.System.IO.UseLegacyPathHandling", out var legacy);
        var blockingKnown = AppContext.TryGetSwitch("Switch.System.IO.BlockLongPaths", out var blocking);
        using var directory = new TestDirectory(300);
        await new FileSessionStore(directory.Path).SaveAsync("switches", Snapshot("scoped path handling"));
        Assert.Equal(legacyKnown, AppContext.TryGetSwitch("Switch.System.IO.UseLegacyPathHandling", out var currentLegacy));
        Assert.Equal(legacy, currentLegacy);
        Assert.Equal(blockingKnown, AppContext.TryGetSwitch("Switch.System.IO.BlockLongPaths", out var currentBlocking));
        Assert.Equal(blocking, currentBlocking);
    }

    [Fact]
    public async Task FixtureWithOneRemainingPathCharacterMakesProgressWithinItsOwnedRoot()
    {
        using var parent = new TestDirectory(120);
        var requested = parent.Path.Length + 1 + "strands-file-session-path-".Length + 32 + 1;
        using var directory = new TestDirectory(requested, parent: parent.Path);
        Assert.Equal(requested + 1, directory.Path.Length);
        await new FileSessionStore(directory.Path).SaveAsync("boundary", Snapshot("finite fixture"));
        Assert.Equal("finite fixture", (await new FileSessionStore(directory.Path).LoadAsync("boundary"))!.Messages[0].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidDirectoriesStillFailBeforeFilesystemAccess(string path) => Assert.Throws<ArgumentException>(() => new FileSessionStore(path));

    private static string Key(string session)
    {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(session))).Replace("-", "").ToLowerInvariant() + ".json";
    }

    private static SessionSnapshot Snapshot(string text) => new(1, [Message.User(text)]);

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _ownedRoot;
        private readonly string _expectedParent;
        public TestDirectory(int length, bool unicode = false, string? parent = null)
        {
            _expectedParent = System.IO.Path.GetFullPath(parent ?? System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            _ownedRoot = System.IO.Path.Combine(_expectedParent, "strands-file-session-path-" + Guid.NewGuid().ToString("N"));
            Path = unicode ? System.IO.Path.Combine(_ownedRoot, "会話-é-😃") : _ownedRoot;
            while (Path.Length < length)
            {
                var remaining = length - Path.Length;
                Path = System.IO.Path.Combine(Path, new string('x', Math.Max(1, remaining == 50 ? 47 : Math.Min(48, remaining - 1))));
            }
        }
        public string Path { get; }
        public string FileSystemPath => ForFileSystem(Path);
        public static string ForFileSystem(string path) => System.IO.Path.DirectorySeparatorChar != '\\' || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        public void Dispose()
        {
            if (!_ownedRoot.StartsWith(_expectedParent, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(_ownedRoot).StartsWith("strands-file-session-path-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the task-owned temporary root.");
            var root = ForFileSystem(_ownedRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
