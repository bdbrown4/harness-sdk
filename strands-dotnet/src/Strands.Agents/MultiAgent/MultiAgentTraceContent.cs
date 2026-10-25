using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.MultiAgent;

internal static class MultiAgentTraceContent
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static string Serialize(MultiAgentInput input, bool python = false, bool latest = false)
    {
        if (python && latest)
        {
            var parts = input.IsResume ? input.InterruptResponses.Select(response => (object)new { type = "interrupt_response", id = response.InterruptId, response = response.Response }).ToArray()
                : input.Content.SelectMany(Parts).ToArray();
            return JsonSerializer.Serialize(new[] { new { role = "user", parts } }, Json);
        }
        if (input.IsTextInput) return python ? input.Text : JsonSerializer.Serialize(input.Text, Json);
        return JsonSerializer.Serialize(input.IsResume
            ? input.InterruptResponses.Select(response => (object)new { interruptResponse = new { interruptId = response.InterruptId, response = response.Response } }).ToArray()
            : input.Content.Select(block => Data(block, python)).ToArray(), Json);
    }

    private static IEnumerable<object> Parts(ContentBlock block)
    {
        switch (block)
        {
            case TextBlock text: return [new { type = "text", content = text.Text }];
            case ToolUseBlock tool: return [new { type = "tool_call", name = tool.Name, id = tool.ToolUseId, arguments = tool.Input }];
            case ToolResultBlock result: return [new { type = "tool_call_response", id = result.ToolUseId, response = ResultContent(result, true) }];
            default:
                var value = JsonSerializer.SerializeToElement(Data(block, true), Json);
                return value.EnumerateObject().Select(property => (object)new { type = property.Name, content = property.Value.Clone() }).ToArray();
        }
    }

    private static object Data(ContentBlock block, bool python) => block switch
    {
        TextBlock text => new { text = text.Text },
        ToolUseBlock tool => new { toolUse = new { toolUseId = tool.ToolUseId, name = tool.Name, input = tool.Input } },
        ToolResultBlock result => new { toolResult = new { toolUseId = result.ToolUseId, status = result.Status == ToolResultStatus.Error ? "error" : "success", content = ResultContent(result, python) } },
        JsonBlock json => new { json = json.Json },
        ReasoningBlock reasoning => python ? new { reasoningContent = new { reasoningText = new { text = reasoning.Text, signature = reasoning.Signature } } }
            : new { reasoning = new { text = reasoning.Text, signature = reasoning.Signature } },
        RedactedReasoningBlock redacted => python ? new { reasoningContent = new { redactedContent = "<replaced>" } } : new { reasoning = new { redactedContent = redacted.Data } },
        CachePointBlock cache => python ? new { cachePoint = new { type = "default", ttl = cache.Ttl } } : new { cachePoint = new { cacheType = "default", ttl = cache.Ttl } },
        ImageBlock image => new { image = Image(image, python) },
        AudioBlock audio => new { audio = new { format = Format(audio.MediaType), source = Source(audio.Data, audio.Location, python) } },
        VideoBlock video => new { video = new { format = Format(video.MediaType), source = Source(video.Data, video.Location, python) } },
        DocumentBlock document => new
        {
            document = new
            {
                format = Format(document.MediaType),
                name = document.Name,
                source = document.Text is { } text ? (object)new { text }
            : document.Content is { } content ? new { content = content.Select(segment => new { text = segment.Text }).ToArray() } : Source(document.Data, document.Location, python),
                context = document.Context,
                citations = document.CitationsEnabled is { } enabled ? new { enabled } : null
            }
        },
        GuardContentBlock guard => new { guardContent = guard.Text is { } text ? (object)new { text = new { text, qualifiers = guard.Qualifiers } } : new { image = Image(guard.Image!, python) } },
        CitationsBlock citations => python ? new { citationsContent = new { citations = citations.Citations.Select(citation => CitationData(citation, true)).ToArray(), content = citations.Content } }
            : new { citations = new { citations = citations.Citations.Select(citation => CitationData(citation, false)).ToArray(), content = citations.Content } },
        _ => JsonSerializer.SerializeToElement(block, block.GetType(), Json)
    };

    private static object[] ResultContent(ToolResultBlock result, bool python) => result.ContentBlocks.Count == 0
        ? [new { text = result.Content }] : result.ContentBlocks.Select(block => Data(block, python)).ToArray();

    private static object Image(ImageBlock image, bool python) => new { format = Format(image.MediaType), source = Source(image.Data, image.Location, python, image.Url) };

    private static object Source(string data, S3Location? location, bool python, Uri? url = null) => location is not null
        ? new { location = new { type = "s3", uri = location.Uri, bucketOwner = location.BucketOwner } }
        : url is not null ? (object)new { url = url.AbsoluteUri } : new { bytes = python ? "<replaced>" : data };

    private static object CitationData(Citation citation, bool python) => new
    {
        location = python ? PythonLocation(citation.Location) : JsonSerializer.SerializeToElement<CitationLocation>(citation.Location, Json),
        sourceContent = citation.SourceContent,
        title = citation.Title
    };

    private static object PythonLocation(CitationLocation location) => location switch
    {
        DocumentCharCitationLocation value => new { documentChar = new { documentIndex = value.DocumentIndex, start = value.Start, end = value.End } },
        DocumentPageCitationLocation value => new { documentPage = new { documentIndex = value.DocumentIndex, start = value.Start, end = value.End } },
        DocumentChunkCitationLocation value => new { documentChunk = new { documentIndex = value.DocumentIndex, start = value.Start, end = value.End } },
        SearchResultCitationLocation value => new { searchResultLocation = new { searchResultIndex = value.SearchResultIndex, start = value.Start, end = value.End } },
        WebCitationLocation value => new { web = new { url = value.Url, domain = value.Domain } },
        _ => JsonSerializer.SerializeToElement(location, location.GetType(), Json)
    };

    private static string Format(string mediaType) => mediaType switch
    {
        "text/plain" => "txt",
        "text/markdown" => "md",
        "application/msword" => "doc",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
        "application/vnd.ms-excel" => "xls",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
        _ => mediaType.Substring(mediaType.IndexOf('/') + 1)
    };
}
