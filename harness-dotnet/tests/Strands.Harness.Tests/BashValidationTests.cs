using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed partial class BashTests
{
    [Theory]
    [InlineData("missing-mode", "{}")]
    [InlineData("unknown-mode", "{\"mode\":\"other\"}")]
    [InlineData("null-mode", "{\"mode\":null}")]
    [InlineData("nonobject", "[]")]
    [InlineData("missing-command", "{\"mode\":\"execute\"}")]
    [InlineData("empty-command", "{\"mode\":\"execute\",\"command\":\"\"}")]
    [InlineData("numeric-command", "{\"mode\":\"execute\",\"command\":3}")]
    [InlineData("nul-command", "{\"mode\":\"execute\",\"command\":\"a\\u0000b\"}")]
    [InlineData("zero-timeout", "{\"mode\":\"execute\",\"command\":\":\",\"timeout\":0}")]
    [InlineData("negative-timeout", "{\"mode\":\"restart\",\"timeout\":-1}")]
    [InlineData("string-timeout", "{\"mode\":\"execute\",\"command\":\":\",\"timeout\":\"1\"}")]
    [InlineData("infinite-timeout", "{\"mode\":\"execute\",\"command\":\":\",\"timeout\":1e309}")]
    [InlineData("timer-overflow", "{\"mode\":\"execute\",\"command\":\":\",\"timeout\":2147484}")]
    public async Task InvalidInputsRejectBeforeInterpreterAdmission(string caseName, string json)
    {
        Assert.False(string.IsNullOrWhiteSpace(caseName));
        await using var tool = Bash.Create(new() { ExecutablePath = Path.Combine(Path.GetTempPath(), "never-created-" + Guid.NewGuid().ToString("N")) });
        await using var agent = NewAgent();
        using var document = JsonDocument.Parse(json);
        var input = document.RootElement;
        var error = await Assert.ThrowsAnyAsync<ArgumentException>(async () => await tool.InvokeResultAsync(input, new(agent, new("invalid", tool.Spec.Name, input), new Dictionary<string, object?>())));
        Assert.DoesNotContain("interpreter", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultsAndSchemaExposeOnlyThePersistentCommandContract()
    {
        var defaults = new BashConfig();
        Assert.Equal("bash", defaults.Name);
        Assert.Equal(1_048_576, defaults.MaximumOutputBytes);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.CleanupTimeout);
        Assert.Null(defaults.ExecutablePath);
        Assert.Null(defaults.WorkingDirectory);
        Assert.Empty(defaults.Environment);
        await using var tool = Bash.Create(new() { Name = "terminal", Description = "application description" });
        Assert.Equal("terminal", tool.Spec.Name);
        Assert.Equal("application description", tool.Spec.Description);
        var schema = tool.Spec.InputSchema;
        Assert.Equal(new[] { "mode" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { "execute", "restart" }, schema.GetProperty("properties").GetProperty("mode").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void InvalidConfigurationFailsWithoutStartingAnInterpreter()
    {
        Assert.ThrowsAny<ArgumentException>(() => Bash.Create(new() { ExecutablePath = "relative/bash" }));
        Assert.ThrowsAny<ArgumentException>(() => Bash.Create(new() { WorkingDirectory = "relative" }));
        Assert.ThrowsAny<ArgumentException>(() => Bash.Create(new() { MaximumOutputBytes = 0 }));
        Assert.ThrowsAny<ArgumentException>(() => Bash.Create(new() { CleanupTimeout = TimeSpan.Zero }));
        Assert.ThrowsAny<ArgumentException>(() => Bash.Create(new() { CleanupTimeout = TimeSpan.FromDays(100) }));
        Assert.ThrowsAny<ArgumentException>(() => Bash.Create(new() { Environment = new Dictionary<string, string> { ["bad=name"] = "value" } }));
    }

    [Fact]
    public async Task EnvironmentIsDetachedAndInitialDirectoryAndPromptOverridesAreApplied()
    {
        var environment = new Dictionary<string, string> { ["STRANDS_DETACHED_BASH"] = "before 雪", ["PS1"] = "prompt-one", ["PS2"] = "prompt-two" };
        await WithFixture(async fixture =>
        {
            environment["STRANDS_DETACHED_BASH"] = "after";
            Assert.Equal("before 雪||", await fixture.Run("builtin printf '%s|%s|%s' \"$STRANDS_DETACHED_BASH\" \"$PS1\" \"$PS2\""));
            Assert.Equal(Path.GetFileName(fixture.Root), await fixture.Run("builtin printf '%s' \"${PWD##*/}\""));
        }, root => new() { ExecutablePath = Environment.GetEnvironmentVariable("STRANDS_BASH_TEST_EXECUTABLE"), WorkingDirectory = root, Environment = environment });
    }

    [Fact]
    public async Task PreCanceledCallerPreservesItsTokenAndNeverStarts()
    {
        await using var tool = Bash.Create(new() { ExecutablePath = Path.Combine(Path.GetTempPath(), "missing-bash-" + Guid.NewGuid().ToString("N")) });
        await using var agent = NewAgent();
        using var source = new CancellationTokenSource(); source.Cancel();
        var input = JsonSerializer.SerializeToElement(new { mode = "execute", command = ":" });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tool.InvokeResultAsync(input, new(agent, new("cancel", tool.Spec.Name, input), new Dictionary<string, object?>()), source.Token));
        Assert.Equal(source.Token, error.CancellationToken);
        _ = source.Token; // The borrowed source remains application-owned.
    }

    [Fact]
    public async Task PreCanceledContextPreservesItsTokenAndNeverStarts()
    {
        await using var tool = Bash.Create(new() { ExecutablePath = Path.Combine(Path.GetTempPath(), "missing-bash-" + Guid.NewGuid().ToString("N")) });
        await using var agent = new Agent(new() { Model = new EmptyModel(), IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel();
        var input = JsonSerializer.SerializeToElement(new { mode = "execute", command = ":" });
        var context = new ToolContext(agent, new("context-cancel", tool.Spec.Name, input), new Dictionary<string, object?>());
        Assert.True(context.CancelSignal.IsCancellationRequested);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tool.InvokeResultAsync(input, context));
        Assert.Equal(context.CancelSignal, error.CancellationToken);
    }

    [Fact]
    public async Task AContextAndAgentAreRequiredBeforeAnyStart()
    {
        await using var tool = Bash.Create();
        var input = JsonSerializer.SerializeToElement(new { mode = "execute", command = ":" });
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await tool.InvokeResultAsync(input, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await tool.InvokeResultAsync(input, new(null!, new("missing-agent", tool.Spec.Name, input), new Dictionary<string, object?>())));
    }
}
