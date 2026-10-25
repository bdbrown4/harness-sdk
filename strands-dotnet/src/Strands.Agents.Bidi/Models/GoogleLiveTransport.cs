using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

public sealed partial class GoogleGeminiLiveModel
{
    private static byte[] EventBytes(object value, int maximum)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > maximum) throw new ModelException("Gemini Live outgoing event exceeds MaximumEventBytes.");
        return bytes;
    }
    private static async Task SendEventAsync(LiveConnection connection, object value, CancellationToken cancellationToken)
    {
        var bytes = EventBytes(value, connection.Options.MaximumEventBytes);
        await connection.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); }
        finally { connection.SendGate.Release(); }
    }
    private static async Task<JsonElement?> ReadEventAsync(LiveConnection connection, CancellationToken caller)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(caller, connection.Token);
        Exception? abortFailure = null;
        using var registration = operation.Token.Register(() => { try { connection.Socket.Abort(); } catch (Exception error) { abortFailure = error; } });
        try
        {
            using var message = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var frame = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    if (frame.CloseStatus is null or WebSocketCloseStatus.NormalClosure) return null;
                    throw new ModelException("Gemini Live closed with status " + frame.CloseStatus + ".");
                }
                if (frame.MessageType != WebSocketMessageType.Text) throw new ModelException("Gemini Live requires JSON text frames.");
                if (message.Length + frame.Count > connection.Options.MaximumEventBytes) throw new ModelException("Gemini Live incoming event exceeds MaximumEventBytes.");
                message.Write(buffer, 0, frame.Count);
                if (!frame.EndOfMessage) continue;
                using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(message.ToArray()));
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ModelException("Gemini Live messages must be objects.");
                try { _ = RealtimeJson.Copy(document.RootElement); }
                catch (ArgumentException error) { throw new ModelException("Gemini Live messages cannot contain duplicate fields.", error); }
                return document.RootElement.Clone();
            }
        }
        catch (Exception primary)
        {
            registration.Dispose();
            if (abortFailure is { } cleanup) Cleanup(primary, cleanup);
            ExceptionDispatchInfo.Capture(primary).Throw(); throw;
        }
    }
    private static void CheckError(JsonElement value)
    {
        if (value.TryGetProperty("error", out var error)) throw new ModelException("Gemini Live returned an error: " + error.GetRawText());
    }
}
