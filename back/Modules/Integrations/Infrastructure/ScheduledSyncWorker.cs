using System.Security.Cryptography;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.OperationalData.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class ScheduledSyncWorker(IServiceScopeFactory scopes,TimeProvider clock,ILogger<ScheduledSyncWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            using var scope=scopes.CreateScope();var store=scope.ServiceProvider.GetRequiredService<ScheduledSyncStore>();SyncWork? work=null;
            try
            {
                await store.EnsureAsync(stoppingToken);work=await store.ClaimAsync(stoppingToken);
                if(work is not null)
                {
                    var protection=scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
                    var secret=protection.CreateProtector("DocParse.Integrations.Credential.v1",work.OrganizationId.ToString(),work.ConnectionId.ToString(),"pull",work.KeyId).Unprotect(work.ProtectedSecret);
                    var cursorProtection=protection.CreateProtector("DocParse.Integrations.SyncCursor.v1",work.OrganizationId.ToString(),work.ConnectionId.ToString(),work.Generation.ToString(),work.CredentialId.ToString());
                    var cursor=work.ProtectedCursor is null?null:cursorProtection.Unprotect(work.ProtectedCursor);
                    var endpoint=ProviderDestination.Endpoint(work.BaseUrl,scope.ServiceProvider.GetRequiredService<ProviderPolicy>(),"changes");
                    var target=new Uri(endpoint.AbsoluteUri+"?sourceBusinessId="+Uri.EscapeDataString(work.Business)+"&pageSize=100"+(cursor is null?"":"&cursor="+Uri.EscapeDataString(cursor)));
                    var transport=scope.ServiceProvider.GetRequiredService<IProviderTransport>();
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var bytes=await transport.ChangesAsync(target,work.KeyId,secret,deadline.Token);
                    var projection=scope.ServiceProvider.GetRequiredService<IOperationalProjection>();
                    var now=clock.GetUtcNow();var page=SyncPageCodec.Parse(bytes,work.Business,projection,now,work.Skew);
                    if(page.NextCursor==cursor&&(!page.Complete||page.Changes.Count>0))throw new LiveQueryFailure("provider_response");
                    var protectedCursor=cursorProtection.Protect(page.NextCursor);
                    using var applyDeadline=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);applyDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                    await store.ApplyAsync(work,page,protectedCursor,projection,now,applyDeadline.Token);
                }
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception error)
            {
                // Never log a request target: it contains the opaque provider cursor.
                logger.LogWarning("Scheduled operational synchronization failed ({FailureType}).",error.GetType().Name);
                if(work is not null)
                {
                    var source=error as LiveQueryFailure;
                    var retryable=source?.Retryable==true||error is HttpRequestException or TimeoutException or OperationCanceledException||error is NpgsqlException{IsTransient:true};
                    var code=source?.Code??(error is CryptographicException?"connection_not_ready":error is OperationalDataInvalid?"provider_response":"sync_failed");
                    try{await store.FailAsync(work,code,retryable,source?.RetryAfterSeconds,source?.Code=="cursor_expired",stoppingToken);}
                    catch(Exception failure)when(!stoppingToken.IsCancellationRequested){logger.LogWarning("Scheduled synchronization recovery could not persist ({FailureType}).",failure.GetType().Name);}
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500),stoppingToken);
        }
    }
}
