using DocParse.Modules.Knowledge.Application;

namespace DocParse.Modules.Knowledge.Infrastructure;

internal sealed class DocumentProcessingWorker(IServiceScopeFactory scopes, ILogger<DocumentProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
                var leaseId = Guid.NewGuid();
                var document = await store.ClaimAsync(leaseId, stoppingToken);
                if (document is null) { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); continue; }
                using var lease = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat = KeepLeaseAsync(document.Id, leaseId, lease);
                try { await scope.ServiceProvider.GetRequiredService<DocumentExtractionService>().ProcessAsync(document, leaseId, lease.Token); }
                catch (OperationCanceledException) when (lease.IsCancellationRequested) { /* A new worker can recover an expired lease after shutdown. */ }
                catch (Exception failure)
                {
                    var (code, retryable) = Classify(failure);
                    logger.LogWarning(failure, "Document {DocumentId} processing attempt {Attempt} failed ({Code})", document.Id, document.ProcessingAttempts, code);
                    await store.FailAsync(document.Id, leaseId, code, retryable, stoppingToken);
                }
                finally
                {
                    lease.Cancel();
                    try { await heartbeat; } catch (OperationCanceledException) when (lease.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception failure)
            {
                logger.LogError(failure, "Document queue iteration failed");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
    private async Task KeepLeaseAsync(Guid documentId, Guid leaseId, CancellationTokenSource lease)
    {
        try
        {
            while (!lease.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), lease.Token);
                await using var scope = scopes.CreateAsyncScope();
                if (!await scope.ServiceProvider.GetRequiredService<IDocumentStore>().RenewAsync(documentId, leaseId, lease.Token)) { lease.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (lease.IsCancellationRequested) { }
        catch (Exception failure) { logger.LogWarning(failure, "Document lease could not be renewed"); lease.Cancel(); }
    }
    private static (string Code, bool Retryable) Classify(Exception failure) => failure switch
    {
        DocParse.Modules.Usage.Contracts.UsageLimitReached => ("usage_limit",false),
        DocParse.Modules.Usage.Contracts.UsageServiceUnavailable => ("usage_unavailable",true),
        DocumentUploadRejected => ("invalid_document", false),
        DocumentProcessingUnavailable unavailable => ("processing_unavailable", unavailable.Retryable),
        InvalidDataException => ("invalid_result", false),
        OperationCanceledException => ("processing_timeout", true),
        HttpRequestException request => ("provider_error", request.StatusCode is null || request.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)request.StatusCode >= 500),
        Azure.RequestFailedException request => ("storage_error", request.Status is 408 or 429 or >= 500),
        Npgsql.NpgsqlException database => ("storage_error", database.IsTransient),
        _ => ("processing_error", false)
    };
}
