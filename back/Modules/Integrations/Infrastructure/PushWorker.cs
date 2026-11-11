using System.Text;
using DocParse.Modules.OperationalData.Contracts;
using Npgsql;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class PushWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            var inbox = scope.ServiceProvider.GetRequiredService<PushInbox>();
            Application.PushWork? work = null;
            try
            {
                work = await inbox.ClaimAsync(stoppingToken);
                if (work is not null)
                {
                    var message = OperationalEventCodec.Parse(Encoding.UTF8.GetBytes(work.Payload));
                    var projection = scope.ServiceProvider.GetRequiredService<IOperationalProjection>();
                    await inbox.ApplyAsync(work, message.Change, projection, clock.GetUtcNow(), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Operational Push processing failed ({FailureType}).", error.GetType().Name);
                if (work is not null)
                {
                    try
                    {
                        if (error is NpgsqlException { IsTransient: true } || error is TimeoutException)
                            await inbox.RetryAsync(work, stoppingToken);
                        else await inbox.FinishAsync(work, "failed", "processing_failed", stoppingToken);
                    }
                    catch (Exception saveError) when (!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning("Operational Push recovery persistence failed ({FailureType}).", saveError.GetType().Name); }
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}
