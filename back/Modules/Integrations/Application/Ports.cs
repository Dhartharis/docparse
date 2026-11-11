namespace DocParse.Modules.Integrations.Application;

public sealed record PushKeyInfo(string KeyId, DateTimeOffset ExpiresAt);
public sealed record ConnectionInfo(Guid Id, string SourceBusinessId, string? BaseUrl, bool Push, bool PullLive, bool PullScheduled,
    ConnectionSettings Settings, Guid Revision, DateTimeOffset? DisconnectedAt, PushKeyInfo? PushKey, string? PullKeyId, string State);
public sealed record ConnectionCreated(ConnectionInfo Connection, string? PushSecretHex);
public sealed record PushKeyIssued(ConnectionInfo Connection, string SecretHex);

public interface IConnectionStore
{
    Task<IReadOnlyList<ConnectionInfo>> ListAsync(Guid organizationId, CancellationToken ct);
    Task<ConnectionCreated> CreateAsync(Guid organizationId, Guid requestId, ConnectionDefinition definition, CancellationToken ct);
    Task<ConnectionInfo?> UpdateAsync(Guid organizationId, Guid connectionId, Guid revision, ConnectionSettings settings, CancellationToken ct);
    Task<PushKeyIssued?> RotatePushAsync(Guid organizationId, Guid connectionId, Guid revision, int days, CancellationToken ct);
    Task<ConnectionInfo?> SetPullKeyAsync(Guid organizationId, Guid connectionId, Guid revision, string keyId, string secretHex, CancellationToken ct);
    Task<bool> DisconnectAsync(Guid organizationId, Guid connectionId, Guid revision, CancellationToken ct);
}
