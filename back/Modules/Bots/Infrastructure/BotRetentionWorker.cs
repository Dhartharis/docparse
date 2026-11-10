using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocParse.Modules.Bots.Infrastructure;

internal sealed class BotRetentionWorker(IServiceScopeFactory scopes,IConfiguration config,ILogger<BotRetentionWorker> logger):BackgroundService
{
 private async Task<NpgsqlConnection> Open(CancellationToken ct){var db=new NpgsqlConnection(config["PostgreSQL:ConnectionString"]);await db.OpenAsync(ct);return db;}
 protected override async Task ExecuteAsync(CancellationToken ct){var lastSweep=DateTime.MinValue;while(!ct.IsCancellationRequested){try{
  await using var scope=scopes.CreateAsyncScope();var context=scope.ServiceProvider.GetRequiredService<BotsDbContext>();var logos=scope.ServiceProvider.GetRequiredService<BotLogoStorage>();
  await Purge(ct);await Cleanup(context,logos,ct);if(DateTime.UtcNow-lastSweep>TimeSpan.FromHours(1)){lastSweep=DateTime.UtcNow;await logos.SweepOrphansAsync(context,ct);}await Task.Delay(1000,ct);
 }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception e){logger.LogWarning("Bot retention iteration failed: {FailureType}",e.GetType().Name);await Task.Delay(5000,ct);}}}
 private async Task Purge(CancellationToken ct){await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);await using var select=new NpgsqlCommand("SELECT id FROM bots.bots WHERE deleted_at<=now()-interval '30 days' ORDER BY deleted_at FOR UPDATE SKIP LOCKED LIMIT 1",db);var id=await select.ExecuteScalarAsync(ct);if(id is Guid bot){await using var cmd=new NpgsqlCommand("INSERT INTO bots.logo_cleanup(blob_key,due_at) VALUES(@key,now()) ON CONFLICT(blob_key) DO UPDATE SET due_at=now();DELETE FROM bots.bots WHERE id=@id",db);cmd.Parameters.AddWithValue("key",bot.ToString("N")+"/");cmd.Parameters.AddWithValue("id",bot);await cmd.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);}
 private async Task Cleanup(BotsDbContext context,BotLogoStorage logos,CancellationToken ct){string key;var lease=Guid.NewGuid();await using(var db=await Open(ct)){await using var claim=new NpgsqlCommand("UPDATE bots.logo_cleanup SET lease_id=@lease,lease_until=now()+interval '2 minutes',attempts=attempts+1 WHERE blob_key=(SELECT blob_key FROM bots.logo_cleanup WHERE due_at<=now() AND(lease_until IS NULL OR lease_until<now()) ORDER BY due_at FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING blob_key",db);claim.Parameters.AddWithValue("lease",lease);var value=await claim.ExecuteScalarAsync(ct)as string;if(value is null)return;key=value;}
  try{if(!await logos.IsReferencedAsync(context,key,ct))await logos.DeleteKeyAsync(key,ct);await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM bots.logo_cleanup WHERE blob_key={key} AND lease_id={lease}",ct);}
  catch{await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE bots.logo_cleanup SET lease_id=NULL,lease_until=NULL,due_at=now()+make_interval(secs=>LEAST(300,5*attempts)) WHERE blob_key={key} AND lease_id={lease}",ct);throw;}
 }
}
