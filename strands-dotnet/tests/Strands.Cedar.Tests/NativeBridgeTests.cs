using System.Runtime.InteropServices;
using System.Text.Json;
using Strands.Agents.Interventions.Cedar;
using Xunit.Abstractions;

namespace Strands.Cedar.Tests;

public sealed class NativeBridgeTests(ITestOutputHelper output)
{
    [Fact]
    public void RealEngineVersionAndRequestedProcessArchitectureAreVerified()
    {
        output.WriteLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}; pointer size: {IntPtr.Size}.");
        output.WriteLine($"Managed location: {typeof(CedarAuthorization).Assembly.Location}; base directory: {AppContext.BaseDirectory}.");
        var version = NativeCedar.Execute("version", new { });
        Assert.Equal("4.12.0", version.GetProperty("sdk").GetString());
        if (Environment.GetEnvironmentVariable("STRANDS_CEDAR_EXPECTED_ARCHITECTURE") is { Length: > 0 } expected)
        {
            Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
            Assert.Equal(expected == "X86" ? 4 : 8, IntPtr.Size);
        }
    }

    [Fact]
    public void UnknownNativeOperationsAreErrorsAndCannotReturnAuthorization()
    {
        var error = Assert.Throws<InvalidOperationException>(() => NativeCedar.Execute("unknown", new { }));
        Assert.Contains("Unsupported Cedar operation", error.Message);
        Assert.Throws<InvalidOperationException>(() => NativeCedar.Execute("unknown", new { }, schema: true));
    }

    [Fact]
    public void OfficialGeneratorRejectsMalformedMcpInputShape()
    {
        var error = Assert.Throws<InvalidOperationException>(() => NativeCedar.Execute("generateSchema", new
        {
            stub = "namespace Agent { @mcp_principal entity User; @mcp_resource entity Resource; }",
            tools = new[] { new { name = "bad", inputSchema = 7 } }
        }, schema: true));
        Assert.Contains("Cedar native bridge failed", error.Message);
    }
}
