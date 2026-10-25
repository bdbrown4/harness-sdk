using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Strands.Agents.Bidi;
using TextBlock = Strands.Agents.TextBlock;
using ToolResultBlock = Strands.Agents.ToolResultBlock;
using ToolUseBlock = Strands.Agents.ToolUseBlock;

namespace Strands.Agents.Bedrock;

/// <summary>A persistent Nova Sonic model using a borrowed official client or explicit borrowed credentials with an owned transport.</summary>
public sealed class BedrockNovaSonicModel : IRestartableBidiModel, IAudioBidiModel, IModelMetadataProvider, IConfigurableModel<BedrockNovaSonicModelOptions>
{
    private BedrockNovaSonicModelOptions _options;
    private readonly IAmazonBedrockRuntime? _client;
    private readonly (AWSCredentials Credentials, RegionEndpoint Region, Func<ImmutableCredentials, RegionEndpoint, IAmazonBedrockRuntime> ClientFactory)? _explicit;
    private readonly Func<ImmutableCredentials, RegionEndpoint, string, INovaSonicDuplex>? _transportFactory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _disposeGate = new();
    private Task? _disposal;
    private int _disposed;
    private ConnectionState? _connection;
    /// <summary>Creates a disconnected model. The AWS client's credentials, region and lifetime remain caller-owned.</summary>
    /// <param name="client">The borrowed official AWS Bedrock Runtime client.</param>
    /// <param name="options">The model identifier and audio settings.</param>
    public BedrockNovaSonicModel(IAmazonBedrockRuntime client, BedrockNovaSonicModelOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = Snapshot(options);
    }
    /// <summary>Creates a disconnected model using explicit borrowed credentials and region. Each connection captures fresh credentials and owns its transport.</summary>
    /// <param name="credentials">Borrowed credentials, refreshed for each connection.</param>
    /// <param name="region">The Bedrock Runtime region.</param>
    /// <param name="options">Settings captured by the next connection.</param>
    public BedrockNovaSonicModel(AWSCredentials credentials, RegionEndpoint region, BedrockNovaSonicModelOptions options)
        : this(credentials, region, options, CreateOfficialClient) { }
    internal BedrockNovaSonicModel(AWSCredentials credentials, RegionEndpoint region, BedrockNovaSonicModelOptions options,
        Func<ImmutableCredentials, RegionEndpoint, IAmazonBedrockRuntime> clientFactory)
    {
        _explicit = (credentials ?? throw new ArgumentNullException(nameof(credentials)), region ?? throw new ArgumentNullException(nameof(region)), clientFactory ?? throw new ArgumentNullException(nameof(clientFactory)));
        _options = Snapshot(options);
    }
    // A per-connection wire seam lets local TLS tests exercise the shared engine without public endpoint or trust overrides.
    internal BedrockNovaSonicModel(AWSCredentials credentials, RegionEndpoint region, BedrockNovaSonicModelOptions options,
        Func<ImmutableCredentials, RegionEndpoint, string, INovaSonicDuplex> transportFactory)
        : this(credentials, region, options)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    }
    private static IAmazonBedrockRuntime CreateOfficialClient(ImmutableCredentials snapshot, RegionEndpoint region)
    {
        AWSCredentials captured = snapshot.UseToken ? new SessionAWSCredentials(snapshot.AccessKey, snapshot.SecretKey, snapshot.Token, snapshot.AccountId)
            : new BasicAWSCredentials(snapshot.AccessKey, snapshot.SecretKey, snapshot.AccountId);
        return new AmazonBedrockRuntimeClient(captured, region);
    }
    /// <inheritdoc />
    public ModelMetadata Metadata { get { var options = ActiveOptions; return new(options.ModelId, MaxOutputTokens: options.MaxTokens); } }
    /// <inheritdoc />
    public BidiConnectionOptions Connection => ActiveOptions.Connection with { };
    /// <inheritdoc />
    public BidiAudioConfig Audio
    {
        get { var options = ActiveOptions; return new(new(options.InputSampleRate, 1, "pcm"), new(options.OutputSampleRate, 1, "pcm")); }
    }
    private BedrockNovaSonicModelOptions ActiveOptions
    {
        get
        {
            return Volatile.Read(ref _connection)?.Options ?? Volatile.Read(ref _options);
        }
    }
    /// <inheritdoc />
    public BedrockNovaSonicModelOptions GetConfig() => Snapshot(Volatile.Read(ref _options));
    /// <summary>Changes settings for the next connection. Active wire formats and metadata remain captured until reconnect.</summary>
    public void UpdateConfig(Func<BedrockNovaSonicModelOptions, BedrockNovaSonicModelOptions> update)
    {
        ThrowIfDisposed();
        if (update is null) throw new ArgumentNullException(nameof(update));
        var next = Snapshot(update(GetConfig())); // Caller code runs outside the lifecycle lock.
        lock (_disposeGate) { ThrowIfDisposed(); Volatile.Write(ref _options, next); }
    }
    private static BedrockNovaSonicModelOptions Snapshot(BedrockNovaSonicModelOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ModelId)) throw new ArgumentException("A Nova Sonic model ID is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.VoiceId)) throw new ArgumentException("A Nova Sonic voice is required.", nameof(options));
        if (options.InputSampleRate is not (8000 or 16000 or 24000) || options.OutputSampleRate is not (8000 or 16000 or 24000)) throw new ArgumentOutOfRangeException(nameof(options), "Nova Sonic audio rates must be 8000, 16000 or 24000 Hz.");
        if (options.MaximumBufferedInputEvents is < 1 or > 1024 || options.MaximumBufferedOutputEvents is < 1 or > 1024 || options.MaximumInputBytes is < 1 or > 16777216)
            throw new ArgumentOutOfRangeException(nameof(options), "Nova Sonic queues must contain 1 to 1024 events and input batches must be bounded to 1 byte through 16 MiB.");
        if (options.MaxTokens <= 0 || double.IsNaN(options.Temperature) || options.Temperature < 0 || options.Temperature > 1 || double.IsNaN(options.TopP) || options.TopP < 0 || options.TopP > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Nova Sonic requires positive MaxTokens and Temperature/TopP between zero and one.");
        if (options.Connection is null) throw new ArgumentException("A connection policy is required.", nameof(options));
        var interval = options.Connection.RestartAfter ?? TimeSpan.FromMinutes(7);
        if (interval <= TimeSpan.Zero || interval >= TimeSpan.FromMinutes(8)) throw new ArgumentOutOfRangeException(nameof(options), "Reconnect timing must be positive and precede Nova Sonic's eight-minute connection limit.");
        return options with { Connection = options.Connection with { RestartAfter = interval } };
    }
    /// <inheritdoc />
    public async Task RestartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        var previous = Volatile.Read(ref _connection) ?? throw new InvalidOperationException("Start Nova Sonic before reconnecting.");
        // Credential refresh, caller factories and history serialization occur before lifecycle admission.
        var next = await PrepareConnectionAsync(session ?? previous.InitialSession, cancellationToken).ConfigureAwait(false);
        try { await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { await ReleasePreparedAsync(next, error).ConfigureAwait(false); throw; }
        try
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(Volatile.Read(ref _connection), previous)) throw new InvalidOperationException("The Nova Sonic connection changed while reconnect was being prepared.");
            if (!previous.SendGate.Wait(0)) throw new InvalidOperationException("A Nova Sonic send is in progress; reconnect cannot discard it.");
            try
            {
                lock (previous.StateGate)
                {
                    if (previous.PendingTools.Count != 0 || previous.Content.Count != 0 || previous.ResponseId is not null)
                        throw new InvalidOperationException("Finish the current response and its pending tools before reconnecting Nova Sonic.");
                    if (session is null && previous.HasTraffic) throw new InvalidOperationException("Supply the retained conversation when reconnecting after traffic.");
                    previous.Closing = true;
                }
                Volatile.Write(ref _connection, null);
            }
            finally { previous.SendGate.Release(); }
            try { await CloseAsync(previous).ConfigureAwait(false); }
            catch (BidiConnectionTimeoutException error) when (error.Data["Strands.CleanupErrors"] is null) { }
            await ConnectAsync(next, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { if (!ReferenceEquals(Volatile.Read(ref _connection), next)) await ReleasePreparedAsync(next, error).ConfigureAwait(false); throw; }
        finally { _lifecycle.Release(); }
    }

    /// <inheritdoc />
    public async Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        var c = await PrepareConnectionAsync(session ?? new(), cancellationToken).ConfigureAwait(false);
        try { await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { await ReleasePreparedAsync(c, error).ConfigureAwait(false); throw; }
        try
        {
            ThrowIfDisposed();
            if (_connection is not null) throw new InvalidOperationException("Stop the current Nova Sonic connection before starting another.");
            await ConnectAsync(c, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { if (!ReferenceEquals(Volatile.Read(ref _connection), c)) await ReleasePreparedAsync(c, error).ConfigureAwait(false); throw; }
        finally { _lifecycle.Release(); }
    }

    /// <inheritdoc />
    public async Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); if (input is null) throw new ArgumentNullException(nameof(input)); cancellationToken.ThrowIfCancellationRequested();
        var c = Volatile.Read(ref _connection) ?? throw new InvalidOperationException("Start Nova Sonic before sending.");
        if (Volatile.Read(ref c.InputFailure) is { } invalid) throw invalid;
        using var send = CancellationTokenSource.CreateLinkedTokenSource(c.Token, cancellationToken);
        try { await c.SendGate.WaitAsync(send.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Nova Sonic send was canceled.", error, cancellationToken); }
        var published = 0;
        try
        {
            if (Volatile.Read(ref c.InputFailure) is { } failure) throw failure;
            c.Token.ThrowIfCancellationRequested();
            string? openedAudio = null;
            var results = input is BidiMessage message ? message.Content.OfType<ToolResultBlock>().ToArray() : [];
            lock (c.StateGate)
            {
                if (c.Closing) throw new InvalidOperationException("Nova Sonic is reconnecting.");
                if (results.Select(result => result.ToolUseId).Distinct(StringComparer.Ordinal).Count() != results.Length || results.Any(result => !c.PendingTools.ContainsKey(result.ToolUseId)))
                    throw new ArgumentException("Nova Sonic tool results must each match one unresolved call on the active connection.", nameof(input));
            }
            var batch = Prepare(c, () =>
            {
                if (input is BidiMessage message) WriteMessage(c, message);
                else if (input is BidiAudioDelta audio)
                {
                    if (audio.Length > c.MaximumInputBytes) throw new ArgumentException("Nova Sonic input exceeds its configured byte limit.", nameof(input));
                    var name = c.AudioContent ?? (openedAudio = Id());
                    if (openedAudio is not null)
                        Put(c, "contentStart", new { promptName = c.Prompt, contentName = name, type = "AUDIO", interactive = true, role = "USER", audioInputConfiguration = new { mediaType = "audio/lpcm", sampleRateHertz = c.Options.InputSampleRate, sampleSizeBits = 16, channelCount = 1, audioType = "SPEECH", encoding = "base64" } });
                    Put(c, "audioInput", new { promptName = c.Prompt, contentName = name, content = Convert.ToBase64String(audio.GetBytes()) });
                }
                else throw new ArgumentException("Nova Sonic supports text, tool results and PCM audio input.", nameof(input));
            });
            // Prevalidation precedes the first publication; pressured sends remain cancellable.
            for (var index = 0; index < batch.Count; index++)
            {
                await c.Input.Writer.WriteAsync(batch[index], send.Token).ConfigureAwait(false);
                published++;
                if (index == 0 && openedAudio is not null) c.AudioContent = openedAudio;
            }
            lock (c.StateGate) { c.HasTraffic |= published != 0; foreach (var result in results) c.PendingTools.Remove(result.ToolUseId); }
        }
        catch (OperationCanceledException error)
        {
            var canceled = cancellationToken.IsCancellationRequested ? new OperationCanceledException("Nova Sonic send was canceled.", error, cancellationToken) : error;
            if (input is BidiMessage && published != 0) await InvalidateAsync(c, canceled).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(canceled).Throw(); throw;
        }
        finally { c.SendGate.Release(); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        var c = Volatile.Read(ref _connection) ?? throw new InvalidOperationException("Start Nova Sonic before receiving.");
        if (Interlocked.CompareExchange(ref c.Receiver, 1, 0) != 0) throw new InvalidOperationException("Only one reader can consume Nova Sonic output.");
        try { await foreach (var item in c.Output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item; }
        finally { Interlocked.Exchange(ref c.Receiver, 0); }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? failure = null;
        var current = Volatile.Read(ref _connection);
        if (current is not null)
        {
            try { current.Lifetime.Cancel(); }
            catch (ObjectDisposedException) when (current.Token.IsCancellationRequested) { }
            catch (Exception error) { failure = error; }
        }
        // Once admitted, cleanup is joined even if the caller cancels its wait.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var c = Interlocked.Exchange(ref _connection, null);
            if (c is not null) try { await CloseAsync(c).ConfigureAwait(false); } catch (Exception error) { if (failure is null) failure = error; else AddCleanup(failure, error); }
        }
        finally { _lifecycle.Release(); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool>? owner = null;
        lock (_disposeGate)
        {
            if (_disposal is null) { Volatile.Write(ref _disposed, 1); owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; }
        }
        if (owner is not null) _ = DisposeCoreAsync(owner);
        return new(_disposal);
    }
    private async Task DisposeCoreAsync(TaskCompletionSource<bool> completion)
    {
        try { await StopAsync().ConfigureAwait(false); completion.TrySetResult(true); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(BedrockNovaSonicModel)); }

    private async Task<ConnectionState> PrepareConnectionAsync(BidiSessionOptions session, CancellationToken caller)
    {
        var c = new ConnectionState(caller, Snapshot(Volatile.Read(ref _options)), session);
        try
        {
            c.Initial = new(Prepare(c, () => Initialize(c, c.InitialSession)));
            if (_explicit is { } identity)
            {
                caller.ThrowIfCancellationRequested();
                var refreshing = identity.Credentials.GetCredentialsAsync();
                ImmutableCredentials snapshot;
                try
                {
                    await TaskCancellation.WaitAsync(refreshing, caller).ConfigureAwait(false);
                    snapshot = (await refreshing.ConfigureAwait(false)).Copy();
                    caller.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException error) when (caller.IsCancellationRequested)
                {
                    _ = ObserveCredentialRefreshAsync(refreshing);
                    if (error.CancellationToken != caller) throw new OperationCanceledException("Nova Sonic credential preparation was canceled.", error, caller);
                    throw;
                }
                if (_transportFactory is { } factory)
                    c.Transport = factory(snapshot, identity.Region, c.Options.ModelId) ?? throw new InvalidOperationException("The Nova Sonic transport factory returned null.");
                else
                {
#if NET8_0_OR_GREATER
                    var client = identity.ClientFactory(snapshot, identity.Region) ?? throw new InvalidOperationException("The AWS client factory returned null.");
                    c.Transport = new AwsNovaSonicDuplex(client, c.Options.ModelId, ownsClient: true);
#else
                    c.Transport = new NativeNovaSonicDuplex(snapshot, identity.Region, c.Options.ModelId);
#endif
                }
            }
            else
            {
#if NET8_0_OR_GREATER
                c.Transport = new AwsNovaSonicDuplex(_client!, c.Options.ModelId, ownsClient: false);
#else
                throw new PlatformNotSupportedException("The official AWS client cannot open Nova Sonic duplex streams on this target; explicit credentials and a native transport are required.");
#endif
            }
            return c;
        }
        catch (Exception error) { await ReleasePreparedAsync(c, error).ConfigureAwait(false); throw; }
    }
    private static async Task ObserveCredentialRefreshAsync(Task refreshing)
    {
        try { await refreshing.ConfigureAwait(false); }
        catch { /* The AWS refresh is borrowed and may be shared; observe its eventual fault after the caller cancels its wait. */ }
    }
    private static async Task ReleasePreparedAsync(ConnectionState c, Exception primary)
    {
        try { if (c.Transport is not null) await c.Transport.StopAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { AddCleanup(primary, cleanup); }
        finally { c.Lifetime.Dispose(); }
    }
    private async Task ConnectAsync(ConnectionState c, CancellationToken caller)
    {
        Volatile.Write(ref _connection, c);
        try
        {
            await c.Transport!.OpenAsync(token => PublishAsync(c, token), c.Token).ConfigureAwait(false);
            c.Output.Writer.TryWrite(new BidiConnectionStartEvent(c.Prompt, c.Options.ModelId));
            c.Reader = ReadOutputAsync(c);
        }
        catch (Exception error)
        {
            try { await CloseAsync(c).ConfigureAwait(false); } catch (Exception cleanup) { AddCleanup(error, cleanup); }
            Volatile.Write(ref _connection, null);
            if (error is OperationCanceledException canceled && caller.IsCancellationRequested && canceled.CancellationToken != caller)
            {
                var replacement = new OperationCanceledException("Nova Sonic connection was canceled.", canceled, caller);
                if (error.Data["Strands.CleanupErrors"] is List<Exception> cleanup) replacement.Data["Strands.CleanupErrors"] = cleanup;
                throw replacement;
            }
            throw;
        }
    }
    private static async Task InvalidateAsync(ConnectionState c, Exception canceled)
    {
        var failure = new ModelException("A finite Nova Sonic content group was canceled after publication; reconnect before sending again.", canceled);
        Interlocked.CompareExchange(ref c.InputFailure, failure, null);
        c.Input.Writer.TryComplete(failure); c.Output.Writer.TryComplete(failure);
        try { c.Lifetime.Cancel(); } catch (Exception error) { AddCleanup(canceled, error); }
        try { if (c.Transport is not null) await c.Transport.StopAsync().ConfigureAwait(false); } catch (Exception error) { AddCleanup(canceled, error); }
        try { await c.Reader.ConfigureAwait(false); } catch (Exception error) { AddCleanup(canceled, error); }
        Task? publish; lock (c.PublishGate) publish = c.PendingPublish;
        if (publish is not null) try { await publish.ConfigureAwait(false); } catch (Exception error) { AddCleanup(canceled, error); }
    }
    private void Initialize(ConnectionState c, BidiSessionOptions session)
    {
        if (session.Tools is null || session.Messages is null || session.Tools.Any(tool => tool is null)) throw new ArgumentException("Nova Sonic session lists cannot contain null.", nameof(session));
        Put(c, "sessionStart", new { inferenceConfiguration = new { maxTokens = c.Options.MaxTokens, topP = c.Options.TopP, temperature = c.Options.Temperature } });
        var prompt = new Dictionary<string, object>
        {
            ["promptName"] = c.Prompt,
            ["textOutputConfiguration"] = new { mediaType = "text/plain" },
            ["audioOutputConfiguration"] = new { mediaType = "audio/lpcm", sampleRateHertz = c.Options.OutputSampleRate, sampleSizeBits = 16, channelCount = 1, voiceId = c.Options.VoiceId, encoding = "base64", audioType = "SPEECH" },
            ["toolUseOutputConfiguration"] = new { mediaType = "application/json" }
        };
        if (session.Tools.Count != 0) prompt["toolConfiguration"] = new { tools = session.Tools.Select(tool => new { toolSpec = new { name = tool.Name, description = tool.Description, inputSchema = new { json = tool.InputSchema.GetRawText() } } }).ToArray() };
        Put(c, "promptStart", prompt);
        WriteText(c, session.SystemPrompt ?? "You are a helpful assistant.", "SYSTEM");
        // Completed tool exchanges are retained as explicit text context, never as new remote calls.
        var calls = new Dictionary<string, ToolUseBlock>(StringComparer.Ordinal);
        foreach (var message in session.Messages)
        {
            if (message is null || message.Content.Any(block => block is not (TextBlock or ToolUseBlock or ToolResultBlock)))
                throw new ArgumentException("Nova Sonic history supports text and completed tool exchanges.", nameof(session));
            var text = new StringBuilder();
            foreach (var block in message.Content)
            {
                if (block is TextBlock value) text.Append(value.Text);
                else if (block is ToolUseBlock call)
                {
                    if (message.Role != Role.Assistant || calls.ContainsKey(call.ToolUseId)) throw new ArgumentException("Nova Sonic history has an invalid or repeated tool call.", nameof(session));
                    calls.Add(call.ToolUseId, call);
                    text.Append("\n[Completed tool call: ").Append(call.Name).Append("; input: ").Append(call.Input.GetRawText()).Append(']');
                }
                else if (block is ToolResultBlock result)
                {
                    if (message.Role != Role.User || !calls.Remove(result.ToolUseId)) throw new ArgumentException("Nova Sonic history has a tool result without its call.", nameof(session));
                    text.Append("\n[Tool result (").Append(result.Status).Append("): ").Append(result.Content).Append(']');
                }
            }
            if (message.Role is not (Role.User or Role.Assistant)) throw new ArgumentException("Nova Sonic history must use user and assistant roles.", nameof(session));
            if (text.Length != 0) WriteText(c, text.ToString(), message.Role == Role.Assistant ? "ASSISTANT" : "USER");
        }
        if (calls.Count != 0) throw new ArgumentException("Resolve pending tools before reconnecting Nova Sonic.", nameof(session));
    }
    private static void WriteText(ConnectionState c, string text, string role)
    {
        var name = Id();
        Put(c, "contentStart", new { promptName = c.Prompt, contentName = name, type = "TEXT", interactive = true, role, textInputConfiguration = new { mediaType = "text/plain" } });
        Put(c, "textInput", new { promptName = c.Prompt, contentName = name, content = text });
        Put(c, "contentEnd", new { promptName = c.Prompt, contentName = name });
    }
    private static void WriteMessage(ConnectionState c, BidiMessage message)
    {
        if (message.Content.Any(block => block is not (TextBlock or ToolResultBlock))) throw new ArgumentException("Nova Sonic does not support image input.", nameof(message));
        foreach (var block in message.Content)
            if (block is TextBlock text) WriteText(c, text.Text, "USER");
            else if (block is ToolResultBlock result)
            {
                var name = Id();
                Put(c, "contentStart", new { promptName = c.Prompt, contentName = name, type = "TOOL", interactive = false, role = "TOOL", toolResultInputConfiguration = new { toolUseId = result.ToolUseId, type = "TEXT", textInputConfiguration = new { mediaType = "text/plain" } } });
                Put(c, "toolResult", new { promptName = c.Prompt, contentName = name, content = JsonSerializer.Serialize(new { text = result.Content }) });
                Put(c, "contentEnd", new { promptName = c.Prompt, contentName = name });
            }
    }
    private static void Put(ConnectionState c, string name, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { @event = new Dictionary<string, object> { [name] = payload } });
        if (bytes.Length > c.MaximumInputBytes - c.BatchBytes) throw new ArgumentException("Nova Sonic input batch exceeds its configured byte limit.");
        c.BatchBytes += bytes.Length;
        c.Batch!.Add(bytes);
    }
    private static List<byte[]> Prepare(ConnectionState c, Action write)
    {
        c.Batch = []; c.BatchBytes = 0;
        try { write(); return c.Batch; }
        finally { c.Batch = null; }
    }
    private static Task<byte[]?> PublishAsync(ConnectionState c, CancellationToken publisher)
    {
        lock (c.PublishGate) { var task = NextAsync(); c.PendingPublish = task; return task; }
        async Task<byte[]?> NextAsync()
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(c.Token, publisher);
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                if (c.Initial.Count != 0) return c.Initial.Dequeue();
                return await c.Input.Reader.ReadAsync(stop.Token).ConfigureAwait(false);
            }
            catch (ChannelClosedException) { return null; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return null; }
        }
    }
    private async Task ReadOutputAsync(ConnectionState c)
    {
        Exception? failure = null;
        var iterator = c.Transport!.ReadAllAsync(c.Token).GetAsyncEnumerator(c.Token);
        try
        {
            while (await iterator.MoveNextAsync().ConfigureAwait(false))
            {
                var bytes = iterator.Current;
                if (bytes is null || bytes.Length > 1048576) throw new ModelException("Nova Sonic output event is absent or exceeds the 1 MiB event limit.");
                using var document = JsonDocument.Parse(bytes);
                IReadOnlyList<IBidiOutputEvent> values;
                lock (c.StateGate) { if (c.Closing) break; values = Process(c, document.RootElement); c.HasTraffic = true; }
                foreach (var value in values) await c.Output.Writer.WriteAsync(value, c.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (c.Token.IsCancellationRequested) { }
        catch (Exception error) when (c.Token.IsCancellationRequested && error is (ObjectDisposedException or IOException)) { }
        catch (Exception error) { failure = error is ModelException or BidiConnectionTimeoutException ? error : new ModelException("Nova Sonic output failed.", error); }
        finally
        {
            try { await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { if (failure is null) failure = new ModelException("Nova Sonic output cleanup failed.", cleanup); else AddCleanup(failure, cleanup); }
            c.Input.Writer.TryComplete(failure); c.Output.Writer.TryComplete(failure);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private IReadOnlyList<IBidiOutputEvent> Process(ConnectionState c, JsonElement root)
    {
        var result = new List<IBidiOutputEvent>();
        var envelope = root.GetProperty("event");
        if (envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Count() != 1) throw new ModelException("Nova Sonic output must contain exactly one event.");
        foreach (var entry in envelope.EnumerateObject())
        {
            var data = entry.Value; var contentId = Text(data, "contentId");
            switch (entry.Name)
            {
                case "completionStart": c.Interrupted = false; c.ResponseId = Text(data, "completionId") ?? Id(); Emit(new BidiResponseStartEvent(c.ResponseId)); break;
                case "contentStart":
                    if (contentId is null) throw new ModelException("Nova Sonic content has no identifier.");
                    var type = Text(data, "type"); var role = Text(data, "role") == "USER" ? Role.User : Role.Assistant; var suppress = false;
                    if (Text(data, "additionalModelFields") is { } extra) { using var fields = JsonDocument.Parse(extra); suppress = role == Role.Assistant && Text(fields.RootElement, "generationStage") == "FINAL"; }
                    if (c.Content.Count >= 128 || c.Content.ContainsKey(contentId)) throw new ModelException("Nova Sonic reused an active content identifier or exceeded its open-content limit.");
                    c.Content.Add(contentId, new ContentState(type, role, suppress));
                    if (type == "AUDIO") Emit(new BidiAudioStartEvent(contentId));
                    else if (type == "TEXT" && !suppress) Emit(new BidiTranscriptStartEvent(role, contentId));
                    break;
                case "textOutput":
                    var text = Text(data, "content") ?? throw new ModelException("Nova Sonic text output is absent.");
                    if ((contentId is null || !c.Content.TryGetValue(contentId, out var speaker) || speaker.Role == Role.Assistant) && IsInterruption(text)) { Interrupt(); break; }
                    var textState = State();
                    if (!textState.Suppress) Emit(new BidiTranscriptDeltaEvent(text, textState.Role, contentId!)); break;
                case "audioOutput":
                    _ = State(); var audio = Text(data, "content") ?? throw new ModelException("Nova Sonic audio output is absent."); _ = Convert.FromBase64String(audio);
                    Emit(new BidiAudioDeltaEvent(audio, contentId!, c.Options.OutputSampleRate, 1, "pcm")); break;
                case "toolUse":
                    var tool = State(); tool.ToolId = Text(data, "toolUseId") ?? tool.ToolId; tool.ToolName = Text(data, "toolName") ?? tool.ToolName; tool.ToolInput.Append(Text(data, "content"));
                    if (tool.ToolInput.Length > 1048576) throw new ModelException("Nova Sonic tool input exceeds the 1 MiB limit."); break;
                case "contentEnd":
                    var ended = State(); c.Content.Remove(contentId!);
                    if (ended.Type == "TOOL")
                    {
                        if (ended.ToolId is null || ended.ToolName is null) throw new ModelException("Nova Sonic tool call is missing its correlation or name.");
                        using var input = JsonDocument.Parse(ended.ToolInput.ToString());
                        if (input.RootElement.ValueKind != JsonValueKind.Object) throw new ModelException("Nova Sonic tool arguments must be a JSON object.");
                        if (c.PendingTools.Count >= 128 || c.PendingTools.ContainsKey(ended.ToolId)) throw new ModelException("Nova Sonic reused an unresolved tool identifier or exceeded its 128-call pending limit.");
                        c.PendingTools.Add(ended.ToolId, ended.ToolName);
                        Emit(new BidiToolUseBlocksEvent([new ToolUseBlock(ended.ToolId, ended.ToolName, input.RootElement)]));
                    }
                    else if (ended.Type == "AUDIO") Emit(new BidiAudioStopEvent(contentId!));
                    else if (ended.Type == "TEXT" && !ended.Suppress) Emit(new BidiTranscriptStopEvent(ended.Role, contentId!));
                    if (Text(data, "stopReason") == "INTERRUPTED") Interrupt();
                    if (Text(data, "stopReason") == "END_TURN") EndResponse(); break;
                case "completionEnd": if (Text(data, "stopReason") == "INTERRUPTED") Interrupt(); EndResponse(); break;
                case "usageEvent":
                    var inputTokens = Count(data, "totalInputTokens"); var outputTokens = Count(data, "totalOutputTokens"); var totalTokens = Count(data, "totalTokens");
                    if (inputTokens < c.InputTokens || outputTokens < c.OutputTokens || totalTokens < c.TotalTokens) throw new ModelException("Nova Sonic cumulative usage cannot decrease within a connection.");
                    IReadOnlyDictionary<string, long>? inputDetails = null, outputDetails = null;
                    if (data.TryGetProperty("details", out var details) && details.TryGetProperty("delta", out var delta))
                    {
                        inputDetails = Categories(delta, "input"); outputDetails = Categories(delta, "output");
                    }
                    Emit(new BidiUsageEvent(new Usage(inputTokens - c.InputTokens, outputTokens - c.OutputTokens, ReportedTotalTokens: totalTokens - c.TotalTokens), inputDetails, outputDetails));
                    c.InputTokens = inputTokens; c.OutputTokens = outputTokens; c.TotalTokens = totalTokens;
                    break;
                default: Emit(new BidiNativeEvent(root)); break;
            }
            ContentState State() => contentId is not null && c.Content.TryGetValue(contentId, out var value) ? value : throw new ModelException("Nova Sonic output refers to unknown content.");
        }
        return result;
        void Emit(IBidiOutputEvent value) => result.Add(value);
        void Interrupt() { if (!c.Interrupted) { c.Interrupted = true; Emit(new BidiBargeInEvent()); } }
        void EndResponse() { if (c.ResponseId is { } id) { Emit(new BidiResponseStopEvent(id)); c.ResponseId = null; } }
    }
    private static bool IsInterruption(string text)
    {
        if (!text.TrimStart().StartsWith("{", StringComparison.Ordinal)) return false;
        try { using var value = JsonDocument.Parse(text); return value.RootElement.ValueKind == JsonValueKind.Object && value.RootElement.EnumerateObject().Count() == 1 && value.RootElement.TryGetProperty("interrupted", out var flag) && flag.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }
    private static long Count(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.TryGetInt64(out var count) && count >= 0 ? count : throw new ModelException("Nova Sonic usage must contain nonnegative integer counts.");
    private static IReadOnlyDictionary<string, long>? Categories(JsonElement delta, string name)
    {
        if (!delta.TryGetProperty(name, out var values)) return null;
        if (values.ValueKind != JsonValueKind.Object) throw new ModelException("Nova Sonic usage categories must be objects.");
        return values.EnumerateObject().ToDictionary(pair => pair.Name, pair => Count(values, pair.Name), StringComparer.Ordinal);
    }
    private static async Task CloseAsync(ConnectionState c)
    {
        Exception? primary = null;
        await c.SendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!c.Lifetime.IsCancellationRequested)
            {
                var closing = Prepare(c, () =>
                {
                    if (c.AudioContent is { } audio) Put(c, "contentEnd", new { promptName = c.Prompt, contentName = audio });
                    Put(c, "promptEnd", new { promptName = c.Prompt }); Put(c, "sessionEnd", new { });
                });
                // Do not wait behind a blocked transport when stopping; HTTP cancellation is authoritative.
                foreach (var item in closing) if (!c.Input.Writer.TryWrite(item)) break;
            }
            c.Input.Writer.TryComplete();
        }
        catch (Exception error) { primary = error; }
        finally { c.SendGate.Release(); }
        try { c.Lifetime.Cancel(); } catch (Exception error) { Add(error); }
        try { if (c.Transport is not null) await c.Transport.StopAsync().ConfigureAwait(false); } catch (Exception error) { Add(error); }
        try { await c.Reader.ConfigureAwait(false); } catch (Exception error) { Add(error); }
        Task? publish; lock (c.PublishGate) publish = c.PendingPublish;
        if (publish is not null) try { await publish.ConfigureAwait(false); } catch (Exception error) { Add(error); }
        c.Output.Writer.TryComplete(); c.Lifetime.Dispose();
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        void Add(Exception error) { if (primary is null) primary = error; else AddCleanup(primary, error); }
    }
    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        errors.Add(cleanup);
    }
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string Id() => Guid.NewGuid().ToString("N");
    private sealed class ContentState(string? type, Role role, bool suppress)
    {
        internal string? Type { get; } = type; internal Role Role { get; } = role; internal bool Suppress { get; } = suppress;
        internal string? ToolId, ToolName; internal StringBuilder ToolInput { get; } = new();
    }
    private sealed class ConnectionState
    {
        internal ConnectionState(CancellationToken caller, BedrockNovaSonicModelOptions options, BidiSessionOptions session)
        {
            Options = options; InitialSession = session with { Tools = session.Tools?.ToArray()!, Messages = session.Messages?.ToArray()! };
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller); Token = Lifetime.Token; MaximumInputBytes = options.MaximumInputBytes;
            Input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.MaximumBufferedInputEvents) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
            Output = Channel.CreateBounded<IBidiOutputEvent>(new BoundedChannelOptions(options.MaximumBufferedOutputEvents) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        }
        internal BedrockNovaSonicModelOptions Options { get; }
        internal BidiSessionOptions InitialSession { get; }
        internal object StateGate { get; } = new();
        internal Dictionary<string, string> PendingTools { get; } = new(StringComparer.Ordinal);
        internal bool HasTraffic, Closing; internal ModelException? InputFailure;
        internal string Prompt { get; } = Id();
        internal Channel<byte[]> Input { get; }
        internal Channel<IBidiOutputEvent> Output { get; }
        internal Queue<byte[]> Initial = new();
        internal List<byte[]>? Batch;
        internal int BatchBytes; internal int MaximumInputBytes { get; }
        internal CancellationTokenSource Lifetime { get; }
        internal CancellationToken Token { get; }
        internal SemaphoreSlim SendGate { get; } = new(1, 1); internal object PublishGate { get; } = new(); internal Task? PendingPublish;
        internal INovaSonicDuplex? Transport; internal Task Reader = Task.CompletedTask;
        internal Dictionary<string, ContentState> Content { get; } = new(StringComparer.Ordinal); internal string? AudioContent, ResponseId; internal int Receiver;
        internal long InputTokens, OutputTokens, TotalTokens; internal bool Interrupted;
    }
}
