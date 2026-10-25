using Strands.Harness;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class AgentToolNamingCompatibilityTests
{
    [Fact]
    public async Task PortableDefaultDisplayNameCanBeExposedAsAToolWithoutChangingAgentName()
    {
        var config = HarnessAgentConfig.Parse("{\"builtinTools\":[],\"caching\":false,\"contextManager\":false,\"skills\":false,\"memory\":false,\"builtinPlugins\":[],\"session\":false}");
        var agent = await HarnessFactory.CreateAsync(config.ToOptions());
        try
        {
            Assert.Equal("Strands harness", agent.Name);
            Assert.Equal("Strands_harness", agent.AsTool().Spec.Name);
            Assert.Equal("explicit_name", agent.AsTool("explicit_name").Spec.Name);
            Assert.Throws<ArgumentException>(() => agent.AsTool("invalid explicit name"));
        }
        finally { await agent.DisposeAsync(); }
    }
}
