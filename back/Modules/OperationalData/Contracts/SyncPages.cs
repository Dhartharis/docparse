namespace DocParse.Modules.OperationalData.Contracts;

public sealed record OperationalPage(string Mode, string? SnapshotId, string? SnapshotWatermark, bool Complete,
    string NextCursor, IReadOnlyList<OperationalChange> Changes);
