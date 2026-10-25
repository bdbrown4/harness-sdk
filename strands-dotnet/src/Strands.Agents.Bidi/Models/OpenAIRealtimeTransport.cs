using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

public sealed partial class OpenAIRealtimeModel
{
    private static Task SendEventAsync(RealtimeConnection connection, object value, CancellationToken cancellationToken)
        => SendBytesAsync(connection, EventBytes(value, connection.Options.MaximumEventBytes), cancellationToken);
    private static byte[] EventBytes(object value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > maximumBytes) throw new ModelException("Realtime outgoing event exceeds MaximumEventBytes.");
        return bytes;
    }
    private static async Task SendBytesAsync(RealtimeConnection connection, byte[] bytes, CancellationToken cancellationToken)
    {
        await connection.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
        finally { connection.SendGate.Release(); }
    }

    private static async Task<JsonElement?> ReadEventAsync(RealtimeConnection connection, CancellationToken caller)
    {
        var remaining = connection.Options.Timeout - connection.Clock.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new BidiConnectionTimeoutException("The OpenAI Realtime connection reached its configured deadline.");
        using var deadline = new CancellationTokenSource(); deadline.CancelAfter(remaining);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(caller, connection.Token, deadline.Token);
        Exception? abortFailure = null;
        using var registration = operation.Token.Register(() => { try { connection.Socket.Abort(); } catch (Exception error) { abortFailure = error; } });
        try
        {
            using var message = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (result.CloseStatus is null or WebSocketCloseStatus.NormalClosure) return null;
                    throw new ModelException("Realtime closed with status " + result.CloseStatus + ".");
                }
                if (result.MessageType != WebSocketMessageType.Text) throw new ModelException("Realtime server events must be JSON text frames.");
                if (message.Length + result.Count > connection.Options.MaximumEventBytes) throw new ModelException("Realtime incoming event exceeds MaximumEventBytes.");
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = new UTF8Encoding(false, true).GetString(message.ToArray());
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ModelException("Realtime events must be JSON objects.");
                try { _ = RealtimeJson.Copy(document.RootElement); }
                catch (ArgumentException error) { throw new ModelException("Realtime server events cannot contain duplicate JSON fields.", error); }
                return document.RootElement.Clone();
            }
        }
        catch (Exception error)
        {
            registration.Dispose();
            Exception primary = deadline.IsCancellationRequested && !caller.IsCancellationRequested && !connection.Token.IsCancellationRequested
                ? new BidiConnectionTimeoutException("The OpenAI Realtime connection reached its configured deadline.") : error;
            if (abortFailure is { } cleanup) Cleanup(primary, cleanup);
            if (!ReferenceEquals(primary, error)) primary.Data["Strands.TransportError"] = error;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); throw;
        }
    }

    private static async Task SendMessageAsync(RealtimeConnection connection, BidiMessage message, CancellationToken cancellationToken)
    {
        var items = message.Content[0] is ToolResultBlock
            ? message.Content.Cast<ToolResultBlock>().Select(result => (object)new { type = "function_call_output", call_id = result.ToolUseId, output = ToolResultOutput(result) }).ToArray()
            : new object[] { new { type = "message", role = "user", content = message.Content.Select(UserContent).ToArray() } };
        var prepared = items.Select(value =>
        {
            var item = RealtimeJson.Copy(JsonSerializer.SerializeToElement(value));
            var id = Guid.NewGuid().ToString("N"); item["id"] = id;
            return (Id: id, Item: item, Bytes: EventBytes(new { type = "conversation.item.create", item }, connection.Options.MaximumEventBytes));
        }).ToArray();
        foreach (var value in prepared)
        {
            connection.State.PendingInputIds.Add(value.Id); connection.State.ResponsePending = true;
            await SendBytesAsync(connection, value.Bytes, cancellationToken).ConfigureAwait(false);
            if (value.Item["type"] is JsonElement type && type.GetString() == "function_call_output" && value.Item["call_id"] is JsonElement call) connection.State.PendingTools.Remove(call.GetString()!);
        }
        await FlushResponseAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static object UserContent(ContentBlock block) => block switch
    {
        TextBlock text => new { type = "input_text", text = text.Text },
        ImageBlock image when image.Location is null && image.Url is null => new { type = "input_image", image_url = "data:" + image.MediaType + ";base64," + image.Data },
        ImageBlock => throw new ArgumentException("OpenAI Realtime requires inline image bytes."),
        _ => throw new ArgumentException("Unsupported OpenAI Realtime user content.")
    };

    private static string ToolResultOutput(ToolResultBlock result)
    {
        var output = new List<object>();
        var blocks = result.ContentBlocks.Count > 0 ? result.ContentBlocks : new ContentBlock[] { new TextBlock(result.Content) };
        foreach (var block in blocks)
            output.Add(block switch { TextBlock text => (object)new { text = text.Text }, JsonBlock json => new { json = json.Json }, _ => throw new ArgumentException("OpenAI Realtime tool results support only text and JSON.", nameof(result)) });
        if (result.StructuredContent is { } structured && !blocks.OfType<JsonBlock>().Any(value => JsonElement.DeepEquals(value.Json, structured))) output.Add(new { json = structured });
        return JsonSerializer.Serialize(output);
    }

    private static async Task FlushResponseAsync(RealtimeConnection connection, CancellationToken cancellationToken)
    {
        var state = connection.State;
        if (state.InputAudioPending || !state.ResponsePending || state.ResponseRequested || state.ActiveResponses.Count > 0 || state.PendingTools.Count > 0) return;
        state.ResponsePending = false; state.ResponseRequested = true; state.PendingInputIds.Clear();
        try { await SendEventAsync(connection, new { type = "response.create" }, cancellationToken).ConfigureAwait(false); }
        catch { state.ResponsePending = true; state.ResponseRequested = false; throw; }
    }

    private static IReadOnlyList<object> History(IReadOnlyList<Message> messages)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var unique = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in messages.SelectMany(message => message.Content).OfType<ToolUseBlock>())
        {
            var wire = call.ToolUseId.Substring(0, Math.Min(call.ToolUseId.Length, 32));
            if (unique.TryGetValue(wire, out var original) && original != call.ToolUseId) throw new ArgumentException("Realtime history tool identifiers collide after the provider's 32-character limit.", nameof(messages));
            ids[call.ToolUseId] = wire; unique[wire] = call.ToolUseId;
        }
        var output = new List<object>();
        foreach (var message in messages)
        {
            var text = new List<object>();
            foreach (var block in message.Content)
            {
                if (block is TextBlock plain) text.Add(new { type = message.Role == Role.User ? "input_text" : "output_text", text = plain.Text });
                else if (block is ImageBlock image && message.Role == Role.User) text.Add(UserContent(image));
                else if (block is ToolUseBlock call) output.Add(new { type = "function_call", call_id = ids[call.ToolUseId], name = call.Name, arguments = call.Input.GetRawText() });
                else if (block is ToolResultBlock result)
                {
                    var content = ToolResultOutput(result);
                    if (ids.TryGetValue(result.ToolUseId, out var id)) output.Add(new { type = "function_call_output", call_id = id, output = content });
                }
                else throw new ArgumentException("OpenAI Realtime cannot replay " + block.GetType().Name + ".", nameof(messages));
            }
            if (text.Count > 0) output.Add(new { type = "message", role = message.Role == Role.User ? "user" : "assistant", content = text });
        }
        return output;
    }
}
