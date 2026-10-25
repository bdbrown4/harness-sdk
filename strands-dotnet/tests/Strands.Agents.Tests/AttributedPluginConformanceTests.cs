using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AttributedPluginConformanceTests
{
    [Theory]
    [InlineData("task")]
    [InlineData("value-task")]
    public async Task BothAsyncHookFormsAwaitTheExactEventAndToken(string form)
    {
        GatedTarget target = form == "task" ? new TaskTarget() : new ValueTaskTarget();
        await using var agent = Create(target);
        await agent.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var value = new BeforeInvocationEvent(agent, new Dictionary<string, object?>());
        var dispatch = agent.Hooks.InvokeAsync(value, cancellation.Token).AsTask();
        try
        {
            await Task.WhenAny(dispatch, target.Entered.Task);
            Assert.False(dispatch.IsCompleted);
            Assert.Same(value, target.Event);
            Assert.Equal(cancellation.Token, target.Token);
        }
        finally
        {
            target.Release.TrySetResult(true);
            await dispatch;
        }
        Assert.True(target.Completed);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("task")]
    [InlineData("value-task")]
    public async Task HookFailuresRetainTheOriginalExceptionAndUserStack(string form)
    {
        ThrowingTarget target = form switch { "sync" => new SyncFailure(), "task" => new TaskFailure(), _ => new ValueTaskFailure() };
        await using var agent = Create(target);
        await agent.InitializeAsync();
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.Hooks.InvokeAsync(new BeforeInvocationEvent(agent, new Dictionary<string, object?>())).AsTask());
        Assert.Same(target.Failure, observed);
        Assert.Contains("Fail", observed.StackTrace);
    }

    [Theory]
    [InlineData("wrong-event")]
    [InlineData("extra-argument")]
    [InlineData("wrong-token")]
    [InlineData("return-value")]
    [InlineData("task-result")]
    [InlineData("async-void")]
    [InlineData("generic")]
    [InlineData("by-reference")]
    [InlineData("overload")]
    [InlineData("dual-role")]
    [InlineData("duplicate-tools")]
    public void InvalidDeclarationsFailBeforeAnyCallbackOrInitialization(string shape)
    {
        InvalidTarget target = shape switch
        {
            "wrong-event" => new WrongEvent(),
            "extra-argument" => new ExtraArgument(),
            "wrong-token" => new WrongToken(),
            "return-value" => new ReturnValue(),
            "task-result" => new TaskResult(),
            "async-void" => new AsyncVoid(),
            "generic" => new GenericHook(),
            "by-reference" => new ByReference(),
            "overload" => new Overloaded(),
            "dual-role" => new DualRole(),
            _ => new DuplicateTools()
        };
        var initialized = 0;
        Assert.Throws<ArgumentException>(() => PluginFactory.FromObject(target, "invalid", (_, _) => { initialized++; return default; }));
        Assert.Equal(0, initialized);
        Assert.Equal(0, target.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void MissingNamesAreRejected(string? name)
        => Assert.ThrowsAny<ArgumentException>(() => PluginFactory.FromObject(new object(), name!));

    [Fact]
    public void NullTargetsAreRejected()
        => Assert.Throws<ArgumentNullException>(() => PluginFactory.FromObject(null!, "named"));

    [Fact]
    public async Task BaseEventInterfacesAndSingleArgumentHooksObserveTheAcceptedEvent()
    {
        var target = new BaseEventsTarget();
        await using var agent = Create(target);
        await agent.InitializeAsync();
        Assert.IsType<InitializedEvent>(Assert.Single(target.Events));
        var value = new BeforeInvocationEvent(agent, new Dictionary<string, object?>());
        await agent.Hooks.InvokeAsync(value);
        Assert.Same(value, target.SingleArgument);
        Assert.Same(value, target.Events.Last());
    }

    [Fact]
    public async Task PriorityAndAfterHookReverseOrderUseTheExistingRegistryPolicy()
    {
        var target = new OrderedTarget();
        await using var agent = Create(target);
        await agent.InitializeAsync();
        await agent.Hooks.InvokeAsync(new AfterInvocationEvent(agent, null, null));
        Assert.Equal(new[] { "zero-second", "zero-first", "one" }, target.Order);
    }

    [Fact]
    public async Task SharedDeclarationsBindSeparatelyAndDisposalKeepsSiblingAndApplicationHooks()
    {
        var parent = new HookRegistry();
        var application = 0;
        using var registration = parent.Register<BeforeInvocationEvent>((_, _) => { application++; return default; });
        var plugin = PluginFactory.FromObject(new StateTarget(), "shared");
        var first = new Agent(new() { Model = new AttributedPluginTestModel(), Hooks = parent, Plugins = [plugin] });
        await using var second = new Agent(new() { Model = new AttributedPluginTestModel(), Hooks = parent, Plugins = [plugin] });
        await first.InitializeAsync();
        await second.InitializeAsync();
        await first.Hooks.InvokeAsync(new BeforeInvocationEvent(first, new Dictionary<string, object?>()));
        Assert.Equal(1, first.State.Get<int>("calls"));
        Assert.Equal(0, second.State.Get<int>("calls"));
        await first.DisposeAsync();
        await second.Hooks.InvokeAsync(new BeforeInvocationEvent(second, new Dictionary<string, object?>()));
        Assert.Equal(1, second.State.Get<int>("calls"));
        Assert.Equal(2, application);
    }

    [Fact]
    public async Task AttributedToolsUseOrdinaryArgumentBindingAndBorrowTheInitializedTarget()
    {
        var target = new ToolTarget();
        var plugin = PluginFactory.FromObject(target, "tools", (_, _) => { target.Prefix = "accepted:"; return default; });
        await using var agent = new Agent(new() { Model = new AttributedPluginTestModel(), Plugins = [plugin] });
        var result = await agent.InvokeToolAsync(new("call", "plugin_echo", JsonSerializer.SerializeToElement(new { value = "literal" })));
        Assert.Equal("accepted:literal", result.Content);
        Assert.Same(agent, target.Agent);
        Assert.Single(plugin.GetTools());
        Assert.Throws<NotSupportedException>(() => ((IList<ITool>)plugin.GetTools()).Clear());
    }

    [Fact]
    public void ToolDeclarationOrderUsesParentSlotsAndChildBoundOverrides()
    {
        var plugin = PluginFactory.FromObject(new ChildTools(), "ordered-tools");
        Assert.Equal(new[] { "base-first", "child-replacement", "child-last" }, plugin.GetTools().Select(tool => tool.Spec.Name));
    }

    [Fact]
    public async Task PrivateAndStaticMethodsRemainOutsideInstanceDiscovery()
    {
        var target = new IgnoredTarget();
        await using var agent = Create(target);
        await agent.InitializeAsync();
        Assert.Equal(0, target.Calls);
        Assert.Equal(0, IgnoredTarget.StaticCalls);
        Assert.Empty(agent.Tools);
    }

    private static Agent Create(object target) => new(new() { Model = new AttributedPluginTestModel(), Plugins = [PluginFactory.FromObject(target, "test")] });

    private abstract class GatedTarget
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BeforeInvocationEvent? Event { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool Completed { get; private set; }
        protected async Task Observe(BeforeInvocationEvent value, CancellationToken token)
        { Event = value; Token = token; Entered.TrySetResult(true); await Release.Task; Completed = true; }
    }
    private sealed class TaskTarget : GatedTarget
    { [Hook] public Task Hook(BeforeInvocationEvent value, CancellationToken token) => Observe(value, token); }
    private sealed class ValueTaskTarget : GatedTarget
    { [Hook] public ValueTask Hook(BeforeInvocationEvent value, CancellationToken token) => new(Observe(value, token)); }

    private abstract class ThrowingTarget { public InvalidOperationException Failure { get; } = new("user-hook-failure"); }
    private sealed class SyncFailure : ThrowingTarget
    { [Hook] public void Fail(BeforeInvocationEvent value) => throw Failure; }
    private sealed class TaskFailure : ThrowingTarget
    { [Hook] public async Task Fail(BeforeInvocationEvent value) { await Task.Yield(); throw Failure; } }
    private sealed class ValueTaskFailure : ThrowingTarget
    { [Hook] public async ValueTask Fail(BeforeInvocationEvent value) { await Task.Yield(); throw Failure; } }

    private abstract class InvalidTarget { public int Calls { get; protected set; } }
    private sealed class WrongEvent : InvalidTarget { [Hook] public void Hook(string value) => Calls++; }
    private sealed class ExtraArgument : InvalidTarget { [Hook] public void Hook(InitializedEvent value, CancellationToken token, int extra) => Calls++; }
    private sealed class WrongToken : InvalidTarget { [Hook] public void Hook(InitializedEvent value, int token) => Calls++; }
    private sealed class ReturnValue : InvalidTarget { [Hook] public int Hook(InitializedEvent value) => ++Calls; }
    private sealed class TaskResult : InvalidTarget { [Hook] public Task<int> Hook(InitializedEvent value) => Task.FromResult(++Calls); }
    private sealed class AsyncVoid : InvalidTarget { [Hook] public async void Hook(InitializedEvent value) { await Task.Yield(); Calls++; } }
    private sealed class GenericHook : InvalidTarget { [Hook] public void Hook<T>(InitializedEvent value) => Calls++; }
    private sealed class ByReference : InvalidTarget { [Hook] public void Hook(ref InitializedEvent value) => Calls++; }
    private sealed class Overloaded : InvalidTarget
    { [Hook] public void Hook(InitializedEvent value) => Calls++; [Hook] public void Hook(BeforeInvocationEvent value) => Calls++; }
    private sealed class DualRole : InvalidTarget { [Hook, Tool("Invalid dual role.")] public void Hook(InitializedEvent value) => Calls++; }
    private sealed class DuplicateTools : InvalidTarget
    { [Tool("First.", Name = "same")] public int First() => ++Calls; [Tool("Second.", Name = "same")] public int Second() => ++Calls; }

    private sealed class BaseEventsTarget
    {
        public List<IAgentStreamEvent> Events { get; } = [];
        public BeforeInvocationEvent? SingleArgument { get; private set; }
        [Hook] public void Any(IAgentStreamEvent value, CancellationToken token) => Events.Add(value);
        [Hook] public void Single(BeforeInvocationEvent value) => SingleArgument = value;
    }
    private sealed class OrderedTarget
    {
        public List<string> Order { get; } = [];
        [Hook(Order = 1)] public void One(AfterInvocationEvent value) => Order.Add("one");
        [Hook] public void ZeroFirst(AfterInvocationEvent value) => Order.Add("zero-first");
        [Hook] public void ZeroSecond(AfterInvocationEvent value) => Order.Add("zero-second");
    }
    private sealed class StateTarget
    { [Hook] public void Count(BeforeInvocationEvent value) => value.Agent.State.Set("calls", value.Agent.State.Get<int>("calls") + 1); }
    private sealed class ToolTarget
    {
        public string Prefix { get; set; } = "before:";
        public Agent? Agent { get; private set; }
        [Tool("Echoes the accepted value.", Name = "plugin_echo")]
        public ValueTask<string> Echo(string value, ToolContext context, CancellationToken token)
        { Agent = context.Agent; return new(Prefix + value); }
    }
    private class ParentTools
    {
        [Tool("First.", Name = "base-first")] public string First() => "first";
        [Tool("Replace.", Name = "base-replacement")] public virtual string Replace() => "base";
        [Tool("Suppress.", Name = "base-suppressed")] public virtual string Suppress() => "base";
    }
    private sealed class ChildTools : ParentTools
    {
        [Tool("Replace.", Name = "child-replacement")] public override string Replace() => "child";
        public override string Suppress() => "unmarked";
        [Tool("Last.", Name = "child-last")] public string Last() => "last";
    }
    private sealed class IgnoredTarget
    {
        public int Calls { get; private set; }
        public static int StaticCalls { get; private set; }
        [Hook] private void Private(InitializedEvent value) => Calls++;
        [Hook] public static void Static(InitializedEvent value) => StaticCalls++;
        [Tool("Hidden.")] private string Hidden() { Calls++; return "hidden"; }
        [Tool("Static.")] public static string StaticTool() { StaticCalls++; return "static"; }
    }
}
