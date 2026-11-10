using DocParse.Modules.Usage.Contracts;

namespace DocParse.Modules.Usage.Infrastructure;

internal sealed class UsageOutboxWorker(IServiceScopeFactory scopes,IConfiguration config,ILogger<UsageOutboxWorker> logger):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken stoppingToken){if(!config.GetValue<bool>("FyrUsage:Enabled"))return;
  while(!stoppingToken.IsCancellationRequested){
   try{await using var scope=scopes.CreateAsyncScope();var ledger=scope.ServiceProvider.GetRequiredService<UsageLedger>();var work=await ledger.Claim(stoppingToken);if(work is null){await Task.Delay(1000,stoppingToken);continue;}
    try{var client=scope.ServiceProvider.GetRequiredService<FyrUsageClient>();await client.Reserve(work.Id,work.OrganizationId,work.BotId,work.Feature,work.Maximum,stoppingToken);await client.Complete(work.Id,work.Success,work.Meters,stoppingToken);await ledger.Resolve(work,true,null,stoppingToken);}
    catch(UsageLimitReached)when(!work.Success&&work.Meters.Length==0){
     // Existing reservations replay before quota admission, so this denial holds no remote allowance.
     await ledger.Resolve(work,true,null,stoppingToken);
    }
    catch(Exception e)when(!stoppingToken.IsCancellationRequested){logger.LogWarning("Usage synchronization failed for operation {OperationId}: {FailureType}",work.Id,e.GetType().Name);await ledger.Resolve(work,false,e is UsageLimitReached?"usage_conflict":"usage_unavailable",stoppingToken);}
   }catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}catch(Exception e){logger.LogWarning("Usage worker iteration failed: {FailureType}",e.GetType().Name);await Task.Delay(5000,stoppingToken);}
  }
 }
}
