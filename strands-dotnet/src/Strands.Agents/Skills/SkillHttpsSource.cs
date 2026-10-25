using System.Text;

namespace Strands.Agents.Skills;

internal static class SkillHttpsSource
{
    private const int MaxReadBytes = 5 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<Skill> LoadAsync(string url, HttpClient? httpClient, bool strict, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(url, nameof(url));
        if (!url.StartsWith("https://", StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri is null || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A skill URL must be an absolute https:// URL.", nameof(url));
        cancellationToken.ThrowIfCancellationRequested();
        using var ownedClient = httpClient is null ? new HttpClient() : null;
        var client = httpClient ?? ownedClient!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("strands-agents-sdk");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxReadBytes)
                throw new IOException("A skill document cannot exceed 5 MiB.");
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (bytes.Length + count > MaxReadBytes) throw new IOException("A skill document cannot exceed 5 MiB.");
                bytes.Write(buffer, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return Skill.FromContent(StrictUtf8.GetString(bytes.ToArray()), strict);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Fetching the skill document exceeded the 30 second deadline.", exception);
        }
    }
}
