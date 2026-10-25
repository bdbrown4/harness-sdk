using System.Diagnostics;
using System.Text.Json;

namespace Strands.Harness.Tests;

public sealed class HarnessAssemblyPathTests
{
    [Fact]
    public async Task NativeAssemblySelectionRejectsDirectoryLinkTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "strands-module-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, "linked");
        var target = Path.Combine(AppContext.BaseDirectory, "ModuleFixture");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
                using var process = Process.Start(start)!;
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(process.ExitCode == 0, await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync());
            }
            else Directory.CreateSymbolicLink(link, target);
            var reference = JsonSerializer.Serialize(new { module = "linked/Strands.Harness.ModuleFixture.dll", export = "echo" });
            var config = HarnessAgentConfig.Parse("{\"builtinTools\":[],\"tools\":[" + reference + "]}");
            var error = Assert.Throws<ArgumentException>(() => config.ToOptions(new HarnessAssemblyLoader(root).Bind()));
            Assert.Contains("symbolic links or reparse", error.Message);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root);
        }
    }
}
