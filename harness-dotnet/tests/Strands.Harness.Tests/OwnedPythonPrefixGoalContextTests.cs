using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents;
using Strands.Agents.Context;
using Strands.Agents.Goals;
using Xunit;

namespace Strands.Harness.Tests;

public sealed class OwnedPythonPrefixGoalContextTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicHarnessGoalRetryKeepsThePersistedPythonPrefix(bool preserveContext)
    {
        var files = new OwnedFiles();
        Agent? agent = null;
        Exception? primary = null;
        try
        {
            var setup = await PersistCompletedPrefixAsync(files.Path);
            var model = new ReplyModel("first draft", "accepted draft");
            var validations = 0;
            var goal = new GoalLoop((_, owner, _) =>
            {
                validations++;
                owner.State.Set("validation-count", validations);
                return new ValueTask<ValidationOutcome>(new ValidationOutcome(validations == 2, validations == 1 ? "retry the draft" : null));
            }, new() { MaxAttempts = 2, PreserveContext = preserveContext, ResumePromptTemplate = _ => new("retry feedback") });
            var sandbox = new NoIoSandbox();
            agent = await Harness.CreateAsync(Options(model, files.Path, sandbox) with { Plugins = [goal] });
            AssertPrefix(Assert.Single(agent.Messages), setup.Prefix);

            var result = await agent.InvokeAsync("new task");

            Assert.Equal("accepted draft", result.ToString());
            Assert.Equal(2, validations);
            Assert.Equal(2, model.Requests.Count);
            AssertPrefix(model.Requests[0].Messages[0], setup.Prefix);
            AssertPrefix(model.Requests[1].Messages[0], setup.Prefix);
            Assert.Equal("S1", model.Requests[0].ModelState!["responseId"].GetString());
            Assert.Equal(preserveContext ? "R1" : "S1", model.Requests[1].ModelState!["responseId"].GetString());
            Assert.Equal("new task", model.Requests[0].Messages.Last().Text);
            Assert.Equal("retry feedback", model.Requests[1].Messages.Last().Text);
            Assert.Equal(preserveContext ? 4 : 3, model.Requests[1].Messages.Count);
            if (preserveContext)
                Assert.Equal("first draft", model.Requests[1].Messages[2].Text);
            else
                Assert.DoesNotContain(model.Requests[1].Messages, message => message.Text == "first draft");
            Assert.Equal("new task", model.Requests[1].Messages[1].Text);
            var completed = Assert.IsType<GoalResult>(goal.LastResult(agent));
            Assert.True(completed.Passed);
            Assert.Equal(GoalStopReason.Satisfied, completed.StopReason);
            Assert.Collection(completed.Attempts, first => Assert.False(first.Passed), second => Assert.True(second.Passed));
            Assert.Equal(2, agent.State.Get<int>("validation-count"));
            Assert.Equal(1, setup.Effects.Completed);
            Assert.Equal(2, setup.Effects.ApprovalEntries);
            Assert.Equal(0, sandbox.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeAll(primary, agent, files); }
    }

    [Fact]
    public async Task PublicHarnessContextStrategyCommitsReductionWhileRetainingTheOwnedPrefix()
    {
        var files = new OwnedFiles();
        Agent? agent = null;
        Exception? primary = null;
        try
        {
            var setup = await PersistCompletedPrefixAsync(files.Path);
            var strategy = new PrefixStrategy(addOrphan: false);
            var model = new ReplyModel("accepted");
            var sandbox = new NoIoSandbox();
            agent = await Harness.CreateAsync(Options(model, files.Path, sandbox) with
            {
                Context = new() { Manager = new(new() { Strategies = [strategy], StashEnabled = false }) }
            });
            AssertPrefix(Assert.Single(agent.Messages), setup.Prefix);

            Assert.Equal("accepted", (await agent.InvokeAsync(new string('x', 200))).ToString());

            Assert.Equal(1, strategy.Calls);
            var request = Assert.Single(model.Requests);
            Assert.Equal(2, request.Messages.Count);
            AssertPrefix(request.Messages[0], setup.Prefix);
            Assert.Equal("reduced prompt", request.Messages[1].Text);
            Assert.Equal("reduced prompt", agent.Messages[1].Text);
            Assert.Equal("S1", request.ModelState!["responseId"].GetString());
            Assert.Equal(1, setup.Effects.Completed);
            Assert.Equal(2, setup.Effects.ApprovalEntries);
            Assert.Equal(0, sandbox.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeAll(primary, agent, files); }
    }

    [Fact]
    public async Task PublicHarnessContextStrategyRollsBackANewUnmatchedToolResult()
    {
        var files = new OwnedFiles();
        Agent? agent = null;
        Exception? primary = null;
        try
        {
            var setup = await PersistCompletedPrefixAsync(files.Path);
            var strategy = new PrefixStrategy(addOrphan: true);
            var model = new ReplyModel("accepted");
            var sandbox = new NoIoSandbox();
            agent = await Harness.CreateAsync(Options(model, files.Path, sandbox) with
            {
                Context = new() { Manager = new(new() { Strategies = [strategy], StashEnabled = false }) }
            });

            Assert.Equal("accepted", (await agent.InvokeAsync("original prompt")).ToString());

            Assert.Equal(1, strategy.Calls);
            var request = Assert.Single(model.Requests);
            Assert.Equal(2, request.Messages.Count);
            AssertPrefix(request.Messages[0], setup.Prefix);
            Assert.Equal("original prompt", request.Messages[1].Text);
            Assert.DoesNotContain(request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.ToolUseId == "new-orphan");
            Assert.DoesNotContain(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.ToolUseId == "new-orphan");
            AssertPrefix(agent.Messages[0], setup.Prefix);
            Assert.Equal(1, setup.Effects.Completed);
            Assert.Equal(2, setup.Effects.ApprovalEntries);
            Assert.Equal(0, sandbox.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeAll(primary, agent, files); }
    }

    [Fact]
    public async Task PublicHarnessRejectsANewOrphanInputBeforeContextOrProvider()
    {
        var files = new OwnedFiles();
        Agent? agent = null;
        Exception? primary = null;
        try
        {
            var setup = await PersistCompletedPrefixAsync(files.Path);
            var strategy = new PrefixStrategy(addOrphan: false);
            var model = new ReplyModel("must not execute");
            var sandbox = new NoIoSandbox();
            agent = await Harness.CreateAsync(Options(model, files.Path, sandbox) with
            {
                Context = new() { Manager = new(new() { Strategies = [strategy], StashEnabled = false }) }
            });
            var before = JsonSerializer.Serialize(agent.Messages, Web);
            var beforeModel = JsonSerializer.Serialize(agent.ModelState.GetAll(), Web);

            var error = await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeContentAsync(Orphan().Content));
            Assert.Equal("content", error.ParamName);

            Assert.Equal(0, strategy.Calls);
            Assert.Empty(model.Requests);
            Assert.Equal(before, JsonSerializer.Serialize(agent.Messages, Web));
            Assert.Equal(beforeModel, JsonSerializer.Serialize(agent.ModelState.GetAll(), Web));
            AssertPrefix(Assert.Single(agent.Messages), setup.Prefix);
            Assert.Equal(1, setup.Effects.Completed);
            Assert.Equal(2, setup.Effects.ApprovalEntries);
            Assert.Equal(0, sandbox.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeAll(primary, agent, files); }
    }

    private static HarnessOptions Options(IModel model, string directory, NoIoSandbox sandbox) => new()
    {
        Model = model,
        Sandbox = sandbox,
        UseEnvironmentTelemetry = false,
        BuiltinTools = [],
        BuiltinPlugins = [],
        BackgroundTasks = null,
        Skills = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Session = new() { Enabled = false },
        Context = new() { Enabled = false },
        AgentOptions = new()
        {
            Model = model,
            ModelHistoryBehavior = ModelHistoryBehavior.Python,
            SessionStore = new FileSessionStore(directory),
            SessionId = "completed-prefix"
        }
    };

    private static async Task<Setup> PersistCompletedPrefixAsync(string directory)
    {
        Agent? producer = null, resumed = null;
        Exception? primary = null;
        try
        {
            var effects = new Effects();
            var original = new MemoryStore();
            var completedStore = new MemoryStore();
            producer = new(new() { Model = new SeedModel(true), SessionStore = original, SessionId = "paired-producer", Tools = Tools(effects), ToolExecution = ToolExecutionStrategy.Sequential });
            Assert.Equal(StopReasons.Interrupt, (await producer.InvokeAsync("original work")).StopReason);
            var paired = Assert.IsType<SessionSnapshot>(original.Saved);
            var pending = Assert.IsType<InterruptState>(paired.InterruptState);
            Assert.Equal(1, effects.Completed);
            Assert.Equal(1, effects.ApprovalEntries);
            await producer.DisposeAsync(); producer = null;

            // Existing explicit DTO mapping, not a new source-profile first-pause producer claim.
            completedStore.Saved = Copy(paired with { Messages = [], InterruptState = pending with { AssistantMessageIsIndependent = true } });
            resumed = new(new()
            {
                Model = new SeedModel(false),
                ModelHistoryBehavior = ModelHistoryBehavior.Python,
                SessionStore = completedStore,
                SessionId = "source-resume",
                Tools = Tools(effects),
                ToolExecution = ToolExecutionStrategy.Sequential
            });
            var question = Assert.Single(pending.Interrupts);
            Assert.Equal(StopReasons.EndTurn, (await resumed.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("true"))] })).StopReason);
            var prefix = Assert.IsType<SessionSnapshot>(completedStore.Writes.LastOrDefault(snapshot =>
                snapshot.InterruptState is null && snapshot.Messages.Count == 1 && snapshot.Messages[0].Content.OfType<ToolResultBlock>().Any()));
            Assert.Equal(ModelHistoryBehavior.Python, prefix.HistoryProfile);
            Assert.Equal("S1", prefix.ModelState!["responseId"].GetString());
            Assert.Null(prefix.ModelHistoryCount);
            Assert.Null(prefix.ModelInputReceipt);
            Assert.Equal(1, effects.Completed);
            Assert.Equal(2, effects.ApprovalEntries);
            await resumed.DisposeAsync(); resumed = null;

            await new FileSessionStore(directory).SaveAsync("completed-prefix", prefix);
            var reopened = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory).LoadAsync("completed-prefix"));
            Assert.Equal(ModelHistoryBehavior.Python, reopened.HistoryProfile);
            Assert.Equal("S1", reopened.ModelState!["responseId"].GetString());
            Assert.Null(reopened.InterruptState);
            AssertPrefix(Assert.Single(reopened.Messages), Assert.Single(prefix.Messages));
            return new(Assert.Single(reopened.Messages), effects);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeAll(primary, resumed, producer); }
    }

    private static void AssertPrefix(Message actual, Message expected)
    {
        Assert.Equal(expected.TrackingId, actual.TrackingId);
        Assert.Equal(Role.User, actual.Role);
        Assert.Equal(2, actual.Content.Count);
        Assert.Collection(actual.Content.OfType<ToolResultBlock>(),
            result => { Assert.Equal("call-0", result.ToolUseId); Assert.Equal("committed", result.Content); Assert.Equal(ToolResultStatus.Success, result.Status); },
            result => { Assert.Equal("call-1", result.ToolUseId); Assert.Equal("approved", result.Content); Assert.Equal(ToolResultStatus.Success, result.Status); });
        Assert.Equal(JsonSerializer.Serialize(expected.Content, Web), JsonSerializer.Serialize(actual.Content, Web));
    }

    private static Message Orphan() => new(Role.User, [new ToolResultBlock("new-orphan", "bad")]);
    private static IReadOnlyList<ITool> Tools(Effects effects) =>
    [
        new FunctionTool(new("done", "completed sibling", Json("{\"type\":\"object\"}")), (_, _, _) =>
        { effects.Completed++; return new ValueTask<string>("committed"); }),
        new FunctionTool(new("approve", "requires approval", Json("{\"type\":\"object\"}")), (_, context, _) =>
        { effects.ApprovalEntries++; return new ValueTask<string>(context.Interrupt("approval").GetBoolean() ? "approved" : "declined"); })
    ];
    private static JsonElement Json(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
    private static SessionSnapshot Copy(SessionSnapshot value) => JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(value, Web), Web)!;
    private sealed record Setup(Message Prefix, Effects Effects);
    private sealed class Effects { internal int Completed; internal int ApprovalEntries; }

    private sealed class PrefixStrategy(bool addOrphan) : ContextStrategy
    {
        public override string Name => "owned-prefix-test";
        internal int Calls { get; private set; }
        public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            Assert.Equal(2, context.Messages.Count);
            Assert.Equal(Role.User, context.Messages[0].Role);
            Assert.Equal(2, context.Messages[0].Content.OfType<ToolResultBlock>().Count());
            if (addOrphan) context.Messages.Add(Orphan());
            else
            {
                var old = context.Messages[1];
                context.Messages[1] = new(old.Role, [new TextBlock("reduced prompt")], old.TrackingId, old.Metadata);
            }
            return new(true);
        }
    }

    private sealed class MemoryStore : ISessionStore
    {
        internal SessionSnapshot? Saved { get; set; }
        internal List<SessionSnapshot> Writes { get; } = [];
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new(Saved is null ? null : Copy(Saved)); }
        public ValueTask SaveAsync(string id, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Saved = Copy(snapshot); Writes.Add(Saved); return default; }
    }

    private sealed class SeedModel(bool pause) : IStatefulModel
    {
        public bool Stateful => !pause;
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            if (++_calls != 1) throw new InvalidOperationException("The seed model permits one finite request.");
            yield return new ModelMessageStartEvent();
            if (pause)
            {
                foreach (var pair in new[] { ("call-0", "done"), ("call-1", "approve") }.Select((value, index) => (value, index)))
                {
                    yield return new ModelContentBlockStartEvent(pair.index, new ToolUseBlock(pair.value.Item1, pair.value.Item2, Json("{}")));
                    yield return new ModelContentBlockDeltaEvent(pair.index, ToolInput: "{}");
                    yield return new ModelContentBlockStopEvent(pair.index);
                }
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
            }
            yield return new ModelMessageStopEvent(pause ? StopReasons.ToolUse : StopReasons.EndTurn)
            { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = Json("\"S1\"") } };
        }
    }

    private sealed class ReplyModel(params string[] replies) : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public ValueTask<int> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new(10); }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            if (Requests.Count >= replies.Length) throw new InvalidOperationException("The response model permits only its finite declared requests.");
            Requests.Add(request with
            {
                Messages = request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray(),
                ModelState = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            });
            var index = Requests.Count - 1;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: replies[index]);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn)
            { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = Json("\"R" + (index + 1) + "\"") } };
        }
    }

    private sealed class NoIoSandbox : ISandbox
    {
        internal int Calls { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No sandbox IO belongs to this fixture."); }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No sandbox IO belongs to this fixture."); }
    }

    private sealed class OwnedFiles : IAsyncDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "owned-python-prefix-goal-context", Guid.NewGuid().ToString("N"));
        public ValueTask DisposeAsync()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned files escaped task TEMP.");
            if (Directory.Exists(target))
            {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidOperationException("Owned files contain an unexpected reparse point.");
                Directory.Delete(target, true);
            }
            return default;
        }
    }

    private static async ValueTask DisposeAll(Exception? primary, params IAsyncDisposable?[] resources)
    {
        Exception? failure = primary;
        var index = 0;
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try { await resource.DisposeAsync(); }
            catch (Exception error)
            {
                if (failure is null) failure = error;
                else if (!ReferenceEquals(failure, error)) failure.Data["OwnedPrefix.Cleanup." + index++] = error;
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
