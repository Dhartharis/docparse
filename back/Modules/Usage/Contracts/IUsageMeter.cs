namespace DocParse.Modules.Usage.Contracts;

public sealed record UsageBound(string Provider,string Model,long Input,long Output);
public interface IUsageOperation { Task CompleteAsync(bool success,CancellationToken ct); }
public interface IUsageMeter {
 void ValidateProcessingProvider();
 Task<IUsageOperation> StartAsync(Guid organizationId,Guid? botId,string feature,string channel,long maximumEmbeddingInput,int maximumLlmInput,int maximumOutput,CancellationToken ct);
}
public interface IUsageMeasurements {
 Task RecordAsync(string provider,string model,long? input,long? output,CancellationToken ct);
}
public sealed class UsageLimitReached:Exception;
public sealed class UsageServiceUnavailable:Exception;
