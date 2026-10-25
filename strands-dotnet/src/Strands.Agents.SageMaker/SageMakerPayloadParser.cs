using System.Text;
using System.Text.Json;

namespace Strands.Agents.SageMaker;

internal sealed class SageMakerPayloadParser(int maximumBytes, SageMakerMalformedEventPolicy malformed)
{
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly StringBuilder _buffer = new();

    internal IReadOnlyList<JsonElement> Append(byte[] bytes, bool final = false, int? length = null)
    {
        var byteCount = length ?? bytes.Length;
        var characters = new char[Encoding.UTF8.GetMaxCharCount(byteCount)];
        int count;
        try { count = _decoder.GetChars(bytes, 0, byteCount, characters, 0, final); }
        catch (DecoderFallbackException error) { throw new ModelException("SageMaker returned invalid UTF-8.", error); }
        _buffer.Append(characters, 0, count);
        var events = new List<JsonElement>();
        while (true)
        {
            var text = _buffer.ToString();
            var newline = text.IndexOf('\n');
            if (newline < 0) break;
            var line = text.Substring(0, newline);
            var payload = Strip(line);
            if (payload is null) { _buffer.Remove(0, newline + 1); continue; }
            if (Encoding.UTF8.GetByteCount(payload) > maximumBytes) throw new ModelException("SageMaker's event exceeds MaximumResponseBytes.");
            if (TryParse(payload, out var value)) { events.Add(value); _buffer.Remove(0, newline + 1); continue; }
            if (!line.TrimStart().StartsWith("data:", StringComparison.Ordinal)) break;
            Invalid("SageMaker skipped a malformed SSE data event.");
            _buffer.Remove(0, newline + 1);
        }
        var remainder = Strip(_buffer.ToString());
        if (remainder is not null && Encoding.UTF8.GetByteCount(remainder) > maximumBytes) throw new ModelException("SageMaker's event exceeds MaximumResponseBytes.");
        if (remainder is not null && TryParse(remainder, out var parsed)) { events.Add(parsed); _buffer.Clear(); }
        if (Encoding.UTF8.GetByteCount(_buffer.ToString()) > maximumBytes) throw new ModelException("SageMaker's incomplete event exceeds MaximumResponseBytes.");
        if (final && Strip(_buffer.ToString()) is not null) Invalid("SageMaker ended with an undecoded partial event.");
        return events;
    }

    private static string? Strip(string line)
    {
        line = line.Trim();
        if (line.StartsWith("data:", StringComparison.Ordinal)) line = line.Substring(5).Trim();
        else if (line.StartsWith("event:", StringComparison.Ordinal) || line.StartsWith("id:", StringComparison.Ordinal) || line.StartsWith("retry:", StringComparison.Ordinal)) return null;
        return line.Length == 0 || line.StartsWith(":", StringComparison.Ordinal) || line == "[DONE]" ? null : line;
    }
    private static bool TryParse(string text, out JsonElement value)
    {
        try { using var document = JsonDocument.Parse(text); value = document.RootElement.Clone(); return true; }
        catch (JsonException) { value = default; return false; }
    }
    private void Invalid(string message)
    {
        if (malformed == SageMakerMalformedEventPolicy.Throw) throw new ModelException(message);
        SdkLogging.Warning("{Message}", message);
    }
}
