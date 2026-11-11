using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocParse.Modules.Integrations.Application;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class PushRejected(int status, string code) : Exception("The operational request was rejected.")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed record OperationalSignature(string KeyId, long Timestamp, Guid RequestId, string RequestIdText, string SignatureHex)
{
    public static OperationalSignature Parse(string key, string timestamp, string request, string signature, DateTimeOffset now)
    {
        if (key is null || !Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{1,128}\z") ||
            timestamp is null || !Regex.IsMatch(timestamp, @"\A(0|[1-9][0-9]{0,11})\z") ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || Math.Abs((decimal)now.ToUnixTimeSeconds() - seconds) > 300 ||
            request is null || !Regex.IsMatch(request, @"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z") ||
            !Guid.TryParseExact(request, "D", out var id) || signature is null || !Regex.IsMatch(signature, @"\Av1=[0-9a-f]{64}\z")) throw Rejected();
        return new(key, seconds, id, request, signature[3..]);
    }
    public void Verify(string secretHex, string method, string originalTarget, byte[] body)
    {
        if (originalTarget.Length == 0 || originalTarget[0] != '/' || originalTarget.Any(c => c is < '!' or > '~') || originalTarget.Contains('#')) throw Rejected();
        var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = string.Join("\n", "FYR-OPDATA-V1", method, originalTarget, KeyId, Timestamp.ToString(CultureInfo.InvariantCulture), RequestIdText, digest);
        try
        {
            var expected = HMACSHA256.HashData(Convert.FromHexString(secretHex), Encoding.UTF8.GetBytes(canonical));
            if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(SignatureHex))) throw Rejected();
        }
        catch (FormatException) { throw Rejected(); }
    }
    // Keep the nonce through the entire inclusive timestamp-second window.
    public DateTimeOffset NonceExpiry => DateTimeOffset.FromUnixTimeSeconds(Timestamp + 301);
    private static PushRejected Rejected() => new(401, "invalid_credentials");
}
