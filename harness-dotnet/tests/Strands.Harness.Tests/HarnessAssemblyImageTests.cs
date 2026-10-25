using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Strands.Agents;
using Xunit.Abstractions;

namespace Strands.Harness.Tests;

public sealed class HarnessAssemblyImageTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RawSameBytesImagesInitializeFreshObjectsAndShareHostContracts()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ModuleFixture", "Strands.Harness.ModuleFixture.dll");
        var bytes = File.ReadAllBytes(path);
        var first = Assembly.Load(bytes);
        var second = Assembly.Load(bytes);
        Assert.NotSame(first, second);
        Assert.Equal(first.FullName, second.FullName);
        Assert.Equal("", first.Location);
        Assert.Equal("", second.Location);
        var firstType = first.GetType("Strands.Harness.ModuleFixture.Exports", true)!;
        var secondType = second.GetType("Strands.Harness.ModuleFixture.Exports", true)!;
        var firstTool = Assert.IsAssignableFrom<ITool>(firstType.GetProperty("Borrowed")!.GetValue(null));
        var secondTool = Assert.IsAssignableFrom<ITool>(secondType.GetProperty("Borrowed")!.GetValue(null));
        var firstModel = Assert.IsAssignableFrom<IModel>(firstType.GetProperty("Model")!.GetValue(null));
        var secondModel = Assert.IsAssignableFrom<IModel>(secondType.GetProperty("Model")!.GetValue(null));
        Assert.NotSame(firstTool, secondTool);
        Assert.NotSame(firstModel, secondModel);
        Assert.Same(typeof(ITool), firstTool.GetType().GetInterface(typeof(ITool).FullName!));
        Assert.Same(typeof(IModel), firstModel.GetType().GetInterface(typeof(IModel).FullName!));
        Assert.IsType<HarnessExportAttribute>(firstType.GetProperty("Borrowed")!.GetCustomAttribute(typeof(HarnessExportAttribute)));
        var input = JsonSerializer.SerializeToElement(new { });
        foreach (var type in new[] { firstType, secondType })
        {
            var setters = Assert.IsAssignableFrom<ITool>(type.GetProperty("InitSetters")!.GetValue(null));
            Assert.Equal("native:fixture:3", await setters.InvokeAsync(input, new(null!, new("abi", setters.Spec.Name, input), new Dictionary<string, object?>())));
        }
        await using (var agent = new Agent(new() { Model = secondModel, Tools = [secondTool] }))
        {
            await agent.InitializeAsync();
            Assert.Same(secondTool, Assert.Single(agent.Tools));
        }
        Assert.Equal("alive", await firstTool.InvokeAsync(input, new(null!, new("old", firstTool.Spec.Name, input), new Dictionary<string, object?>())));
        Assert.Equal("alive", await secondTool.InvokeAsync(input, new(null!, new("new", secondTool.Spec.Name, input), new Dictionary<string, object?>())));
        using var hash = SHA256.Create();
        output.WriteLine("RAW_IMAGE_SHA256=" + BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""));
        output.WriteLine("RAW_IMAGE_ARCHITECTURE=" + (IntPtr.Size == 8 ? "x64" : "x86") + ";POINTER=" + IntPtr.Size);
    }
}
