using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocParse.Modules.Usage.Contracts;

namespace DocParse.Modules.Usage.Infrastructure;

public sealed class FyrUsageClient(HttpClient http,IConfiguration config)
{
 private static string Encode(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
 private string Token() {
  var key=config["FyrUsage:ClientKey"]??"docparse-local";var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
  var header=Encode(JsonSerializer.SerializeToUtf8Bytes(new{alg="ES256",typ="JWT",kid=config["FyrUsage:KeyId"]??"local-v1"}));
  var payload=Encode(JsonSerializer.SerializeToUtf8Bytes(new{iss=key,sub=key,aud="fyr-usage",iat=now,exp=now+120,jti=Guid.NewGuid().ToString("N")}));
  using var signer=ECDsa.Create();signer.ImportFromPem(File.ReadAllText(config["FyrUsage:PrivateKeyPath"]??".local/usage-private.pem"));
  return header+"."+payload+"."+Encode(signer.SignData(Encoding.ASCII.GetBytes(header+"."+payload),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
 }
 public async Task<JsonElement> SendAsync(HttpMethod method,string path,object? body,CancellationToken ct) {
  using var request=new HttpRequestMessage(method,path);request.Headers.Authorization=new("Bearer",Token());if(body is not null)request.Content=JsonContent.Create(body);
  HttpResponseMessage response;
  try{response=await http.SendAsync(request,ct);}catch(HttpRequestException){throw new UsageServiceUnavailable();}
  using(response){
   if(response.StatusCode==HttpStatusCode.NotFound&&path.StartsWith("v2/credits/state"))throw new UsageCycleMissing();
   if(response.StatusCode==HttpStatusCode.Conflict){
    try{using var error=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));if(error.RootElement.TryGetProperty("code",out var code)&&code.ValueKind==JsonValueKind.String&&code.GetString()=="usage_limit_reached")throw new UsageLimitReached();}
    catch(JsonException){}
    throw new UsageServiceUnavailable();
   }
   if(!response.IsSuccessStatusCode)throw new UsageServiceUnavailable();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));return json.RootElement.Clone();
  }
 }
 public Task<JsonElement> Reserve(Guid id,Guid org,Guid? bot,string feature,IReadOnlyList<UsageBound> maximum,CancellationToken ct)=>SendAsync(HttpMethod.Post,"v2/credits/reservations",new{operationId=id.ToString(),subject=org.ToString(),feature,resourceId=bot?.ToString(),meters=maximum},ct);
 public Task<JsonElement> Complete(Guid id,bool success,IReadOnlyList<UsageBound> meters,CancellationToken ct)=>SendAsync(HttpMethod.Post,$"v2/credits/operations/{id}/complete",new{outcome=success?"success":"failure",meters},ct);
}
