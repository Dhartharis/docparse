using System.Text.Json;
using DocParse.Modules.Usage.Contracts;

namespace DocParse.Modules.Usage.Infrastructure;

internal sealed class UsageCycleMissing:Exception;
public sealed class UsageMeter(UsageLedger ledger,FyrUsageClient client,IConfiguration config):IUsageMeter,IUsageMeasurements
{
 private Guid? active;
 private bool Enabled=>config.GetValue<bool>("FyrUsage:Enabled");
 public void ValidateProcessingProvider(){if(Enabled&&config["AI:Provider"]!="Ollama")throw new UsageServiceUnavailable();}
 public async Task<JsonElement> State(Guid org,CancellationToken ct){
  try{return await client.SendAsync(HttpMethod.Get,"v2/credits/state?subject="+org,null,ct);}
  catch(UsageCycleMissing){var now=DateTime.UtcNow;var start=new DateTime(now.Year,now.Month,1,0,0,0,DateTimeKind.Utc);return await client.SendAsync(HttpMethod.Post,"v2/credits/entitlements/sync",new{subject=org.ToString(),planKey=await ledger.Plan(org,ct),periodStart=start,periodEnd=start.AddMonths(1)},ct);}
 }
 public async Task<IUsageOperation> StartAsync(Guid organizationId,Guid? botId,string feature,string channel,long maximumEmbeddingInput,int maximumLlmInput,int maximumOutput,CancellationToken ct){
  if(!Enabled)return new NoOperation();if(active is not null)throw new InvalidOperationException("Nested metering scope.");
  if(config["AI:Provider"]!="Ollama")throw new UsageServiceUnavailable();
  var maximum=new List<UsageBound>();if(maximumEmbeddingInput>0)maximum.Add(new("ollama",config["Ollama:EmbeddingModel"]??"qwen3-embedding:0.6b",maximumEmbeddingInput,0));if(maximumLlmInput>0)maximum.Add(new("ollama",config["Ollama:ChatModel"]??"qwen3:4b-instruct",maximumLlmInput,maximumOutput));
  var id=Guid.NewGuid();await ledger.Create(id,organizationId,botId,feature,channel,maximum.ToArray(),ct);
  var reservationAttempted=false;
  try{await State(organizationId,ct);reservationAttempted=true;await client.Reserve(id,organizationId,botId,feature,maximum,ct);await ledger.Transition(id,"running",null,ct);active=id;return new Operation(this,id);}
  catch(UsageLimitReached){await ledger.Transition(id,"denied","usage_limit",CancellationToken.None);throw;}
  catch{await ledger.Transition(id,reservationAttempted?"pending":"denied","usage_unavailable",CancellationToken.None);throw;}
 }
 public async Task RecordAsync(string provider,string model,long? input,long? output,CancellationToken ct){if(!Enabled)return;if(active is not Guid id)throw new UsageServiceUnavailable();if(input is null||output is null)throw new InvalidDataException("Provider token usage is missing.");await ledger.Record(id,provider,model,input.Value,output.Value,ct);}
 private async Task Finish(Guid id,bool success,CancellationToken ct){await ledger.Finish(id,success,ct);active=null;}
 private sealed class Operation(UsageMeter owner,Guid id):IUsageOperation{
  private bool complete;
  public async Task CompleteAsync(bool success,CancellationToken ct){if(complete)return;await owner.Finish(id,success,ct);complete=true;}
 }
 private sealed class NoOperation:IUsageOperation{public Task CompleteAsync(bool success,CancellationToken ct)=>Task.CompletedTask;}
}
