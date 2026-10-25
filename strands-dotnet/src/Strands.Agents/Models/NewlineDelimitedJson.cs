using System.Runtime.CompilerServices;
using System.Text;

namespace Strands.Agents.Models;

internal static class NewlineDelimitedJson
{
    internal static async IAsyncEnumerable<string> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int maximumCharacters = 4 * 1024 * 1024;
        var encoding = new UTF8Encoding(false, true);
        var decoder = encoding.GetDecoder();
        var bytes = new byte[1024];
        var characters = new char[encoding.GetMaxCharCount(bytes.Length)];
        var line = new StringBuilder();
        var first = true;
        while (true)
        {
            int read, count;
            try
            {
                read = await ProviderHttp.ReadBytesAsync(stream, bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                count = decoder.GetChars(bytes, 0, read, characters, 0, read == 0);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or DecoderFallbackException)
            { throw new ModelException("The newline-delimited provider stream could not be read.", error); }
            for (var index = 0; index < count; index++)
            {
                var value = characters[index];
                if (first) { first = false; if (value == '\uFEFF') continue; }
                if (value == '\n')
                {
                    if (line.Length > 0 && line[line.Length - 1] == '\r') line.Length--;
                    if (line.Length > 0) yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    if (line.Length >= maximumCharacters) throw new ModelException("A newline-delimited provider event exceeded the size limit.");
                    line.Append(value);
                }
            }
            if (read != 0) continue;
            if (line.Length > 0) yield return line.ToString();
            yield break;
        }
    }
}
