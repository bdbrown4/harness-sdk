using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Tests.Models;

public sealed class DocumentProviderTests
{
    public static IEnumerable<object[]> Sources()
    {
        foreach (var provider in new[] { "openai", "responses", "google" })
            foreach (var segmented in new[] { false, true })
                foreach (var rich in new[] { false, true }) yield return [provider, segmented, rich];
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task DocumentTextAndSegmentsRetainTheirProviderProjectionAndOrder(string provider, bool segmented, bool rich)
    {
        using var handler = new MediaSourceTests.CaptureHandler();
        using var client = new HttpClient(handler);
        var model = MediaSourceTests.Create(provider, client);
        var document = segmented ? DocumentBlock.FromContent([new("first segment"), new("second segment")]) : DocumentBlock.FromText("Résumé 🌍");
        ContentBlock source = rich ? new ToolResultBlock("call", [document]) : document;
        try
        {
            await Assert.ThrowsAsync<ModelException>(() => MediaSourceTests.Collect(model, new([new(Role.User, [source])])));
            var body = handler.Body!.Value;
            if (provider == "google" && !segmented)
                Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Résumé 🌍")), Strings(body));
            else
            {
                var text = string.Join("|", Strings(body));
                if (segmented)
                {
                    Assert.Contains("first segment", text);
                    Assert.Contains("second segment", text);
                    Assert.True(text.IndexOf("first segment", StringComparison.Ordinal) < text.IndexOf("second segment", StringComparison.Ordinal), text);
                }
                else Assert.Contains("Résumé 🌍", text);
                Assert.DoesNotContain("data:text/plain", body.GetRawText());
            }
        }
        finally { (model as IDisposable)?.Dispose(); }
    }

    private static IEnumerable<string> Strings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) foreach (var text in Strings(property.Value)) yield return text;
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (var text in Strings(item)) yield return text;
    }
}
