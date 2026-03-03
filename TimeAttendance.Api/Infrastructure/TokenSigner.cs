using System.Security.Cryptography;
using System.Text;

namespace TimeAttendance.Api.Infrastructure;

public static class TokenSigner
{
    public static long CurrentCounter(int stepSeconds = 30)
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / stepSeconds;

    public static string Sign(byte[] sharedSecret, string deviceCode, long counter)
    {
        var payload = $"{deviceCode}|{counter}";
        using var hmac = new HMACSHA256(sharedSecret);
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Base64UrlEncode(bytes);
    }

    public static bool Verify(byte[] sharedSecret, string deviceCode, long counter, string signature)
    {
        var expected = Sign(sharedSecret, deviceCode, counter);
        return FixedTimeEquals(expected, signature);
    }

    public static bool IsCounterWithinWindow(long counter, long nowCounter, int window = 1)
        => counter >= nowCounter - window && counter <= nowCounter + window;

    private static bool FixedTimeEquals(string a, string b)
    {
        // Avoid timing attacks (good practice even for intranet).
        if (a is null || b is null) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        var s = Convert.ToBase64String(data);
        s = s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return s;
    }
}
