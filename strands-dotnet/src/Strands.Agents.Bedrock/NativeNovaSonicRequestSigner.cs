using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Strands.Agents.Bedrock;

internal static class NativeNovaSonicRequestSigner
{
    internal static (Dictionary<string, string> Headers, byte[] Signature) Sign(string access, string secret, string token,
        string region, string host, string path, DateTimeOffset timestamp)
    {
        var key = Utf8(secret);
        var session = Utf8(token);
        try
        {
            var headers = new byte[16384];
            var signature = new byte[32];
            var error = NativeNovaSonicNativeMethods.SignRequest(Utf8(access), key, session, Utf8(region), Utf8(host), Utf8(path), timestamp.ToUnixTimeMilliseconds(),
                headers, (uint)headers.Length, out var length, signature);
            if (error != 0) throw new IOException("AWS CRT request signing failed: " + Marshal.PtrToStringAnsi(NativeNovaSonicNativeMethods.ErrorName(error)));
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in Encoding.UTF8.GetString(headers, 0, (int)length).Split('\n'))
            {
                if (header.Length == 0) continue;
                var colon = header.IndexOf(':');
                parsed.Add(header.Substring(0, colon), header.Substring(colon + 1));
            }
            return (parsed, signature);
        }
        finally { Array.Clear(key, 0, key.Length); Array.Clear(session, 0, session.Length); }
    }
    private static byte[] Utf8(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("A signing field cannot contain NUL.", nameof(value));
        return Encoding.UTF8.GetBytes(value + "\0");
    }
}
