using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocParse.Modules.Integrations.Application;

namespace DocParse.Modules.Integrations.Infrastructure;

public sealed class GoogleDriveProvider(GoogleDriveOptions options,TimeProvider clock):IGoogleDriveProvider,IDisposable
{
    public const string SheetMime="application/vnd.google-apps.spreadsheet",ExcelMime="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly HttpClient client=new(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(5)}){Timeout=TimeSpan.FromSeconds(10)};
    public void Dispose()=>client.Dispose();
    private static string Query(IEnumerable<KeyValuePair<string,string>> values)=>string.Join('&',values.Select(value=>Uri.EscapeDataString(value.Key)+"="+Uri.EscapeDataString(value.Value)));
    public string AuthorizationUrl(string state,string challenge)=>options.AuthorizationEndpoint+"?"+Query(new Dictionary<string,string>{["client_id"]=options.ClientId,["redirect_uri"]=options.RedirectUri,["response_type"]="code",["scope"]=GoogleDriveOptions.Scope,["access_type"]="offline",["prompt"]="consent select_account",["include_granted_scopes"]="false",["state"]=state,["code_challenge"]=challenge,["code_challenge_method"]="S256"});
    private async Task<byte[]> SendAsync(HttpRequestMessage request,int maximum,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));var bounded=deadline.Token;
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,bounded);
            if(!response.IsSuccessStatusCode)
                throw new GoogleDriveFailure(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden?"google_permission":response.StatusCode==HttpStatusCode.NotFound?"google_file":response.StatusCode==HttpStatusCode.BadRequest?"google_authorization":"google_unavailable");
            if(response.Content.Headers.ContentLength>maximum||response.Content.Headers.ContentEncoding.Count>0)throw new GoogleDriveFailure("catalog_limit");
            await using var stream=await response.Content.ReadAsStreamAsync(bounded);using var bytes=new MemoryStream();var buffer=new byte[16384];int read;
            while((read=await stream.ReadAsync(buffer,bounded))>0){if(bytes.Length+read>maximum)throw new GoogleDriveFailure("catalog_limit");await bytes.WriteAsync(buffer.AsMemory(0,read),bounded);}
            return bytes.ToArray();
        }
        catch(HttpRequestException){throw new GoogleDriveFailure("google_unavailable");}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new GoogleDriveFailure("google_unavailable");}
    }
    private async Task<JsonDocument> GetAsync(string url,string token,int maximum,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        try{return JsonDocument.Parse(await SendAsync(request,maximum,ct));}
        catch(JsonException){throw new GoogleDriveFailure("google_response");}
    }
    private async Task<GoogleTokens> TokenAsync(Dictionary<string,string> form,GoogleTokens? previous,CancellationToken ct)
    {
        form["client_id"]=options.ClientId;form["client_secret"]=options.ClientSecret;
        using var request=new HttpRequestMessage(HttpMethod.Post,options.TokenEndpoint){Content=new FormUrlEncodedContent(form)};
        using var document=Parse(await SendAsync(request,65536,ct));var root=document.RootElement;
        var access=Text(root,"access_token",8192);var refresh=root.TryGetProperty("refresh_token",out _)?Text(root,"refresh_token",8192):previous?.RefreshToken;
        var scopes=root.TryGetProperty("scope",out var scope)?scope.GetString()?.Split(' ',StringSplitOptions.RemoveEmptyEntries):null;
        if(root.GetProperty("token_type").GetString()!="Bearer"||refresh is null||scopes is not null&&!scopes.Contains(GoogleDriveOptions.Scope,StringComparer.Ordinal)||previous is null&&scopes is null||
            !root.GetProperty("expires_in").TryGetInt32(out var seconds)||seconds is <61 or >86400)throw new GoogleDriveFailure("google_permission");
        var now=clock.GetUtcNow();DateTimeOffset? refreshUntil=previous?.RefreshExpiresAt;
        if(root.TryGetProperty("refresh_token_expires_in",out var duration))
        {if(!duration.TryGetInt64(out var remaining)||remaining is <1 or >315360000)throw new GoogleDriveFailure("google_response");refreshUntil=now.AddSeconds(remaining);}
        return new(access,refresh,now.AddSeconds(seconds),refreshUntil);
    }
    public async Task<GoogleTokens> ExchangeAsync(string code,string verifier,CancellationToken ct)
    {try{return await TokenAsync(new(){["grant_type"]="authorization_code",["code"]=code,["code_verifier"]=verifier,["redirect_uri"]=options.RedirectUri},null,ct);}catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException){throw new GoogleDriveFailure("google_response");}}
    public async Task<GoogleTokens> RefreshAsync(GoogleTokens tokens,CancellationToken ct)
    {try{return await TokenAsync(new(){["grant_type"]="refresh_token",["refresh_token"]=tokens.RefreshToken},tokens,ct);}catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException){throw new GoogleDriveFailure("google_response");}}
    private static JsonDocument Parse(byte[] bytes){try{return JsonDocument.Parse(bytes);}catch(JsonException){throw new GoogleDriveFailure("google_response");}}
    private static string Text(JsonElement root,string field,int maximum)
    {
        var value=root.GetProperty(field).GetString();
        if(string.IsNullOrWhiteSpace(value)||value.Length>maximum||value.Any(char.IsControl))throw new GoogleDriveFailure("google_response");return value;
    }
    private async Task<GoogleFile> FileAsync(string token,string fileId,CancellationToken ct)
    {
        using var document=await GetAsync(options.DriveEndpoint+"files/"+Uri.EscapeDataString(fileId)+"?supportsAllDrives=true&fields=id,name,mimeType,version,modifiedTime,trashed,isAppAuthorized,capabilities(canDownload),size",token,65536,ct);var root=document.RootElement;
        var mime=Text(root,"mimeType",128);var version=Text(root,"version",128);
        if(root.GetProperty("id").GetString()!=fileId||root.GetProperty("trashed").GetBoolean()||!root.GetProperty("isAppAuthorized").GetBoolean()||!root.GetProperty("capabilities").GetProperty("canDownload").GetBoolean()||
            mime is not(SheetMime or ExcelMime)||!Regex.IsMatch(version,@"\A[0-9]{1,128}\z")||!DateTimeOffset.TryParse(Text(root,"modifiedTime",64),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var modified))throw new GoogleDriveFailure("google_file");
        if(mime==ExcelMime&&root.TryGetProperty("size",out var size)&&(!long.TryParse(size.GetString(),NumberStyles.None,CultureInfo.InvariantCulture,out var length)||length>SpreadsheetReader.MaximumFileBytes))throw new GoogleDriveFailure("catalog_limit");
        return new(fileId,Text(root,"name",1024),mime,version,modified);
    }
    public async Task<GoogleCatalog> ReadAsync(string accessToken,string fileId,string? sheet,CancellationToken ct)
    {
        if(!Regex.IsMatch(fileId,@"\A[A-Za-z0-9_-]{1,256}\z"))throw new GoogleDriveFailure("google_file");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(25));var token=deadline.Token;
        try
        {
            var before=await FileAsync(accessToken,fileId,token);CatalogTable table;
            if(before.MimeType==ExcelMime)
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,options.DriveEndpoint+"files/"+Uri.EscapeDataString(fileId)+"?alt=media&supportsAllDrives=true");request.Headers.Authorization=new("Bearer",accessToken);
                table=SpreadsheetReader.Xlsx(await SendAsync(request,SpreadsheetReader.MaximumFileBytes,token),sheet,token);
            }
            else
            {
                using var metadata=await GetAsync(options.SheetsEndpoint+"spreadsheets/"+Uri.EscapeDataString(fileId)+"?fields=sheets(properties(title,sheetType))",accessToken,65536,token);
                var items=metadata.RootElement.GetProperty("sheets");if(items.GetArrayLength() is <1 or >100)throw new GoogleDriveFailure("catalog_limit");
                var sheets=items.EnumerateArray().Select(item=>item.GetProperty("properties")).Where(item=>!item.TryGetProperty("sheetType",out var kind)||kind.GetString()=="GRID").Select(item=>Text(item,"title",128)).ToArray();
                var selected=sheet??sheets.FirstOrDefault();if(selected is null||!sheets.Contains(selected,StringComparer.Ordinal))throw new GoogleDriveFailure("google_file");
                var range="'"+selected.Replace("'","''")+"'";
                using var request=new HttpRequestMessage(HttpMethod.Get,options.SheetsEndpoint+"spreadsheets/"+Uri.EscapeDataString(fileId)+"/values/"+Uri.EscapeDataString(range)+"?majorDimension=ROWS&valueRenderOption=UNFORMATTED_VALUE&dateTimeRenderOption=FORMATTED_STRING");request.Headers.Authorization=new("Bearer",accessToken);
                table=SpreadsheetReader.Sheets(await SendAsync(request,SpreadsheetReader.MaximumJsonBytes,token),selected,sheets);
            }
            var after=await FileAsync(accessToken,fileId,token);
            if(before!=after)throw new GoogleDriveFailure("google_changed");
            return new(before,table);
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new GoogleDriveFailure("google_unavailable");}
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){throw new GoogleDriveFailure("google_response");}
    }
}
