using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Strands.Agents.S3;
using Strands.Agents.Storage;

namespace Strands.S3.Tests;

public sealed class S3StorageWireTests
{
    [Fact]
    public async Task OfficialSdkSerializesAllFourOperationsAndParsesNativeXmlOnLoopbackOnly()
    {
        using var server = new LoopbackS3([
            new(200, "", Array.Empty<byte>()),
            new(200, "application/octet-stream", new byte[] { 0, 255, 13, 10 }),
            new(200, "application/xml", Encoding.UTF8.GetBytes("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>fixture-bucket</Name><IsTruncated>false</IsTruncated><Contents><Key>root/a b/雪</Key><Size>4</Size></Contents></ListBucketResult>")),
            new(204, "", Array.Empty<byte>())]);
        using var client = NativeClient(server.Url);
        await using var storage = new S3Storage(client, "fixture-bucket", new() { Prefix = "root/" });
        await storage.WriteAsync("a b/雪", new byte[] { 0, 255, 13, 10 });
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, await storage.ReadAsync("a b/雪"));
        Assert.Equal(new[] { "a b/雪" }, await storage.ListAsync("a b/"));
        await storage.DeleteAsync("a b/雪");
        await S3StorageSafetyTests.Ready(server.Completion);
        Assert.Equal(new[] { "PUT", "GET", "GET", "DELETE" }, server.Requests.Select(request => request.Method));
        Assert.All(server.Requests, request => { Assert.StartsWith("AWS4-HMAC-SHA256 ", request.Headers["Authorization"]); Assert.Contains("fixture-access/", request.Headers["Authorization"]); Assert.Contains("aws-sdk-dotnet", request.Headers["User-Agent"]); });
        Assert.Equal("/fixture-bucket/root/a%20b/%E9%9B%AA", server.Requests[0].Target);
        Assert.Equal(server.Requests[0].Target, server.Requests[1].Target); Assert.Equal(server.Requests[0].Target, server.Requests[3].Target);
        var listing = server.Requests[2].Target;
        Assert.Contains("list-type=2", listing); Assert.Contains("max-keys=1000", listing); Assert.Contains("prefix=root%2Fa%20b%2F", listing);
        Assert.NotEmpty(server.Requests[0].Body);
    }

    [Fact]
    public async Task OfficialSdkDeserializedErrorCodesControlMissingPolicyInsteadOfHttpStatus()
    {
        using var server = new LoopbackS3([
            Error("NoSuchKey"), Error("NoSuchBucket"), Error("NotFound"), Error("NotFound")]);
        using var client = NativeClient(server.Url);
        await using var normal = new S3Storage(client, "fixture-bucket"); await using var strict = new S3Storage(client, "fixture-bucket", new() { MissingKeyPolicy = S3MissingKeyPolicy.NoSuchKeyOnly });
        Assert.Null(await normal.ReadAsync("absent"));
        Assert.Equal("NoSuchBucket", Assert.IsType<AmazonS3Exception>((await Assert.ThrowsAsync<StorageException>(async () => await normal.ReadAsync("absent"))).InnerException).ErrorCode);
        Assert.Null(await normal.ReadAsync("absent"));
        Assert.Equal("NotFound", Assert.IsType<AmazonS3Exception>((await Assert.ThrowsAsync<StorageException>(async () => await strict.ReadAsync("absent"))).InnerException).ErrorCode);
        await S3StorageSafetyTests.Ready(server.Completion);
    }

    private static WireResponse Error(string code) => new(404, "application/xml", Encoding.UTF8.GetBytes($"<Error><Code>{code}</Code><Message>fixture missing</Message><RequestId>fixture</RequestId></Error>"));
    private static AmazonS3Client NativeClient(string url) => new(new BasicAWSCredentials("fixture-access", "fixture-secret"), new AmazonS3Config { ServiceURL = url, ForcePathStyle = true, AuthenticationRegion = "us-east-1", MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(10) });
    private sealed record WireResponse(int Status, string ContentType, byte[] Body);
    private sealed record WireRequest(string Method, string Target, Dictionary<string, string> Headers, byte[] Body);

    private sealed class LoopbackS3 : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        internal string Url { get; }
        internal List<WireRequest> Requests { get; } = [];
        internal Task Completion { get; }
        internal LoopbackS3(IReadOnlyList<WireResponse> responses)
        {
            _listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture); Completion = ServeAsync(responses);
        }
        private async Task ServeAsync(IReadOnlyList<WireResponse> responses)
        {
            using var registration = _stop.Token.Register(_listener.Stop);
            foreach (var response in responses)
            {
                using var connection = await _listener.AcceptTcpClientAsync(); using var stream = connection.GetStream();
                var header = await HeaderAsync(stream, _stop.Token); var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None); var request = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1)) { var colon = line.IndexOf(':'); if (colon >= 0) headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim(); }
                if (headers.TryGetValue("Expect", out var expect) && expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0) await SendAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), _stop.Token);
                var body = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length, System.Globalization.CultureInfo.InvariantCulture) : 0]; var offset = 0;
                while (offset < body.Length) { var count = await stream.ReadAsync(body, offset, body.Length - offset, _stop.Token); if (count == 0) throw new EndOfStreamException("Incomplete SDK request body."); offset += count; }
                if (headers.ContainsKey("Transfer-Encoding")) throw new InvalidDataException("This bounded fixture requires an SDK content-length request.");
                Requests.Add(new(request[0], request[1], headers, body));
                var prefix = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} Fixture\r\nConnection: close\r\nx-amz-request-id: fixture\r\nContent-Type: {response.ContentType}\r\nContent-Length: {response.Body.Length}\r\n\r\n");
                await SendAsync(stream, prefix, _stop.Token); await SendAsync(stream, response.Body, _stop.Token);
            }
        }
        private static async Task<string> HeaderAsync(Stream stream, CancellationToken token)
        {
            using var data = new MemoryStream(); var next = new byte[1];
            while (data.Length < 65536)
            {
                if (await stream.ReadAsync(next, 0, 1, token) == 0) throw new EndOfStreamException("Incomplete request headers."); data.WriteByte(next[0]);
                var bytes = data.GetBuffer(); var length = (int)data.Length;
                if (length >= 4 && bytes[length - 4] == 13 && bytes[length - 3] == 10 && bytes[length - 2] == 13 && bytes[length - 1] == 10) return Encoding.ASCII.GetString(bytes, 0, length - 4);
            }
            throw new InvalidDataException("Request headers exceeded the fixture bound.");
        }
        private static Task SendAsync(Stream stream, byte[] bytes, CancellationToken token) => stream.WriteAsync(bytes, 0, bytes.Length, token);
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop();
            try { Completion.GetAwaiter().GetResult(); } catch (Exception) when (_stop.IsCancellationRequested && Completion.Status != TaskStatus.RanToCompletion) { }
            _stop.Dispose();
        }
    }
}
