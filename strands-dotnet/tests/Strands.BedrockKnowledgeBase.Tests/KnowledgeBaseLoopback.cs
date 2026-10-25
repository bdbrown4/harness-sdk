using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.BedrockAgent;
using Amazon.BedrockAgentRuntime;
using Amazon.Runtime;
using Amazon.S3;

namespace Strands.BedrockKnowledgeBase.Tests;

internal sealed class KnowledgeBaseLoopback : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly List<Request> _requests = [];
    private readonly Task _loop;
    private readonly string _url;
    private readonly string _detectedType;
    private readonly Func<CancellationToken, Task>? _beforeAccept;
    private readonly Action? _afterListenerStop;
    internal Func<Request, ErrorReply?>? Respond { get; set; }
    internal sealed class ErrorReply(string errorCode, string message)
    {
        internal string ErrorCode { get; } = errorCode;
        internal string Message { get; } = message;
    }
    internal IReadOnlyList<Request> Requests { get { lock (_requests) return _requests.ToArray(); } }
    internal KnowledgeBaseLoopback(string detectedType = "VECTOR", Func<CancellationToken, Task>? beforeAccept = null, Action? afterListenerStop = null)
    {
        _detectedType = detectedType; _beforeAccept = beforeAccept; _afterListenerStop = afterListenerStop;
        _listener.Start(); _url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        _loop = ServeAsync();
    }
    internal AmazonBedrockAgentRuntimeClient Runtime() => new(new BasicAWSCredentials("fixture", "secret"), new AmazonBedrockAgentRuntimeConfig { ServiceURL = _url, AuthenticationRegion = "us-east-1", MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(10) });
    internal AmazonBedrockAgentClient Agent() => new(new BasicAWSCredentials("fixture", "secret"), new AmazonBedrockAgentConfig { ServiceURL = _url, AuthenticationRegion = "us-east-1", MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(10) });
    internal AmazonS3Client S3() => new(new BasicAWSCredentials("fixture", "secret"), new AmazonS3Config { ServiceURL = _url, ForcePathStyle = true, AuthenticationRegion = "us-east-1", MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(10) });
    private async Task ServeAsync()
    {
        using var registration = _stop.Token.Register(_listener.Stop);
        try
        {
            while (true)
            {
                if (_beforeAccept is { } beforeAccept) await beforeAccept(_stop.Token).ConfigureAwait(false);
                TcpClient accepted;
                try { accepted = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) when (_stop.IsCancellationRequested) { break; }
                using var socket = accepted; using var stream = socket.GetStream();
                using var header = new MemoryStream(); var one = new byte[1];
                while (true)
                {
                    if (await stream.ReadAsync(one, 0, 1, _stop.Token).ConfigureAwait(false) == 0) throw new EndOfStreamException();
                    header.WriteByte(one[0]); var data = header.GetBuffer(); var n = (int)header.Length;
                    if (n >= 4 && data[n - 4] == 13 && data[n - 3] == 10 && data[n - 2] == 13 && data[n - 1] == 10) break;
                    if (n > 65536) throw new InvalidDataException("Fixture header bound.");
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                var first = lines[0].Split(' '); var length = 0; var authorization = false;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':'); if (colon >= 0) headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim(), CultureInfo.InvariantCulture);
                    if (line.StartsWith("Authorization: AWS4-HMAC-SHA256 ", StringComparison.OrdinalIgnoreCase)) authorization = true;
                    if (line.StartsWith("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)) await WriteAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n")).ConfigureAwait(false);
                }
                if (!authorization || length is < 0 or > 65536) throw new InvalidDataException("Fixture requires signed bounded SDK request.");
                var body = new byte[length];
                for (var offset = 0; offset < length;)
                {
                    var read = await stream.ReadAsync(body, offset, length - offset, _stop.Token).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); offset += read;
                }
                if (headers.TryGetValue("Content-Encoding", out var encoding) && encoding.IndexOf("aws-chunked", StringComparison.OrdinalIgnoreCase) >= 0) body = DecodeAwsChunks(body);
                var captured = new Request(first[0], first[1], Encoding.UTF8.GetString(body), headers);
                lock (_requests) _requests.Add(captured);
                var response = first[0] == "GET" ? System.Text.Json.JsonSerializer.Serialize(new { knowledgeBase = new { knowledgeBaseConfiguration = new { type = _detectedType } } }) : "{\"retrievalResults\":[{\"content\":{\"text\":\"fact\"},\"score\":0,\"metadata\":{}}]}";
                if (first[1].Contains("/datasources/")) response = "{}";
                if (first[1].StartsWith("/fixture-bucket/", StringComparison.Ordinal)) response = "";
                var error = Respond?.Invoke(captured);
                if (error is not null) response = System.Text.Json.JsonSerializer.Serialize(new { message = error.Message });
                var status = error is null ? "200 OK" : "400 Bad Request";
                var errorHeader = error is null ? "" : "x-amzn-ErrorType: " + error.ErrorCode + "\r\n";
                var bytes = Encoding.UTF8.GetBytes(response);
                await WriteAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" + errorHeader + "Connection: close\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n")).ConfigureAwait(false);
                await WriteAsync(stream, bytes).ConfigureAwait(false);
            }
        }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }
    private static byte[] DecodeAwsChunks(byte[] input)
    {
        using var output = new MemoryStream(); var offset = 0;
        while (offset < input.Length)
        {
            var end = offset; while (end + 1 < input.Length && !(input[end] == 13 && input[end + 1] == 10)) end++;
            if (end + 1 >= input.Length) throw new InvalidDataException("Incomplete SDK chunk header.");
            var size = int.Parse(Encoding.ASCII.GetString(input, offset, end - offset).Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture); offset = end + 2;
            if (size == 0) return output.ToArray();
            if (size < 0 || size > input.Length - offset - 2) throw new InvalidDataException("Invalid SDK chunk size.");
            output.Write(input, offset, size); offset += size;
            if (input[offset] != 13 || input[offset + 1] != 10) throw new InvalidDataException("Invalid SDK chunk terminator.");
            offset += 2;
        }
        throw new InvalidDataException("Incomplete SDK chunks.");
    }
    private Task WriteAsync(Stream stream, byte[] bytes) => stream.WriteAsync(bytes, 0, bytes.Length, _stop.Token);
    public void Dispose() { _stop.Cancel(); _listener.Stop(); _afterListenerStop?.Invoke(); try { _loop.GetAwaiter().GetResult(); } finally { _stop.Dispose(); } }
    internal sealed class Request(string method, string target, string body, IReadOnlyDictionary<string, string> headers)
    {
        internal string Method { get; } = method;
        internal string Target { get; } = target;
        internal string Body { get; } = body;
        internal IReadOnlyDictionary<string, string> Headers { get; } = headers;
    }
}
