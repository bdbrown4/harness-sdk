using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Strands.Agents.Bedrock;

internal static class NativeNovaSonicEventSigner
{
    internal static byte[] Sign(string secret, string region, string previousSignature, DateTimeOffset timestamp, byte[] eventBytes)
    {
        if (eventBytes is null) throw new ArgumentNullException(nameof(eventBytes));
        var key = Utf8(secret);
        try
        {
            var result = new byte[32];
            var error = NativeNovaSonicNativeMethods.SignEvent(key, Utf8(region), Utf8(previousSignature), timestamp.ToUnixTimeMilliseconds(), eventBytes, (UIntPtr)(uint)eventBytes.Length, result);
            if (error != 0) throw new InvalidOperationException("AWS CRT event signing failed: " + Marshal.PtrToStringAnsi(NativeNovaSonicNativeMethods.ErrorName(error)));
            return result;
        }
        finally { Array.Clear(key, 0, key.Length); }
    }

    private static byte[] Utf8(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("An AWS signing field cannot contain NUL.", nameof(value));
        return Encoding.UTF8.GetBytes(value + "\0");
    }

}
