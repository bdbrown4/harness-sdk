using System.Globalization;
using System.Security.Cryptography;

namespace Strands.Agents.SessionCatalogs;

// RFC9562 method2: monotonic 12-bit counter, borrowing a millisecond on exhaustion.
internal static class SnapshotSessionIds
{
    private static readonly object Gate = new();
    private static long _timestamp = -1;
    private static int _counter;
    internal static string New()
    {
        var random = new byte[10];
        using (var generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now > _timestamp) { _timestamp = now; _counter = ((random[0] << 8) | random[1]) & 0x7ff; }
            else if (++_counter > 0xfff) { _timestamp++; _counter = ((random[0] << 8) | random[1]) & 0x7ff; }
            var time = _timestamp.ToString("x12", CultureInfo.InvariantCulture);
            var tail = string.Concat(random.Skip(2).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            return time.Substring(0, 8) + "-" + time.Substring(8) + "-7" + _counter.ToString("x3", CultureInfo.InvariantCulture) + "-" +
                ((random[2] & 0x3f) | 0x80).ToString("x2", CultureInfo.InvariantCulture) + tail.Substring(2, 2) + "-" + tail.Substring(4);
        }
    }
    internal static bool IsValid(string value)
    {
        if (value.Length != 36 || value[8] != '-' || value[13] != '-' || value[18] != '-' || value[23] != '-' || value[14] != '7' || value[19] is not ('8' or '9' or 'a' or 'b')) return false;
        for (var index = 0; index < value.Length; index++)
            if (index is not (8 or 13 or 18 or 23) && value[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
    internal static void Validate(string value) { if (!IsValid(value)) throw new ArgumentException("Snapshot handles must be canonical lowercase UUIDv7 values.", nameof(value)); }
}
