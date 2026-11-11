using DocParse.Modules.Integrations.Contracts;
using DocParse.Modules.OperationalData.Contracts;

namespace DocParse.Modules.Integrations.Application;

public sealed record VerifiedPushKey(OperationalSource Source, Guid CredentialId, string KeyId, string SecretHex);
public sealed record PushEvent(Guid EventId, string SourceBusinessId, DateTimeOffset OccurredAt, OperationalChange Change, string CanonicalPayload);
public sealed record PushReceipt(Guid ReceiptId, Guid EventId, string Status, DateTimeOffset ReceivedAt, string? ErrorCode);
public sealed record PushWork(Guid ReceiptId, Guid OrganizationId, Guid ConnectionId, Guid LeaseId, int Attempts, string Payload, int ClockSkewSeconds);
