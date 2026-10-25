using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Strands.Agents.Bidi;
using ToolResultBlock = Strands.Agents.ToolResultBlock;
using ToolUseBlock = Strands.Agents.ToolUseBlock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockNovaSonicFeatureTests
{
    [Fact]
    public async Task StartPublishesBeforeInvokeReturnsAndStreamsConversationTextAndPcm()
    {
        using var caller = new CancellationTokenSource();
        var wire = new NovaWire();
        using var client = new DuplexClient(wire);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0" });
        Task? starting = null;
        Task<List<IAgentStreamEvent>>? receiving = null;
        Exception? primary = null;
        try
        {
            Assert.Equal("amazon.nova-sonic-v1:0", ((IModelMetadataProvider)model).Metadata.ModelId);
            Assert.Equal(new BidiAudioStreamConfig(16000, 1, "pcm"), model.Audio.Input);
            Assert.Equal(new BidiAudioStreamConfig(24000, 1, "pcm"), model.Audio.Output);
            Assert.True(model.Connection.AutoRestart); // Intentional default evolution: Core now schedules long-lived Nova reconnection.
            var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } });
            starting = model.StartAsync(new() { SystemPrompt = "Speak briefly.", Tools = [new("lookup", "Lookup one value", schema)] }, caller.Token);
            await Within(starting);
            Assert.True(client.InvokeCount == 1, "StartAsync must invoke the official AWS duplex operation; a no-op API scaffold is not a Nova connection.");
            Assert.True(client.InitializationBeforeResponse, "The real BodyPublisher must deliver initialization before the held SDK invocation response is released.");
            Assert.Equal("amazon.nova-sonic-v1:0", client.Request!.ModelId);
            var initial = client.Sent;
            Assert.Equal("sessionStart", initial[0].EnumerateObject().Single().Name);
            var prompt = initial[1].GetProperty("promptStart");
            var promptName = prompt.GetProperty("promptName").GetString()!;
            Assert.False(string.IsNullOrEmpty(promptName));
            Assert.Equal(24000, prompt.GetProperty("audioOutputConfiguration").GetProperty("sampleRateHertz").GetInt32());
            Assert.Equal("matthew", prompt.GetProperty("audioOutputConfiguration").GetProperty("voiceId").GetString());
            var specification = prompt.GetProperty("toolConfiguration").GetProperty("tools")[0].GetProperty("toolSpec");
            Assert.Equal("lookup", specification.GetProperty("name").GetString());
            using (var sentSchema = JsonDocument.Parse(specification.GetProperty("inputSchema").GetProperty("json").GetString()!))
                Assert.Equal("string", sentSchema.RootElement.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());

            await model.SendAsync(new BidiMessage([new TextBlock("hello \u03c9")]), caller.Token);
            var pcm = new byte[] { 0, 1, 2, 3, 4, 5 };
            var audio = new BidiAudioDelta(pcm);
            pcm[0] = 255;
            await model.SendAsync(audio, caller.Token);
            await Within(client.AudioSent.Task);
            var sent = client.Sent;
            var text = Assert.Single(sent, value => value.TryGetProperty("textInput", out var block) && block.GetProperty("content").GetString() == "hello \u03c9").GetProperty("textInput");
            Assert.Equal(promptName, text.GetProperty("promptName").GetString());
            AssertClosedContentGroup(sent, text.GetProperty("contentName").GetString()!, "USER", "TEXT");
            var inputAudio = Assert.Single(sent, value => value.TryGetProperty("audioInput", out _)).GetProperty("audioInput");
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, Convert.FromBase64String(inputAudio.GetProperty("content").GetString()!));
            Assert.Equal(promptName, inputAudio.GetProperty("promptName").GetString());
            var inputAudioName = inputAudio.GetProperty("contentName").GetString();
            var audioStart = Assert.Single(sent, value => value.TryGetProperty("contentStart", out var block) && block.GetProperty("contentName").GetString() == inputAudioName).GetProperty("contentStart");
            Assert.Equal(16000, audioStart.GetProperty("audioInputConfiguration").GetProperty("sampleRateHertz").GetInt32());

            receiving = CollectUntil(model.ReceiveAsync(caller.Token), value => value is BidiResponseStopEvent);
            wire.AssistantResponse(promptName, "Ready.", new byte[] { 9, 8, 7, 6 });
            var events = await Within(receiving);
            Assert.Equal("Ready.", string.Concat(events.OfType<BidiTranscriptDeltaEvent>().Where(value => value.Role == Role.Assistant).Select(value => value.Delta)));
            var outputAudio = Assert.Single(events.OfType<BidiAudioDeltaEvent>());
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, Convert.FromBase64String(outputAudio.Audio));
            Assert.Equal((24000, 1, "pcm"), (outputAudio.SampleRate, outputAudio.Channels, outputAudio.Format));
            var begin = Assert.Single(events.OfType<BidiResponseStartEvent>());
            Assert.Equal(begin.ResponseId, Assert.Single(events.OfType<BidiResponseStopEvent>()).ResponseId);
            Assert.Equal(Assert.Single(events.OfType<BidiAudioStartEvent>()).ContentId, outputAudio.ContentId);
            Assert.Equal(outputAudio.ContentId, Assert.Single(events.OfType<BidiAudioStopEvent>()).ContentId);
            await model.StopAsync(caller.Token);
            await Within(client.JoinAsync());
            Assert.True(client.InputEnded);
            Assert.True(wire.Disposed);
            Assert.False(client.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            caller.Cancel();
            wire.Complete();
            await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(starting, caller.Token), () => Join(receiving, caller.Token), () => client.JoinAsync(),
                () => { Assert.False(client.Disposed); return Task.CompletedTask; }, () => { wire.Dispose(); return Task.CompletedTask; });
        }
    }

    [Fact]
    public async Task NovaToolCallExecutesOneCoreToolAndReturnsOneCorrelatedResult()
    {
        using var caller = new CancellationTokenSource();
        var wire = new NovaWire();
        using var client = new DuplexClient(wire);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0" });
        var state = new Dictionary<string, object?> { ["principal"] = "fixture" };
        var calls = 0;
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } });
        var tool = new FunctionTool(new("lookup", "Lookup one value", schema), (input, context, _) =>
        {
            Assert.Same(state, context.InvocationState);
            Assert.Equal("London", input.GetProperty("query").GetString());
            Interlocked.Increment(ref calls);
            return new ValueTask<string>("seven from lookup");
        });
        var host = new Agent(new() { Model = new NoOrdinaryModelCalls(), Tools = [tool] });
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SystemPrompt = "Use lookup." });
        Task? starting = null;
        Task<List<IAgentStreamEvent>>? receiving = null;
        Exception? primary = null;
        try
        {
            starting = agent.StartAsync(state, caller.Token);
            await Within(starting);
            Assert.True(client.InvokeCount == 1, "The public BidiAgent must enter the native AWS Nova provider before a tool conversation can run.");
            var prompt = client.Sent.Single(value => value.TryGetProperty("promptStart", out _)).GetProperty("promptStart").GetProperty("promptName").GetString()!;
            receiving = CollectUntil(agent.ReceiveAsync(caller.Token), value => value is ToolResultEvent);
            wire.Event("contentStart", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "tool-content", type = "TOOL", role = "TOOL" });
            wire.Event("toolUse", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "tool-content", toolUseId = "nova-tool-1", toolName = "lookup", content = "{\"query\":\"London\"}" });
            wire.Event("contentEnd", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "tool-content", type = "TOOL", stopReason = "TOOL_USE" });
            await Within(receiving);
            await Within(client.ToolResultSent.Task);
            Assert.Equal(1, calls);
            var sent = client.Sent;
            var result = Assert.Single(sent, value => value.TryGetProperty("toolResult", out _)).GetProperty("toolResult");
            Assert.Equal(prompt, result.GetProperty("promptName").GetString());
            var contentName = result.GetProperty("contentName").GetString()!;
            var contentStart = AssertClosedContentGroup(sent, contentName, "TOOL", "TOOL");
            Assert.Equal("nova-tool-1", contentStart.GetProperty("toolResultInputConfiguration").GetProperty("toolUseId").GetString());
            using (var value = JsonDocument.Parse(result.GetProperty("content").GetString()!))
                Assert.Equal("seven from lookup", value.RootElement.GetProperty("text").GetString());
            var recordedCall = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>());
            var recordedResult = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
            Assert.Equal("nova-tool-1", recordedCall.ToolUseId);
            Assert.Equal(recordedCall.ToolUseId, recordedResult.ToolUseId);
            Assert.Equal("seven from lookup", recordedResult.Content);
            Assert.Empty(host.Messages);
            Assert.Single(state);
            await agent.StopAsync(caller.Token);
            await Within(client.JoinAsync());
            Assert.False(client.Disposed);
            Assert.True(wire.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            caller.Cancel();
            wire.Complete();
            await Cleanup(primary, () => agent.DisposeAsync().AsTask(), () => model.DisposeAsync().AsTask(), () => Join(starting, caller.Token), () => Join(receiving, caller.Token), () => client.JoinAsync(),
                () => { Assert.False(client.Disposed); return Task.CompletedTask; }, () => host.InitializeAsync(), () => host.DisposeAsync().AsTask(), () => { wire.Dispose(); return Task.CompletedTask; });
        }
    }

    [Fact]
    public async Task CancellingAReceivePreservesTheCallerTokenAndStopJoinsTheBorrowedTransport()
    {
        using var caller = new CancellationTokenSource();
        var wire = new NovaWire(); using var client = new DuplexClient(wire);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0" });
        Task? receiving = null; Exception? primary = null;
        try
        {
            await model.StartAsync();
            receiving = CollectUntil(model.ReceiveAsync(caller.Token), _ => false);
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
            Assert.Equal(caller.Token, error.CancellationToken);
            await model.StopAsync(); await Within(client.JoinAsync());
            Assert.True(wire.Disposed); Assert.True(client.InputEnded); Assert.False(client.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally { caller.Cancel(); wire.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(receiving, caller.Token), () => client.JoinAsync()); wire.Dispose(); }
    }

    [Fact]
    public async Task AResponseHeldDuringStartCanBeCancelledWithoutAdoptingTheClient()
    {
        using var caller = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new NovaWire(); using var client = new DuplexClient(wire) { ResponseGate = gate.Task };
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0" });
        Task? starting = null; Exception? primary = null;
        try
        {
            starting = model.StartAsync(cancellationToken: caller.Token);
            await Within(client.ResponseEntered.Task); caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            Assert.Equal(caller.Token, error.CancellationToken);
            await Within(client.JoinAsync()); Assert.False(client.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally { caller.Cancel(); gate.TrySetResult(); wire.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(starting, caller.Token), () => client.JoinAsync()); wire.Dispose(); }
    }

    [Fact]
    public async Task MalformedContentFailsTheReceiverAndStillClosesTheResponseAndPublisher()
    {
        using var caller = new CancellationTokenSource();
        var wire = new NovaWire(); using var client = new DuplexClient(wire);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0" });
        Task? receiving = null; Exception? primary = null;
        try
        {
            await model.StartAsync();
            receiving = CollectUntil(model.ReceiveAsync(caller.Token), _ => false);
            wire.Event("textOutput", new { contentId = "not-started", content = "invalid" });
            var error = await Assert.ThrowsAsync<ModelException>(() => receiving);
            primary = error; // The final join must observe this already asserted failure without replacing it.
            Assert.Contains("unknown content", error.Message);
            Assert.Same(error, await Assert.ThrowsAsync<ModelException>(() => model.StopAsync()));
            await Within(client.JoinAsync()); Assert.True(wire.Disposed); Assert.True(client.InputEnded); Assert.False(client.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally { caller.Cancel(); wire.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(receiving, caller.Token), () => client.JoinAsync()); wire.Dispose(); }
    }

    [Fact]
    public async Task HeldPublisherAppliesPcmBackpressureAndCancelsTheWaitingSendWithItsCallerToken()
    {
        using var caller = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new NovaWire(); using var client = new DuplexClient(wire) { PublisherGate = gate.Task };
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0", MaximumBufferedInputEvents = 2 });
        Task? sending = null; Exception? primary = null;
        try
        {
            await model.StartAsync(); await Within(client.PublisherBlocked.Task);
            await model.SendAsync(new BidiAudioDelta([0, 1])); // opens one audio group plus its first chunk
            sending = model.SendAsync(new BidiAudioDelta([2, 3]), caller.Token);
            Assert.False(sending.IsCompleted, "PCM must wait when the bounded publisher queue is full.");
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        catch (Exception error) { primary = error; throw; }
        finally { caller.Cancel(); gate.TrySetResult(); wire.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(sending, caller.Token), () => client.JoinAsync()); wire.Dispose(); }
    }

    [Fact]
    public async Task StopCancelsAPressuredSendBeforeWaitingForItsSendGate()
    {
        using var cleanupCancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new NovaWire(); using var client = new DuplexClient(wire) { PublisherGate = gate.Task };
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0", MaximumBufferedInputEvents = 2 });
        Task? sending = null, stopping = null; Exception? primary = null;
        try
        {
            await model.StartAsync(); await Within(client.PublisherBlocked.Task);
            await model.SendAsync(new BidiAudioDelta([0, 1]));
            sending = model.SendAsync(new BidiAudioDelta([2, 3]));
            Assert.False(sending.IsCompleted);
            stopping = model.StopAsync();
            await Within(stopping);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.True(wire.Disposed); Assert.False(client.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            gate.TrySetResult(); cleanupCancellation.Cancel(); wire.Complete();
            await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(sending, cleanupCancellation.Token), () => Join(stopping, cleanupCancellation.Token), () => client.JoinAsync()); wire.Dispose();
        }
    }

    [Fact]
    public async Task OversizedLaterBlockRejectsTheWholeInputBeforePublishingItsFirstBlock()
    {
        var wire = new NovaWire(); using var client = new DuplexClient(wire);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0", MaximumInputBytes = 4096 });
        Exception? primary = null;
        try
        {
            await model.StartAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => model.SendAsync(new BidiMessage([new Strands.Agents.TextBlock("must-not-publish"), new Strands.Agents.TextBlock(new string('x', 8192))])));
            await model.SendAsync(new BidiMessage([new Strands.Agents.TextBlock("after-rejected-batch")]));
            await Within(client.BarrierSent.Task);
            Assert.DoesNotContain(client.Sent, value => value.TryGetProperty("textInput", out var text) && text.GetProperty("content").GetString() == "must-not-publish");
        }
        catch (Exception error) { primary = error; throw; }
        finally { wire.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => client.JoinAsync()); wire.Dispose(); }
    }

    [Fact]
    public async Task CumulativeNovaUsageBecomesAdditiveTotalsAndPreservesSpeechAndTextCategories()
    {
        var wire = new NovaWire(); using var client = new DuplexClient(wire);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0" });
        Task<List<IAgentStreamEvent>>? receiving = null; Exception? primary = null;
        try
        {
            await model.StartAsync();
            receiving = CollectUntil(model.ReceiveAsync(), value => value is BidiResponseStopEvent);
            wire.Event("completionStart", new { completionId = "usage-completion" });
            wire.Event("usageEvent", new { totalInputTokens = 100, totalOutputTokens = 20, totalTokens = 120, details = new { delta = new { input = new { speechTokens = 90, textTokens = 10 }, output = new { speechTokens = 15, textTokens = 5 } } } });
            wire.Event("usageEvent", new { totalInputTokens = 103, totalOutputTokens = 25, totalTokens = 128, details = new { delta = new { input = new { speechTokens = 2, textTokens = 1 }, output = new { speechTokens = 4, textTokens = 1 } } } });
            wire.Event("completionEnd", new { completionId = "usage-completion", stopReason = "END_TURN" });
            var usage = (await Within(receiving)).OfType<BidiUsageEvent>().ToArray();
            Assert.Equal(2, usage.Length);
            Assert.Equal(new Usage(100, 20, ReportedTotalTokens: 120), usage[0].Usage);
            Assert.Equal(new Usage(3, 5, ReportedTotalTokens: 8), usage[1].Usage);
            Assert.Equal(2, usage[1].InputDetails!["speechTokens"]); Assert.Equal(1, usage[1].InputDetails!["textTokens"]);
            Assert.Equal(4, usage[1].OutputDetails!["speechTokens"]);
        }
        catch (Exception error) { primary = error; throw; }
        finally { wire.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(receiving, CancellationToken.None), () => client.JoinAsync()); wire.Dispose(); }
    }

    [Fact]
    public async Task AssistantInterruptionNotifiesPlaybackWithoutDisplayingTheProtocolMarkerAsSpeech()
    {
        var wire = new NovaWire(); using var client = new DuplexClient(wire);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "amazon.nova-sonic-v1:0" });
        Task<List<IAgentStreamEvent>>? receiving = null; Exception? primary = null;
        try
        {
            await model.StartAsync(); receiving = CollectUntil(model.ReceiveAsync(), value => value is BidiResponseStopEvent);
            wire.Event("completionStart", new { completionId = "interrupted-completion" });
            wire.Event("contentStart", new { contentId = "interruption", type = "TEXT", role = "ASSISTANT" });
            wire.Event("textOutput", new { contentId = "interruption", content = "{ \"interrupted\" : true }" });
            wire.Event("contentEnd", new { contentId = "interruption", type = "TEXT", stopReason = "INTERRUPTED" });
            wire.Event("completionEnd", new { completionId = "interrupted-completion", stopReason = "INTERRUPTED" });
            var events = await Within(receiving);
            Assert.Single(events.OfType<BidiBargeInEvent>());
            Assert.Empty(events.OfType<BidiTranscriptDeltaEvent>());
            Assert.Single(events.OfType<BidiResponseStopEvent>());
        }
        catch (Exception error) { primary = error; throw; }
        finally { wire.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(receiving, CancellationToken.None), () => client.JoinAsync()); wire.Dispose(); }
    }

    [Fact]
    public void InvalidBufferConfigurationFailsBeforeAnyAwsOperation()
    {
        using var wire = new NovaWire(); using var client = new DuplexClient(wire);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockNovaSonicModel(client, new() { ModelId = "nova", MaximumBufferedInputEvents = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockNovaSonicModel(client, new() { ModelId = "nova", MaximumBufferedOutputEvents = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockNovaSonicModel(client, new() { ModelId = "nova", MaximumInputBytes = 0 }));
        Assert.Equal(0, client.InvokeCount);
    }

    [Fact]
    public async Task CancellationAfterAFiniteContentStartInvalidatesTheConnectionInsteadOfReusingAnOpenGroup()
    {
        using var caller = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new NovaWire(); using var client = new DuplexClient(wire) { UserStartGate = gate.Task };
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "nova", MaximumBufferedInputEvents = 1 });
        Task? sending = null; Exception? primary = null;
        try
        {
            await model.StartAsync();
            sending = model.SendAsync(new BidiMessage([new Strands.Agents.TextBlock("finite content")]), caller.Token);
            await Within(client.UserStartObserved.Task); Assert.False(sending.IsCompleted);
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending); Assert.Equal(caller.Token, error.CancellationToken);
            Assert.True(wire.Disposed, "A canceled finite content group must not leave a reusable live transport.");
            await Assert.ThrowsAsync<ModelException>(() => model.SendAsync(new BidiMessage([new Strands.Agents.TextBlock("must reject after partial publication")])));
            Assert.Equal(1, client.InvokeCount);
        }
        catch (Exception error) { primary = error; throw; }
        finally { caller.Cancel(); gate.TrySetResult(); wire.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(sending, caller.Token), () => client.JoinAsync()); wire.Dispose(); }
    }

    [Fact]
    public void DefaultAndExplicitReconnectPoliciesUseNativeConnectionSettings()
    {
        using var wire = new NovaWire(); using var client = new DuplexClient(wire);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "nova" });
        Assert.True(model.Connection.AutoRestart); Assert.Equal(TimeSpan.FromMinutes(7), model.Connection.RestartAfter);
        var manual = new BedrockNovaSonicModel(client, new() { ModelId = "nova", Connection = new() { AutoRestart = false, RestartAfter = TimeSpan.FromMinutes(2) } });
        Assert.False(manual.Connection.AutoRestart); Assert.Equal(TimeSpan.FromMinutes(2), manual.Connection.RestartAfter);
        Assert.Equal(0, client.InvokeCount);
    }

    [Fact]
    public async Task ConfigurationIsAppliedOnTheWireAndOnlyTheNextConnectionAdoptsUpdates()
    {
        var first = new NovaWire(); var second = new NovaWire(); using var client = new DuplexClient(first);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "nova-old", MaxTokens = 321, Temperature = 0.25, TopP = 0.6, VoiceId = "tiffany", OutputSampleRate = 16000 });
        Exception? primary = null; Task<List<IAgentStreamEvent>>? receiving = null;
        try
        {
            await model.StartAsync();
            var inference = client.Sent[0].GetProperty("sessionStart").GetProperty("inferenceConfiguration");
            Assert.Equal(321, inference.GetProperty("maxTokens").GetInt32()); Assert.Equal(0.25, inference.GetProperty("temperature").GetDouble()); Assert.Equal(0.6, inference.GetProperty("topP").GetDouble());
            model.UpdateConfig(options => options with { ModelId = "nova-new", VoiceId = "matthew", MaxTokens = 123, OutputSampleRate = 24000, InputSampleRate = 8000 });
            Assert.Equal("nova-new", model.GetConfig().ModelId); Assert.Equal("nova-old", model.Metadata.ModelId);
            Assert.Equal(16000, model.Audio.Output.SampleRate); Assert.Equal(16000, model.Audio.Input.SampleRate);
            receiving = CollectUntil(model.ReceiveAsync(), value => value is BidiResponseStopEvent);
            first.AssistantResponse("prompt", "old audio", [0, 1]);
            Assert.Equal(16000, Assert.Single((await Within(receiving)).OfType<BidiAudioDeltaEvent>()).SampleRate);
            client.NextOutput = second; await model.RestartAsync(new());
            await model.SendAsync(new BidiMessage([new Strands.Agents.TextBlock("after-rejected-batch")])); await Within(client.BarrierSent.Task);
            var next = client.SentForInvocation(2);
            Assert.Equal(123, next[0].GetProperty("sessionStart").GetProperty("inferenceConfiguration").GetProperty("maxTokens").GetInt32());
            Assert.Equal("matthew", next[1].GetProperty("promptStart").GetProperty("audioOutputConfiguration").GetProperty("voiceId").GetString());
            Assert.Equal("nova-new", client.Request!.ModelId); Assert.Equal(24000, model.Audio.Output.SampleRate); Assert.Equal(8000, model.Audio.Input.SampleRate);
            Assert.True(first.Disposed); Assert.False(client.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally { first.Complete(); second.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(receiving, CancellationToken.None), () => client.JoinAsync()); first.Dispose(); second.Dispose(); }
    }

    [Fact]
    public async Task RestartReseedsCompletedTextAndToolContextInTheSecondOfficialInvocation()
    {
        var first = new NovaWire(); var second = new NovaWire(); using var client = new DuplexClient(first);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "nova" }); Exception? primary = null;
        var history = new Strands.Agents.Message[]
        {
            new(Role.User, [new Strands.Agents.TextBlock("Find the weather in London")]),
            new(Role.Assistant, [new ToolUseBlock("done-1", "lookup", JsonSerializer.SerializeToElement(new { city = "London" }))]),
            new(Role.User, [new ToolResultBlock("done-1", "London is sunny")]),
            new(Role.Assistant, [new Strands.Agents.TextBlock("It is sunny in London.")])
        };
        try
        {
            await model.StartAsync(new() { SystemPrompt = "Continue our conversation." });
            client.NextOutput = second;
            await model.RestartAsync(new() { SystemPrompt = "Continue our conversation.", Messages = history });
            await model.SendAsync(new BidiMessage([new Strands.Agents.TextBlock("after-rejected-batch")])); await Within(client.BarrierSent.Task);
            Assert.Equal(2, client.InvokeCount); Assert.True(first.Disposed); Assert.False(second.Disposed);
            var text = client.SentForInvocation(2).Where(value => value.TryGetProperty("textInput", out _)).Select(value => value.GetProperty("textInput").GetProperty("content").GetString()!).ToArray();
            Assert.Contains("Find the weather in London", text); Assert.Contains("It is sunny in London.", text);
            Assert.Contains(text, value => value.Contains("lookup") && value.Contains("London")); Assert.Contains(text, value => value.Contains("London is sunny"));
            Assert.DoesNotContain(client.SentForInvocation(2), value => value.TryGetProperty("toolResult", out _));
        }
        catch (Exception error) { primary = error; throw; }
        finally { first.Complete(); second.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => client.JoinAsync()); first.Dispose(); second.Dispose(); }
    }

    [Fact]
    public async Task RestartRejectsPendingToolsBeforeClosingTheHealthyConnectionAndTheirResultStillCorrelates()
    {
        var wire = new NovaWire(); using var client = new DuplexClient(wire);
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "nova" });
        Task<List<IAgentStreamEvent>>? receiving = null; Exception? primary = null;
        try
        {
            await model.StartAsync(); receiving = CollectUntil(model.ReceiveAsync(), value => value is BidiToolUseBlocksEvent);
            wire.Event("contentStart", new { contentId = "pending", type = "TOOL", role = "TOOL" });
            wire.Event("toolUse", new { contentId = "pending", toolUseId = "pending-1", toolName = "lookup", content = "{}" });
            wire.Event("contentEnd", new { contentId = "pending", type = "TOOL", stopReason = "TOOL_USE" }); await Within(receiving);
            await Assert.ThrowsAsync<InvalidOperationException>(() => model.RestartAsync(new()));
            Assert.False(wire.Disposed); Assert.Equal(1, client.InvokeCount);
            await model.SendAsync(new BidiMessage([new ToolResultBlock("pending-1", "still on original session")])); await Within(client.ToolResultSent.Task);
            var resultStart = Assert.Single(client.Sent, value => value.TryGetProperty("contentStart", out var content) && content.TryGetProperty("toolResultInputConfiguration", out _));
            Assert.Equal("pending-1", resultStart.GetProperty("contentStart").GetProperty("toolResultInputConfiguration").GetProperty("toolUseId").GetString());
        }
        catch (Exception error) { primary = error; throw; }
        finally { wire.Complete(); await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(receiving, CancellationToken.None), () => client.JoinAsync()); wire.Dispose(); }
    }

    [Fact]
    public async Task PublicAgentReconnectsAfterANativeTimeoutWithoutExecutingItsCompletedToolAgain()
    {
        using var caller = new CancellationTokenSource();
        var first = new NovaWire(); var second = new NovaWire(); using var client = new DuplexClient(first) { NextOutputAfterFirst = second };
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "nova" }); var calls = 0;
        var host = new Agent(new() { Model = new NoOrdinaryModelCalls(), Tools = [new FunctionTool(new("lookup", "lookup", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { calls++; return new ValueTask<string>("one completed effect"); })] });
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>((value, _) => { Assert.Null(value.Error); restarted.TrySetResult(); return default; });
        Task<List<IAgentStreamEvent>>? receiving = null; Exception? primary = null;
        try
        {
            await agent.StartAsync(cancellationToken: caller.Token);
            receiving = CollectUntil(agent.ReceiveAsync(caller.Token), value => value is BidiConnectionStartEvent && client.InvokeCount == 2);
            first.Event("contentStart", new { contentId = "one-tool", type = "TOOL", role = "TOOL" });
            first.Event("toolUse", new { contentId = "one-tool", toolUseId = "only-call", toolName = "lookup", content = "{}" });
            first.Event("contentEnd", new { contentId = "one-tool", type = "TOOL", stopReason = "TOOL_USE" }); await Within(client.ToolResultSent.Task);
            first.Timeout();
            await Within(restarted.Task); await Within(receiving);
            await agent.SendAsync(new BidiMessage([new Strands.Agents.TextBlock("after-rejected-batch")]), caller.Token); await Within(client.BarrierSent.Task);
            Assert.Equal(2, client.InvokeCount); Assert.Equal(1, calls);
            Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>()); Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
            Assert.Contains(client.SentForInvocation(2), value => value.TryGetProperty("textInput", out var content) && content.GetProperty("content").GetString()!.Contains("one completed effect"));
            Assert.Empty(host.Messages); Assert.True(first.Disposed); Assert.False(client.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally { caller.Cancel(); first.Complete(); second.Complete(); await Cleanup(primary, () => agent.DisposeAsync().AsTask(), () => model.DisposeAsync().AsTask(), () => Join(receiving, caller.Token), () => client.JoinAsync(), () => host.DisposeAsync().AsTask()); first.Dispose(); second.Dispose(); }
    }

    [Fact]
    public void InvalidInferenceOrRestartConfigurationCannotPublishOrMutateAcceptedConfiguration()
    {
        using var wire = new NovaWire(); using var client = new DuplexClient(wire);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockNovaSonicModel(client, new() { ModelId = "nova", MaxTokens = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockNovaSonicModel(client, new() { ModelId = "nova", Temperature = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockNovaSonicModel(client, new() { ModelId = "nova", TopP = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockNovaSonicModel(client, new() { ModelId = "nova", Connection = new() { RestartAfter = TimeSpan.FromMinutes(8) } }));
        var model = new BedrockNovaSonicModel(client, new() { ModelId = "nova" });
        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(options => options with { MaxTokens = 0 })); Assert.Equal(1024, model.GetConfig().MaxTokens); Assert.Equal(0, client.InvokeCount);
    }

    [Fact]
    public async Task ExplicitCredentialsRefreshEachConnectionAndOwnTheCreatedOfficialClients()
    {
        using var credentials = new RotatingCredentials();
        var first = new NovaWire(); var second = new NovaWire();
        var clients = new List<DuplexClient>(); var snapshots = new List<ImmutableCredentials>();
        var model = new BedrockNovaSonicModel(credentials, RegionEndpoint.USEast1, new() { ModelId = "nova" }, (snapshot, region) =>
        {
            Assert.Same(RegionEndpoint.USEast1, region); snapshots.Add(snapshot);
            var client = new DuplexClient(clients.Count == 0 ? first : second); clients.Add(client); return client;
        });
        Exception? primary = null;
        try
        {
            var startFailure = await Record.ExceptionAsync(() => model.StartAsync());
            Assert.Single(clients); // An inert explicit-credentials path cannot satisfy this feature.
            Assert.Null(startFailure); Assert.Equal(1, clients[0].InvokeCount); Assert.False(clients[0].Disposed);
            Assert.Equal("access-1", snapshots[0].AccessKey); Assert.Equal("session-1", snapshots[0].Token);
            await model.RestartAsync(new());
            Assert.Equal(2, clients.Count); Assert.Equal(2, credentials.Reads);
            Assert.Equal("access-2", snapshots[1].AccessKey); Assert.Equal("session-2", snapshots[1].Token);
            Assert.True(clients[0].Disposed); Assert.True(first.Disposed); Assert.False(clients[1].Disposed);
            await model.DisposeAsync();
            Assert.True(clients[1].Disposed); Assert.True(second.Disposed); Assert.False(credentials.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            first.Complete(); second.Complete();
            await Cleanup(primary, () => model.DisposeAsync().AsTask(), async () => { foreach (var client in clients) await client.JoinAsync(); });
            foreach (var client in clients) client.Dispose(); first.Dispose(); second.Dispose();
        }
    }

    [Fact]
    public async Task CredentialRefreshFailureLeavesTheHealthyConnectionAndItsCapturedSettingsUsable()
    {
        var refreshFailure = new InvalidOperationException("fixture credential refresh failed");
        using var credentials = new RotatingCredentials { SecondReadFailure = refreshFailure };
        var wire = new NovaWire(); var clients = new List<DuplexClient>();
        var model = new BedrockNovaSonicModel(credentials, RegionEndpoint.USEast1, new() { ModelId = "nova-old", OutputSampleRate = 24000 }, (_, _) =>
        {
            var client = new DuplexClient(wire); clients.Add(client); return client;
        });
        Exception? primary = null;
        try
        {
            await model.StartAsync();
            model.UpdateConfig(options => options with { ModelId = "nova-next", OutputSampleRate = 16000 });
            var error = await Record.ExceptionAsync(() => model.RestartAsync(new()));
            Assert.Same(refreshFailure, error); Assert.Equal(2, credentials.Reads);
            Assert.Single(clients); Assert.Equal(1, clients[0].InvokeCount);
            Assert.False(clients[0].Disposed); Assert.False(wire.Disposed);
            Assert.Equal("nova-old", model.Metadata.ModelId); Assert.Equal(24000, model.Audio.Output.SampleRate);
            await model.SendAsync(new BidiMessage([new TextBlock("after-rejected-batch")]));
            await Within(clients[0].BarrierSent.Task);
            Assert.False(credentials.Disposed);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            wire.Complete();
            await Cleanup(primary, () => model.DisposeAsync().AsTask(), async () => { foreach (var client in clients) await client.JoinAsync(); });
            foreach (var client in clients) client.Dispose(); wire.Dispose();
        }
    }

    [Fact]
    public async Task OwnedClientOpenFailureJoinsThePublisherAndPreservesBorrowedCredentialsAndPrimaryError()
    {
        var openFailure = new InvalidOperationException("fixture official open failed");
        using var credentials = new RotatingCredentials();
        var wire = new NovaWire(); DuplexClient? client = null;
        var model = new BedrockNovaSonicModel(credentials, RegionEndpoint.USEast1, new() { ModelId = "nova" }, (_, _) =>
            client = new DuplexClient(wire) { ResponseGate = Task.FromException(openFailure) });
        Exception? primary = null;
        try
        {
            var error = await Record.ExceptionAsync(() => model.StartAsync());
            Assert.Same(openFailure, error); Assert.NotNull(client);
            Assert.Equal(1, client.InvokeCount);
            Assert.True(client.Disposed);
            await client.JoinAsync();
            Assert.True(client.InputEnded);
            Assert.False(credentials.Disposed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => model.SendAsync(new BidiMessage([new TextBlock("must-not-send")])));
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            wire.Complete();
            await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => client?.JoinAsync() ?? Task.CompletedTask);
            client?.Dispose(); wire.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationStopsWaitingForBorrowedCredentialRefreshWithoutClosingAHealthyConnection(bool restarting)
    {
        var lateFailure = new InvalidOperationException("fixture deferred credential failure");
        using var credentials = new HeldCredentials(restarting ? 2 : 1);
        using var caller = new CancellationTokenSource();
        var wire = new NovaWire(); var clients = new List<DuplexClient>();
        var model = new BedrockNovaSonicModel(credentials, RegionEndpoint.USEast1, new() { ModelId = "nova-old" }, (_, _) =>
        {
            var client = new DuplexClient(wire); clients.Add(client); return client;
        });
        Task? preparing = null; Exception? primary = null;
        try
        {
            if (restarting)
            {
                await model.StartAsync();
                model.UpdateConfig(options => options with { ModelId = "nova-next" });
            }
            preparing = restarting ? model.RestartAsync(new(), caller.Token) : model.StartAsync(cancellationToken: caller.Token);
            await Within(credentials.Entered.Task);
            caller.Cancel();
            var error = await Within(Record.ExceptionAsync(() => preparing));
            var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(caller.Token, canceled.CancellationToken);
            Assert.False(credentials.Pending.Task.IsCompleted); // The borrowed/shared refresh itself was not canceled.
            Assert.False(credentials.Disposed);
            if (restarting)
            {
                var client = Assert.Single(clients);
                Assert.False(client.Disposed); Assert.False(wire.Disposed); Assert.Equal(1, client.InvokeCount);
                Assert.Equal("nova-old", model.Metadata.ModelId);
                await model.SendAsync(new BidiMessage([new TextBlock("after-rejected-batch")]));
                await Within(client.BarrierSent.Task);
            }
            else Assert.Empty(clients);
            credentials.Pending.SetException(lateFailure);
            Assert.Same(lateFailure, await Record.ExceptionAsync(() => credentials.Pending.Task));
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            caller.Cancel(); credentials.Pending.TrySetException(lateFailure); wire.Complete();
            await Cleanup(primary, () => model.DisposeAsync().AsTask(), () => Join(preparing, caller.Token),
                async () => { foreach (var client in clients) await client.JoinAsync(); },
                async () => { try { await credentials.Pending.Task; } catch (Exception error) when (ReferenceEquals(error, lateFailure)) { } });
            foreach (var client in clients) client.Dispose(); wire.Dispose();
        }
    }

    private sealed class HeldCredentials(int holdOnRead) : AWSCredentials, IDisposable
    {
        private int _reads;
        internal bool Disposed;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ImmutableCredentials> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ImmutableCredentials GetCredentials() => throw new InvalidOperationException("The async credential path is required.");
        public override Task<ImmutableCredentials> GetCredentialsAsync()
        {
            if (Interlocked.Increment(ref _reads) == holdOnRead) { Entered.TrySetResult(); return Pending.Task; }
            return Task.FromResult(new ImmutableCredentials("fixture-access", "fixture-secret", "fixture-session"));
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class RotatingCredentials : AWSCredentials, IDisposable
    {
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }
        internal Exception? SecondReadFailure { get; init; }
        public override ImmutableCredentials GetCredentials()
        {
            Reads++;
            if (Reads == 2 && SecondReadFailure is not null) throw SecondReadFailure;
            return new("access-" + Reads, "fixture-secret", "session-" + Reads);
        }
        public void Dispose() => Disposed = true;
    }

    private static JsonElement AssertClosedContentGroup(IReadOnlyList<JsonElement> sent, string name, string role, string type)
    {
        var start = Assert.Single(sent, value => value.TryGetProperty("contentStart", out var block) && block.GetProperty("contentName").GetString() == name).GetProperty("contentStart");
        Assert.Equal(role, start.GetProperty("role").GetString());
        Assert.Equal(type, start.GetProperty("type").GetString());
        Assert.Single(sent, value => value.TryGetProperty("contentEnd", out var block) && block.GetProperty("contentName").GetString() == name);
        return start;
    }

    private static async Task<List<IAgentStreamEvent>> CollectUntil(IAsyncEnumerable<IAgentStreamEvent> source, Func<IAgentStreamEvent, bool> complete)
    {
        var values = new List<IAgentStreamEvent>();
        await foreach (var value in source)
        {
            values.Add(value);
            if (complete(value)) break;
        }
        return values;
    }

    private static async Task Within(Task operation)
    {
        using var stop = new CancellationTokenSource();
        var deadline = Task.Delay(TimeSpan.FromSeconds(8), stop.Token);
        try
        {
            if (await Task.WhenAny(operation, deadline) != operation) throw new TimeoutException("The controlled Nova feature operation did not reach its observation boundary.");
            await operation;
        }
        finally
        {
            stop.Cancel();
            try { await deadline; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task<T> Within<T>(Task<T> operation) { await Within((Task)operation); return await operation; }

    private static async Task Join(Task? operation, CancellationToken token)
    {
        if (operation is null) return;
        try { await operation; } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task Cleanup(Exception? primary, params Func<Task>[] steps)
    {
        var errors = new List<Exception>();
        foreach (var step in steps) try { await step(); } catch (Exception error) { if (!ReferenceEquals(error, primary)) errors.Add(error); }
        if (errors.Count == 0) return;
        if (primary is not null) primary.Data["Strands.TestCleanupErrors"] = errors;
        else
        {
            if (errors.Count > 1) errors[0].Data["Strands.TestCleanupErrors"] = errors.Skip(1).ToArray();
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
    }

    private sealed class DuplexClient(NovaWire output) : AmazonBedrockRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        private readonly object _gate = new();
        private readonly List<JsonElement> _sent = [];
        private readonly List<Task> _publishers = [];
        private readonly Dictionary<int, List<JsonElement>> _perInvocation = [];
        public int InvokeCount { get; private set; }
        public bool InitializationBeforeResponse { get; private set; }
        public bool InputEnded { get; private set; }
        public bool Disposed { get; private set; }
        public InvokeModelWithBidirectionalStreamRequest? Request { get; private set; }
        public TaskCompletionSource AudioSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ToolResultSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResponseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? ResponseGate { get; init; }
        public Task? PublisherGate { get; init; }
        public Task? UserStartGate { get; init; }
        public TaskCompletionSource UserStartObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PublisherBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BarrierSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NovaWire? NextOutput { get; set; }
        public NovaWire? NextOutputAfterFirst { get; init; }
        public IReadOnlyList<JsonElement> SentForInvocation(int invocation) { lock (_gate) return _perInvocation.TryGetValue(invocation, out var values) ? values.ToArray() : []; }
        public IReadOnlyList<JsonElement> Sent { get { lock (_gate) return _sent.ToArray(); } }

        public override async Task<InvokeModelWithBidirectionalStreamResponse> InvokeModelWithBidirectionalStreamAsync(InvokeModelWithBidirectionalStreamRequest request, CancellationToken cancellationToken = default)
        {
            InvokeCount++;
            var invocation = InvokeCount;
            lock (_gate) _perInvocation.Add(invocation, []);
            Request = request;
            var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publisher = Capture(request, initialized, invocation);
            lock (_gate) _publishers.Add(publisher);
            await initialized.Task.WaitAsync(cancellationToken);
            ResponseEntered.TrySetResult();
            if (ResponseGate is not null) await ResponseGate.WaitAsync(cancellationToken);
            InitializationBeforeResponse = true;
            var stream = invocation == 1 ? output : NextOutput ?? NextOutputAfterFirst ?? output;
            return new() { Body = new InvokeModelWithBidirectionalStreamOutput(stream) };
        }

        private async Task Capture(InvokeModelWithBidirectionalStreamRequest request, TaskCompletionSource initialized, int invocation)
        {
            string? systemContent = null, toolResultContent = null;
            try
            {
                while (true)
                {
                    if (initialized.Task.IsCompleted && PublisherGate is not null) { PublisherBlocked.TrySetResult(); await PublisherGate; }
                    var item = await request.BodyPublisher();
                    if (item is null) { InputEnded = true; break; }
                    var part = Assert.IsType<BidirectionalInputPayloadPart>(item);
                    using var json = JsonDocument.Parse(part.Bytes.ToArray());
                    var value = json.RootElement.GetProperty("event").Clone();
                    lock (_gate) { _sent.Add(value); _perInvocation[invocation].Add(value); }
                    if (value.TryGetProperty("contentStart", out var start) && start.GetProperty("role").GetString() == "SYSTEM")
                        systemContent = start.GetProperty("contentName").GetString();
                    if (value.TryGetProperty("contentEnd", out var end) && systemContent is not null && end.GetProperty("contentName").GetString() == systemContent)
                        initialized.TrySetResult();
                    if (value.TryGetProperty("audioInput", out _)) AudioSent.TrySetResult();
                    if (value.TryGetProperty("textInput", out var barrier) && barrier.GetProperty("content").GetString() == "after-rejected-batch") BarrierSent.TrySetResult();
                    if (value.TryGetProperty("toolResult", out var result)) toolResultContent = result.GetProperty("contentName").GetString();
                    if (value.TryGetProperty("contentEnd", out var closed) && toolResultContent is not null && closed.GetProperty("contentName").GetString() == toolResultContent)
                        ToolResultSent.TrySetResult();
                    if (value.TryGetProperty("contentStart", out var userStart) && userStart.GetProperty("role").GetString() == "USER" && userStart.GetProperty("type").GetString() == "TEXT" && UserStartGate is not null)
                    { UserStartObserved.TrySetResult(); await UserStartGate; }
                }
            }
            catch (Exception error) { initialized.TrySetException(error); throw; }
        }

        public async Task JoinAsync()
        {
            Task[] tasks; lock (_gate) tasks = _publishers.ToArray(); Exception? primary = null;
            foreach (var task in tasks) try { await task; } catch (OperationCanceledException) { } catch (Exception error) { primary ??= error; }
            if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class NovaWire : Stream
    {
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true, SingleWriter = true });
        private byte[]? _current;
        private int _offset;
        public bool Disposed { get; private set; }

        public void Event(string name, object data)
        {
            var nova = JsonSerializer.SerializeToUtf8Bytes(new { @event = new Dictionary<string, object> { [name] = data } });
            var headers = new[] { new EventStreamHeader(":message-type"), new EventStreamHeader(":event-type"), new EventStreamHeader(":content-type") };
            headers[0].SetString("event"); headers[1].SetString("chunk"); headers[2].SetString("application/json");
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { bytes = Convert.ToBase64String(nova) });
            Assert.True(_frames.Writer.TryWrite(new EventStreamMessage(headers.Cast<IEventStreamHeader>().ToList(), payload).ToByteArray()));
        }

        public void Timeout()
        {
            var kind = new EventStreamHeader(":message-type"); kind.SetString("exception");
            var type = new EventStreamHeader(":exception-type"); type.SetString("modelTimeoutException");
            Assert.True(_frames.Writer.TryWrite(new EventStreamMessage(new List<IEventStreamHeader> { kind, type }, JsonSerializer.SerializeToUtf8Bytes(new { message = "Model has timed out in processing the request" })).ToByteArray()));
        }

        public void AssistantResponse(string prompt, string text, byte[] audio)
        {
            Event("completionStart", new { sessionId = "session", promptName = prompt, completionId = "completion" });
            Event("contentStart", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "spoken-text", type = "TEXT", role = "ASSISTANT", additionalModelFields = "{\"generationStage\":\"SPECULATIVE\"}" });
            Event("textOutput", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "spoken-text", role = "ASSISTANT", content = text });
            Event("contentEnd", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "spoken-text", type = "TEXT", stopReason = "PARTIAL_TURN" });
            Event("contentStart", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "final-text", type = "TEXT", role = "ASSISTANT", additionalModelFields = "{\"generationStage\":\"FINAL\"}" });
            Event("textOutput", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "final-text", role = "ASSISTANT", content = text });
            Event("contentEnd", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "final-text", type = "TEXT", stopReason = "PARTIAL_TURN" });
            Event("contentStart", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "spoken-audio", type = "AUDIO", role = "ASSISTANT", audioOutputConfiguration = new { mediaType = "audio/lpcm", sampleRateHertz = 24000, sampleSizeBits = 16, channelCount = 1, encoding = "base64" } });
            Event("audioOutput", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "spoken-audio", content = Convert.ToBase64String(audio) });
            Event("contentEnd", new { sessionId = "session", promptName = prompt, completionId = "completion", contentId = "spoken-audio", type = "AUDIO", stopReason = "END_TURN" });
        }

        public void Complete() => _frames.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            while (_current is null || _offset == _current.Length)
            {
                if (!await _frames.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_frames.Reader.TryRead(out _current)) continue;
                _offset = 0;
            }
            var current = _current;
            var count = Math.Min(11, Math.Min(buffer.Length, current.Length - _offset));
            current.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { Disposed = true; Complete(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NoOrdinaryModelCalls : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The ordinary host model must not be invoked by the Nova tool conversation.");
    }
}
