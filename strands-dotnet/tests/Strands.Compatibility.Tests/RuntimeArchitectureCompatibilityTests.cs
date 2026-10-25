using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit.Abstractions;

namespace Strands.Compatibility.Tests;

public sealed class RuntimeArchitectureCompatibilityTests(ITestOutputHelper output)
{
    [Fact]
    public void TestHostUsesTheRequestedProcessArchitecture()
    {
        var expected = Environment.GetEnvironmentVariable("STRANDS_TEST_PROCESS_ARCHITECTURE");
        output.WriteLine(JsonSerializer.Serialize(new
        {
            PointerSize = IntPtr.Size,
            Runtime = RuntimeInformation.FrameworkDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ExpectedArchitecture = expected,
        }));
        if (string.IsNullOrEmpty(expected)) return;
        Assert.Contains(expected, new[] { "x86", "x64" });
        Assert.Equal(expected == "x86" ? 4 : 8, IntPtr.Size);
        Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
    }
}
