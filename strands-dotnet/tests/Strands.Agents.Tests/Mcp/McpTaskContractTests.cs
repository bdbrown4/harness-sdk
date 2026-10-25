using System.Text.Json;
using Strands.Agents.Mcp;
using Xunit;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpTaskContractTests
{
    [Fact]
    public void CompleteTaskRetainsUnlimitedTtlAndDetachedRichResult()
    {
        McpTask task;
        using (var document = JsonDocument.Parse("""
            {"resultType":"complete","taskId":"task-1","status":"completed","createdAt":"2026-10-20T10:00:00Z","lastUpdatedAt":"2026-10-20T10:00:01+00:00","ttlMs":null,"result":{"content":[{"type":"text","text":"done"}],"structuredContent":{"answer":42}},"_meta":{"vendor":"retained"}}
            """))
            task = McpTask.Parse(document.RootElement);

        Assert.Equal("task-1", task.TaskId);
        Assert.Equal(McpTaskStatus.Completed, task.Status);
        Assert.Null(task.TimeToLive);
        Assert.Equal(42, task.Result!.StructuredContent!.Value.GetProperty("answer").GetInt32());
        Assert.Equal("retained", task.Definition.GetProperty("_meta").GetProperty("vendor").GetString());
    }

    [Theory]
    [InlineData("working", "\"result\":{\"content\":[]}")]
    [InlineData("completed", "\"error\":{\"code\":-1,\"message\":\"failed\"}")]
    [InlineData("failed", "\"result\":{\"content\":[]}")]
    [InlineData("input_required", "\"inputRequests\":null")]
    [InlineData("cancelled", "\"error\":null")]
    public void InconsistentStatusPayloadsAreRejected(string status, string extra)
    {
        using var document = JsonDocument.Parse("{\"resultType\":\"complete\",\"taskId\":\"task-1\",\"status\":\"" + status + "\",\"createdAt\":\"2026-10-20T10:00:00Z\",\"lastUpdatedAt\":\"2026-10-20T10:00:01Z\",\"ttlMs\":null," + extra + "}");
        Assert.Throws<InvalidDataException>(() => McpTask.Parse(document.RootElement));
    }

    [Theory]
    [InlineData("taskId", "\"\"")]
    [InlineData("taskId", "7")]
    [InlineData("status", "\"unknown\"")]
    [InlineData("createdAt", "\"2026-10-20T10:00:00\"")]
    [InlineData("lastUpdatedAt", "\"2026-10-20T09:00:00Z\"")]
    [InlineData("ttlMs", "-1")]
    [InlineData("ttlMs", "1.5")]
    [InlineData("pollIntervalMs", "-1")]
    [InlineData("pollIntervalMs", "null")]
    [InlineData("resultType", "\"task\"")]
    public void MalformedOperationalMetadataIsRejected(string field, string raw)
    {
        using var baseline = JsonDocument.Parse("""
            {"resultType":"complete","taskId":"task-1","status":"working","createdAt":"2026-10-20T10:00:00Z","lastUpdatedAt":"2026-10-20T10:00:01Z","ttlMs":null}
            """);
        var values = baseline.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        using var replacement = JsonDocument.Parse(raw);
        values[field] = replacement.RootElement.Clone();

        Assert.Throws<InvalidDataException>(() => McpTask.Parse(JsonSerializer.SerializeToElement(values)));
    }

    [Fact]
    public void MissingTtlIsRejectedAndNonUtcOffsetsAreRetained()
    {
        using var document = JsonDocument.Parse("""
            {"resultType":"complete","taskId":"task-1","status":"working","createdAt":"2026-10-20T10:00:00-04:00","lastUpdatedAt":"2026-10-20T10:00:01-04:00","ttlMs":0,"pollIntervalMs":0}
            """);
        var task = McpTask.Parse(document.RootElement);
        Assert.Equal(TimeSpan.FromHours(-4), task.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, task.TimeToLive);
        Assert.Equal(TimeSpan.Zero, task.PollInterval);
        var missing = document.RootElement.EnumerateObject().Where(property => property.Name != "ttlMs").ToDictionary(property => property.Name, property => property.Value.Clone());
        Assert.Throws<InvalidDataException>(() => McpTask.Parse(JsonSerializer.SerializeToElement(missing)));
    }

    [Fact]
    public void InputRequestsCanOmitRootsParamsAndReturnDetachedObjects()
    {
        using var document = JsonDocument.Parse("""
            {"resultType":"complete","taskId":"task-1","status":"input_required","createdAt":"2026-10-20T10:00:00Z","lastUpdatedAt":"2026-10-20T10:00:01Z","ttlMs":null,"inputRequests":{"roots":{"method":"roots/list"}}}
            """);
        var task = McpTask.Parse(document.RootElement);
        task.InputRequests!["roots"].Method = "modified-by-caller";
        Assert.Equal("roots/list", task.InputRequests!["roots"].Method);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"type\":\"text\",\"text\":null}")]
    [InlineData("{\"type\":\"tool_use\",\"id\":\"call\",\"name\":\"tool\",\"input\":{}}")]
    [InlineData("{\"type\":\"image\",\"data\":\"ZGF0YQ==\"}")]
    public void CompletedToolResultsRejectMalformedNestedContent(string content)
    {
        using var document = JsonDocument.Parse("{\"resultType\":\"complete\",\"taskId\":\"task-1\",\"status\":\"completed\",\"createdAt\":\"2026-10-20T10:00:00Z\",\"lastUpdatedAt\":\"2026-10-20T10:00:01Z\",\"ttlMs\":null,\"result\":{\"content\":[" + content + "]}}");

        Assert.Throws<InvalidDataException>(() => McpTask.Parse(document.RootElement));
    }
}
