using System.Text;

namespace Strands.Agents.Files;

/// <summary>Explicit text conveniences over an existing selected byte filesystem.</summary>
/// <remarks>Explicit source and Encoding reads use the selected byte capability and do not inherit an undiscoverable native text-read limit.</remarks>
public static class FileSystemTextExtensions
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding ReplacementUtf8 = new UTF8Encoding(false, false);

    /// <summary>Writes text using the native strict UTF-8 convenience, without a byte preamble.</summary>
    public static ValueTask WriteTextAsync(this IFileSystem fileSystem, string path, string text, CancellationToken cancellationToken = default) =>
        WriteTextWithProfileAsync(fileSystem, path, text, FileTextProfile.Native, cancellationToken);

    /// <summary>Reads native text or explicitly decodes the selected byte capability.</summary>
    /// <remarks>Native delegates to the existing selected text policy. TypeScript uses UTF-8 replacement and removes one initial BOM; Python uses strict UTF-8 and retains the BOM.</remarks>
    public static ValueTask<string> ReadTextWithProfileAsync(this IFileSystem fileSystem, string path, FileTextProfile profile, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(fileSystem, nameof(fileSystem));
        Guard.NotNull(path, nameof(path));
        ValidateProfile(profile);
        cancellationToken.ThrowIfCancellationRequested();
        return profile == FileTextProfile.Native ? fileSystem.ReadTextAsync(path, cancellationToken)
            : ReadEncodedAsync(fileSystem, path, profile == FileTextProfile.TypeScript ? ReplacementUtf8 : StrictUtf8, profile == FileTextProfile.TypeScript, cancellationToken);
    }

    /// <summary>Writes text using an explicit conversion profile, without a byte preamble.</summary>
    public static ValueTask WriteTextWithProfileAsync(this IFileSystem fileSystem, string path, string text, FileTextProfile profile, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(fileSystem, nameof(fileSystem));
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(text, nameof(text));
        ValidateProfile(profile);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = (profile == FileTextProfile.TypeScript ? ReplacementUtf8 : StrictUtf8).GetBytes(text);
        return fileSystem.WriteBytesAsync(path, bytes, cancellationToken);
    }

    /// <summary>Decodes selected bytes using a caller-provided BCL encoding captured before the read.</summary>
    /// <remarks>Encoding.Clone is a shallow BCL copy. GetString semantics apply without automatic preamble removal.</remarks>
    public static ValueTask<string> ReadTextWithEncodingAsync(this IFileSystem fileSystem, string path, Encoding encoding, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(fileSystem, nameof(fileSystem));
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(encoding, nameof(encoding));
        cancellationToken.ThrowIfCancellationRequested();
        var captured = (Encoding)encoding.Clone();
        return ReadEncodedAsync(fileSystem, path, captured, false, cancellationToken);
    }

    /// <summary>Encodes text using a caller-provided BCL encoding captured before the write.</summary>
    /// <remarks>GetBytes semantics apply without automatic preamble insertion. The selected write is returned without a post-commit cancellation check.</remarks>
    public static ValueTask WriteTextWithEncodingAsync(this IFileSystem fileSystem, string path, string text, Encoding encoding, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(fileSystem, nameof(fileSystem));
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(text, nameof(text));
        Guard.NotNull(encoding, nameof(encoding));
        cancellationToken.ThrowIfCancellationRequested();
        var captured = (Encoding)encoding.Clone();
        return fileSystem.WriteBytesAsync(path, captured.GetBytes(text), cancellationToken);
    }

    private static async ValueTask<string> ReadEncodedAsync(IFileSystem fileSystem, string path, Encoding encoding, bool omitBom, CancellationToken cancellationToken)
    {
        var bytes = await fileSystem.ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var offset = omitBom && bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return omitBom ? DecodeTypeScriptUtf8(bytes, offset) : encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    private static string DecodeTypeScriptUtf8(byte[] bytes, int offset)
    {
        var text = new StringBuilder(bytes.Length - offset);
        var codePoint = 0;
        var bytesSeen = 0;
        var bytesNeeded = 0;
        var lowerBoundary = 0x80;
        var upperBoundary = 0xbf;
        for (var index = offset; index < bytes.Length; index++)
        {
            var value = bytes[index];
            if (bytesNeeded == 0)
            {
                if (value <= 0x7f) { text.Append((char)value); continue; }
                if (value is >= 0xc2 and <= 0xdf) { bytesNeeded = 1; codePoint = value & 0x1f; }
                else if (value is >= 0xe0 and <= 0xef)
                {
                    if (value == 0xe0) lowerBoundary = 0xa0;
                    if (value == 0xed) upperBoundary = 0x9f;
                    bytesNeeded = 2; codePoint = value & 0xf;
                }
                else if (value is >= 0xf0 and <= 0xf4)
                {
                    if (value == 0xf0) lowerBoundary = 0x90;
                    if (value == 0xf4) upperBoundary = 0x8f;
                    bytesNeeded = 3; codePoint = value & 0x7;
                }
                else text.Append('\ufffd');
                continue;
            }
            if (value < lowerBoundary || value > upperBoundary)
            {
                codePoint = bytesNeeded = bytesSeen = 0;
                lowerBoundary = 0x80; upperBoundary = 0xbf;
                text.Append('\ufffd');
                // A rejected continuation is processed again as a leading byte.
                index--;
                continue;
            }
            lowerBoundary = 0x80; upperBoundary = 0xbf;
            codePoint = (codePoint << 6) | (value & 0x3f);
            if (++bytesSeen != bytesNeeded) continue;
            if (codePoint <= 0xffff) text.Append((char)codePoint);
            else
            {
                codePoint -= 0x10000;
                text.Append((char)(0xd800 + (codePoint >> 10)));
                text.Append((char)(0xdc00 + (codePoint & 0x3ff)));
            }
            codePoint = bytesNeeded = bytesSeen = 0;
        }
        if (bytesNeeded != 0) text.Append('\ufffd');
        return text.ToString();
    }

    private static void ValidateProfile(FileTextProfile profile)
    {
        if (profile is not (FileTextProfile.Native or FileTextProfile.TypeScript or FileTextProfile.Python))
            throw new ArgumentOutOfRangeException(nameof(profile));
    }
}
