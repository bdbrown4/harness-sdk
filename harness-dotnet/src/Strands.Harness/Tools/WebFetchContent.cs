using System.Text;
using System.Text.RegularExpressions;

namespace Strands.Harness.Tools;

internal sealed record FetchedPage(string ResolvedUrl, string Text);

internal static class WebFetchContent
{
    private static readonly Regex AllowedUrlCharacters = new(@"\A[A-Za-z0-9\-._~:/?#[\]@!$&'()*+,;=%]+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Authority = new(@"\Ahttps?://[^/?#]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static string ValidateUrl(string value)
    {
        value = value.Trim();
        if (!AllowedUrlCharacters.IsMatch(value)) throw new ArgumentException("web_fetch URLs may only contain the characters RFC 3986 allows; percent-encode spaces and non-ASCII characters (and punycode the host) and retry.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("web_fetch only supports well-formed http(s) URLs.");
        if (string.IsNullOrEmpty(uri.Host) || !Authority.IsMatch(value)) throw new ArgumentException("web_fetch URL has no host.");
        return value;
    }
    internal static FetchedPage Convert(byte[] data, string contentType, string resolvedUrl, string originalUrl)
    {
        var charset = Regex.Match(contentType, "charset=[\"']?([\\w.-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Encoding encoding;
        try { encoding = charset.Success ? CodePagesEncodingProvider.Instance.GetEncoding(charset.Groups[1].Value) ?? Encoding.GetEncoding(charset.Groups[1].Value) : Encoding.UTF8; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { encoding = Encoding.UTF8; }
        var text = encoding.GetString(data);
        if (contentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            const RegexOptions flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            text = Regex.Replace(text, @"<(script|style)\b[^>]*>[\s\S]*?</\1>", " ", flags, TimeSpan.FromSeconds(2));
            text = Regex.Replace(text, @"<[^>]+>", " ", flags, TimeSpan.FromSeconds(2));
            text = Regex.Replace(text, @"[ \t]+", " ", flags, TimeSpan.FromSeconds(2));
            text = Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n", flags, TimeSpan.FromSeconds(2)).Trim();
        }
        if (text.Length > WebFetch.MaxCharacters)
        {
            var count = WebFetch.MaxCharacters;
            if (char.IsHighSurrogate(text[count - 1]) && char.IsLowSurrogate(text[count])) count--;
            text = text.Substring(0, count);
        }
        return new(string.IsNullOrWhiteSpace(resolvedUrl) ? originalUrl : resolvedUrl.Trim(), text);
    }
}
