namespace DocParse.Modules.Integrations.Application;

public sealed class GoogleDriveFailure(string code) : Exception("The Google Drive operation could not be completed.")
{ public string Code { get; } = code; }
public sealed record GoogleTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, DateTimeOffset? RefreshExpiresAt);
public sealed record GoogleAccount(Guid Id, DateTimeOffset CreatedAt, bool NeedsReconnect);
public sealed record GoogleAuthorization(Guid Id, string State, string AuthorizationUrl);
public sealed record GoogleFile(string Id, string Name, string MimeType, string Version, DateTimeOffset ModifiedAt);
public sealed record GoogleCatalog(GoogleFile File, CatalogTable Table);
public interface IGoogleDriveProvider
{
    string AuthorizationUrl(string state, string challenge);
    Task<GoogleTokens> ExchangeAsync(string code, string verifier, CancellationToken ct);
    Task<GoogleTokens> RefreshAsync(GoogleTokens tokens, CancellationToken ct);
    Task<GoogleCatalog> ReadAsync(string accessToken, string fileId, string? sheet, CancellationToken ct);
}
public sealed record GoogleAuthorizationClaim(Guid AccountId, string? Verifier, bool Completed);
public sealed record GoogleTokenLease(Guid Revision, Guid? LeaseId, GoogleTokens Tokens);
public interface IGoogleDriveAccounts
{
    Task StartAsync(Guid org, Guid user, Guid account, string state, string verifier, CancellationToken ct);
    Task<GoogleAuthorizationClaim> ClaimAsync(Guid org, Guid user, string state, CancellationToken ct);
    Task CompleteAsync(Guid org, Guid user, string state, Guid account, GoogleTokens tokens, CancellationToken ct);
    Task<IReadOnlyList<GoogleAccount>> ListAsync(Guid org, Guid user, CancellationToken ct);
    Task<GoogleTokenLease> AcquireAsync(Guid org, Guid user, Guid account, CancellationToken ct);
    Task<bool> SaveRefreshAsync(Guid org, Guid user, Guid account, GoogleTokenLease lease, GoogleTokens? tokens, CancellationToken ct);
    Task<bool> IsCurrentAsync(Guid org, Guid user, Guid account, Guid revision, CancellationToken ct);
    Task DisconnectAsync(Guid org, Guid user, Guid account, CancellationToken ct);
}
