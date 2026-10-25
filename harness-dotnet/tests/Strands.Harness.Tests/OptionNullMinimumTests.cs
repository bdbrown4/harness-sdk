using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class OptionNullMinimumTests
{
    [Fact]
    public void FactoryAndRecordCopyContainRealNull()
    {
        var option = Option.Null("none", "Use the default.");
        Assert.Null(option.Value);
        Assert.Equal("none", option.Name);
        Assert.Equal("Use the default.", option.Description);
        var copy = option with { };
        Assert.NotSame(option, copy);
        Assert.Null(copy.Value);
        Assert.Equal(option, copy);
        Assert.Null(new Choice([option]).Options[0].Value);
    }

    [Fact]
    public void ExistingConstructorsAndImplicitNamesKeepTheirValues()
    {
        Assert.Equal("legacy", new Option("legacy").Value);
        Assert.Equal("legacy", new Option("legacy", null).Value);
        Assert.Equal("legacy", new Option("legacy", null, "description").Value);
        var value = new object();
        var explicitValue = new Option("named", value, "description");
        Assert.Same(value, explicitValue.Value);
        Assert.Equal("description", explicitValue.Description);
        Option implicitName = "implicit";
        Assert.Equal("implicit", implicitName.Name);
        Assert.Equal("implicit", implicitName.Value);
        Assert.Equal(string.Empty, implicitName.Description);
    }

    [Fact]
    public void FactoryPreservesExistingGuardParameterIdentity()
    {
        Assert.Equal("name", Assert.ThrowsAny<ArgumentException>(() => Option.Null(" ")).ParamName);
        Assert.Equal("name", Assert.ThrowsAny<ArgumentException>(() => Option.Null(null!)).ParamName);
        Assert.Equal("description", Assert.Throws<ArgumentNullException>(() => Option.Null("none", null!)).ParamName);
        Assert.Equal(string.Empty, Option.Null("none").Description);
    }

    [Fact]
    public async Task ScalarNullChoiceWinsBeforePresetFallback()
    {
        AgentSpec? observed = null;
        var model = new AnswerModel();
        var tool = Subagent.Create(new()
        {
            Instructions = new Choice([Option.Null("none")]),
            Model = new Choice([Option.Null("none")]),
            Tools = new Fixed(Array.Empty<string>()),
            Presets = new Dictionary<string, Preset>
            {
                ["role"] = new() { Instructions = "preset instructions", Model = model }
            },
            Builder = (specification, _, _) =>
            {
                observed = specification;
                return new(new Agent(new() { Model = model }));
            }
        });
        await using var parent = new Agent(new() { Model = model });
        var input = JsonSerializer.SerializeToElement(new { task = "inspect", agent_type = "role", instructions = "none", model = "none" });
        var context = new ToolContext(parent, new("option-call", tool.Spec.Name, input), new Dictionary<string, object?>());
        var result = await tool.InvokeResultAsync(input, context);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.NotNull(observed);
        Assert.Equal("role", observed.AgentType);
        Assert.Null(observed.Instructions);
        Assert.Null(observed.Model);
    }

    [Fact]
    public void NullToolsAndMcpChoicesRejectBeforePublication()
    {
        var builds = 0;
        ValueTask<Agent> Builder(AgentSpec _, ToolContext __, CancellationToken ___)
        {
            builds++;
            return new(new Agent(new() { Model = new AnswerModel() }));
        }
        Assert.Throws<ArgumentException>(() => Subagent.Create(new()
        {
            Builder = Builder,
            Tools = new Choice([Option.Null("none")], multiple: true)
        }));
        Assert.Throws<ArgumentException>(() => Subagent.Create(new()
        {
            Builder = Builder,
            McpServers = new Choice([Option.Null("none")], multiple: true)
        }));
        Assert.Equal(0, builds);
    }

    [Fact]
    public async Task NullContextChoiceUsesTheValidatedNoneDefault()
    {
        string? observed = null;
        var builds = 0;
        var model = new AnswerModel();
        var tool = Subagent.Create(new()
        {
            Context = new Choice([Option.Null("fresh")]),
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (specification, _, _) =>
            {
                builds++;
                observed = specification.Context;
                return new(new Agent(new() { Model = model }));
            }
        });
        await using var parent = new Agent(new() { Model = model });
        var input = JsonSerializer.SerializeToElement(new { task = "inspect", context = "fresh" });
        var context = new ToolContext(parent, new("context-call", tool.Spec.Name, input), new Dictionary<string, object?>());
        var result = await tool.InvokeResultAsync(input, context);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(1, builds);
        Assert.Equal("none", observed);
    }

    private sealed class AnswerModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "complete");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
