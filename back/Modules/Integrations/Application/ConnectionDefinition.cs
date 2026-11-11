using System.Text.RegularExpressions;

namespace DocParse.Modules.Integrations.Application;

public enum ConnectionFailureKind { Invalid, OriginNotAllowed, Conflict }
public sealed class ConnectionFailure(ConnectionFailureKind kind) : Exception("The connection operation could not be completed.")
{
    public ConnectionFailureKind Kind { get; } = kind;
}

public sealed record ProviderPolicy(bool Development, IReadOnlySet<string> AllowedOrigins);
public sealed record ConnectionSettings(string Name, int PriceMaxAgeSeconds, int StockMaxAgeSeconds, int ClockSkewSeconds, int? SyncIntervalSeconds)
{
    public ConnectionSettings Validate(bool scheduled)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Any(char.IsControl) || Name.Trim().Length > 100 || PriceMaxAgeSeconds is < 1 or > 604800 ||
            StockMaxAgeSeconds is < 1 or > 86400 || ClockSkewSeconds is < 0 or > 300 ||
            (scheduled ? SyncIntervalSeconds is null or < 60 or > 86400 : SyncIntervalSeconds is not null))
            throw new ConnectionFailure(ConnectionFailureKind.Invalid);
        return this with { Name = Name.Trim() };
    }
}

public sealed record ConnectionDefinition(string SourceBusinessId, string? BaseUrl, bool Push, bool PullLive, bool PullScheduled,
    int PushKeyDays, ConnectionSettings Settings)
{
    public ConnectionDefinition Validate(ProviderPolicy policy)
    {
        if (SourceBusinessId is null || !Regex.IsMatch(SourceBusinessId, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z") ||
            !(Push || PullLive || PullScheduled) || (Push ? PushKeyDays is < 1 or > 365 : PushKeyDays != 0) || Settings is null)
            throw new ConnectionFailure(ConnectionFailureKind.Invalid);
        var settings = Settings.Validate(PullScheduled);
        if (!PullLive && !PullScheduled)
        {
            if (!string.IsNullOrEmpty(BaseUrl)) throw new ConnectionFailure(ConnectionFailureKind.Invalid);
            return this with { BaseUrl = null, Settings = settings };
        }
        if (BaseUrl is null || BaseUrl.Any(char.IsControl) || !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(policy.Development && uri.Scheme == "http" && (uri.IsLoopback || uri.Host == "host.docker.internal"))))
            throw new ConnectionFailure(ConnectionFailureKind.Invalid);
        if (!policy.AllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority))) throw new ConnectionFailure(ConnectionFailureKind.OriginNotAllowed);
        return this with { BaseUrl = uri.AbsoluteUri.TrimEnd('/') + "/", Settings = settings };
    }

    public static string ValidateKey(string keyId, string secretHex)
    {
        if (keyId is null || !Regex.IsMatch(keyId, @"\A[A-Za-z0-9._-]{1,128}\z") ||
            secretHex is null || !Regex.IsMatch(secretHex, @"\A[0-9A-Fa-f]{64}\z"))
            throw new ConnectionFailure(ConnectionFailureKind.Invalid);
        return secretHex.ToLowerInvariant();
    }
}
