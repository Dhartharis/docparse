using System.Security.Cryptography;
using System.Text;
using DocParse.Modules.OperationalData.Contracts;
using DocParse.Modules.Access.Contracts;

namespace DocParse.Modules.Integrations.Application;

public sealed class GoogleDriveSessions(IGoogleDriveAccounts accounts, IGoogleDriveProvider provider, TimeProvider clock, IOperationalProjection projection,IOrganizationAccess access)
{
    private static string Random() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public async Task<GoogleAuthorization> StartAsync(Guid org, Guid user, CancellationToken ct)
    {
        var state=Random(); var verifier=Random(); var id=Guid.NewGuid();
        var challenge=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
        await accounts.StartAsync(org,user,id,state,verifier,ct);
        return new(id,state,provider.AuthorizationUrl(state,challenge));
    }
    public async Task<Guid> CompleteAsync(Guid org, Guid user, string state, string code, CancellationToken ct)
    {
        if(code.Length is <1 or >4096 || code.Any(char.IsControl))throw new GoogleDriveFailure("google_authorization");
        var claim=await accounts.ClaimAsync(org,user,state,ct);
        if(claim.Completed)return claim.AccountId;
        var tokens=await provider.ExchangeAsync(code,claim.Verifier!,ct);
        if(!await access.HasAccessAsync(org,true,ct))throw new GoogleDriveFailure("google_authorization");
        await accounts.CompleteAsync(org,user,state,claim.AccountId,tokens,ct);
        return claim.AccountId;
    }
    private async Task<(GoogleTokens Tokens,Guid Revision)> TokensAsync(Guid org,Guid user,Guid account,CancellationToken ct)
    {
        var lease=await accounts.AcquireAsync(org,user,account,ct);
        if(lease.LeaseId is null)return(lease.Tokens,lease.Revision);
        GoogleTokens renewed;
        try{renewed=await provider.RefreshAsync(lease.Tokens,ct);}
        catch
        {
            // An interrupted/uncertain exchange is not blindly replayed. A fresh authorization recovers it.
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await accounts.SaveRefreshAsync(org,user,account,lease,null,cleanup.Token);
            throw;
        }
        if(!await accounts.SaveRefreshAsync(org,user,account,lease,renewed,ct))throw new GoogleDriveFailure("google_authorization");
        return(renewed,lease.Revision);
    }
    public async Task<string> PickerTokenAsync(Guid org,Guid user,Guid account,CancellationToken ct)
    {
        var result=await TokensAsync(org,user,account,ct);
        if(!await accounts.IsCurrentAsync(org,user,account,result.Revision,ct))throw new GoogleDriveFailure("google_authorization");
        return result.Tokens.AccessToken;
    }
    public async Task<(GoogleFile File,CatalogInspection Inspection,CatalogPreview? Preview)> PreviewAsync(Guid org,Guid user,Guid account,
        string fileId,string? sheet,int headerRow,CatalogMapping? mapping,CancellationToken ct)
    {
        var result=await TokensAsync(org,user,account,ct);
        var catalog=await provider.ReadAsync(result.Tokens.AccessToken,fileId,sheet,ct);
        CatalogInspection inspection;
        try{inspection=SpreadsheetCatalog.Inspect(catalog.Table,headerRow);}
        catch(CatalogFailure error)when(error.Code=="catalog_headers"){inspection=new(catalog.Table.Sheet,catalog.Table.Sheets,[],0,false);}
        var preview=mapping is null?null:SpreadsheetCatalog.Map(catalog.Table,mapping,clock.GetUtcNow(),catalog.File.ModifiedAt,new("preview",600,60,0,null),projection);
        if(!await accounts.IsCurrentAsync(org,user,account,result.Revision,ct))throw new GoogleDriveFailure("google_authorization");
        return(catalog.File,inspection,preview);
    }
}
