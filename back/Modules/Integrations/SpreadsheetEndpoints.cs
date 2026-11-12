using System.Text.Json;
using DocParse.Modules.Access.Contracts;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Infrastructure;
using DocParse.Modules.OperationalData.Contracts;
using Microsoft.AspNetCore.Http.Features;

namespace DocParse.Modules.Integrations;

internal static class SpreadsheetEndpoints
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static void MapSpreadsheetEndpoints(this WebApplication app)
    {
        var group=app.MapGroup("/api/organizations/{organizationId:guid}/spreadsheet-catalog").RequireAuthorization().RequireRateLimiting("authentication");
        group.AddEndpointFilter(async(context,next)=>
        {
            context.HttpContext.Response.Headers.CacheControl="no-store";
            var org=Guid.Parse(context.HttpContext.Request.RouteValues["organizationId"]!.ToString()!);
            if(!await context.HttpContext.RequestServices.GetRequiredService<IOrganizationAccess>().HasAccessAsync(org,true,context.HttpContext.RequestAborted))return Results.NotFound();
            try{return await next(context);}
            catch(CatalogFailure error){return Results.Problem(statusCode:400,title:"The spreadsheet preview could not be completed.",extensions:new Dictionary<string,object?>{["code"]=error.Code});}
            catch(BadHttpRequestException error)when(error.StatusCode==413){return Results.Problem(statusCode:413,title:"The spreadsheet is too large.",extensions:new Dictionary<string,object?>{["code"]="catalog_limit"});}
            catch(OperationCanceledException)when(!context.HttpContext.RequestAborted.IsCancellationRequested){return Results.Problem(statusCode:400,title:"The spreadsheet parsing limit was exceeded.",extensions:new Dictionary<string,object?>{["code"]="catalog_limit"});}
            catch(InvalidDataException){return Results.Problem(statusCode:400,title:"The spreadsheet could not be read.",extensions:new Dictionary<string,object?>{["code"]="catalog_file"});}
        });
        group.MapPost("/inspect",async(HttpContext context)=>
        {
            var(table,form)=await ReadAsync(context);
            var header=form["headerRow"].ToString();var row=1;
            if(header.Length>0&&!int.TryParse(header,out row))throw new CatalogFailure("catalog_headers");
            try{return Results.Ok(SpreadsheetCatalog.Inspect(table,row));}
            catch(CatalogFailure error)when(error.Code=="catalog_headers"){return Results.Ok(new CatalogInspection(table.Sheet,table.Sheets,[],0,false));}
        });
        group.MapPost("/preview",async(HttpContext context,IOperationalProjection projection,TimeProvider clock)=>
        {
            var(table,form)=await ReadAsync(context);
            CatalogMapping? mapping;
            try{mapping=JsonSerializer.Deserialize<CatalogMapping>(form["mapping"].ToString(),Json);}
            catch(JsonException){throw new CatalogFailure("catalog_mapping");}
            if(mapping is null)throw new CatalogFailure("catalog_mapping");
            var now=clock.GetUtcNow();
            return Results.Ok(SpreadsheetCatalog.Map(table,mapping,now,now,new("preview",600,60,0,null),projection));
        });
    }
    private static async Task<(CatalogTable Table,IFormCollection Form)> ReadAsync(HttpContext context)
    {
        if(!context.Request.HasFormContentType||context.Request.ContentType?.StartsWith("multipart/form-data",StringComparison.OrdinalIgnoreCase)!=true)throw new CatalogFailure("catalog_file");
        var feature=context.Features.Get<IHttpMaxRequestBodySizeFeature>();if(feature is{IsReadOnly:false})feature.MaxRequestBodySize=SpreadsheetReader.MaximumFileBytes+65536;
        var form=await context.Request.ReadFormAsync(new FormOptions{MultipartBodyLengthLimit=SpreadsheetReader.MaximumFileBytes,ValueCountLimit=16,ValueLengthLimit=16384,MemoryBufferThreshold=SpreadsheetReader.MaximumFileBytes+65536},context.RequestAborted);
        if(form.Files.Count!=1||form.Files[0].Length is <=0 or >SpreadsheetReader.MaximumFileBytes||!string.Equals(Path.GetExtension(form.Files[0].FileName),".xlsx",StringComparison.OrdinalIgnoreCase))throw new CatalogFailure("catalog_file");
        using var bytes=new MemoryStream();await form.Files[0].CopyToAsync(bytes,context.RequestAborted);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var selected=form["sheet"].ToString();return(SpreadsheetReader.Xlsx(bytes.ToArray(),selected.Length==0?null:selected,deadline.Token),form);
    }
}
