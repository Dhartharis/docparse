using System.Security.Cryptography;
using System.Text.Json;
using DocParse.Modules.Bots.Infrastructure;

namespace DocParse.Modules.Bots.Endpoints;

internal static class WidgetEndpoints
{
    public static void MapWidget(this WebApplication app)
    {
        app.MapGet("/api/embed/{botId:guid}/loader.js", (Guid botId,IConfiguration config,HttpContext context)=> {
            var api=(config["PublicApi:BaseUrl"]??"http://127.0.0.1:5128").TrimEnd('/');
            context.Response.Headers.CacheControl="no-store";
            var frame=JsonSerializer.Serialize(api+"/api/embed/"+botId+"/frame");
            return Results.Text("(()=>{const s=document.currentScript;if(!s||s.dataset.loaded)return;s.dataset.loaded='true';const b=document.createElement('button');b.type='button';b.textContent='✦';b.setAttribute('aria-label','DocParse');b.setAttribute('aria-expanded','false');Object.assign(b.style,{position:'fixed',right:'20px',bottom:'20px',width:'52px',height:'52px',borderRadius:'50%',border:'1px solid #b77aef',background:'#7441ec',color:'white',fontSize:'24px',cursor:'pointer',zIndex:'2147483000'});let f;b.onclick=()=>{if(!f){f=document.createElement('iframe');f.src="+frame+";f.title='DocParse';f.referrerPolicy='strict-origin-when-cross-origin';Object.assign(f.style,{position:'fixed',right:'16px',bottom:'84px',width:'min(420px,calc(100vw - 32px))',height:'min(660px,calc(100dvh - 104px))',border:'1px solid #ffffff30',borderRadius:'16px',zIndex:'2147482999',background:'#0c0b0f'});document.body.append(f);}else f.hidden=!f.hidden;b.setAttribute('aria-expanded',String(!f.hidden));};document.body.append(b);})();","application/javascript");
        });
        app.MapGet("/api/embed/{botId:guid}/frame", async(Guid botId,HttpContext context,BotChannelStore channels,BotLogoStorage logos,IConfiguration config,CancellationToken ct)=> {
            if(!Uri.TryCreate(context.Request.Headers.Referer.ToString(),UriKind.Absolute,out var referer))return Results.NotFound();
            var session=await channels.CreateSessionAsync(botId,referer.GetLeftPart(UriPartial.Authority),ct);if(session is null)return Results.NotFound();
            var root=config["Widget:AssetsPath"]??Path.Combine(app.Environment.ContentRootPath,"..","front","dist");
            var assets=Path.Combine(root,"assets");if(!Directory.Exists(assets))return Results.StatusCode(503);
            var js=Directory.GetFiles(assets,"index-*.js").Order().LastOrDefault();var css=Directory.GetFiles(assets,"index-*.css").Order().LastOrDefault();if(js is null||css is null)return Results.StatusCode(503);
            var nonce=Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            context.Response.Headers.ContentSecurityPolicy="default-src 'none'; script-src 'self' 'nonce-"+nonce+"'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; connect-src 'self'; frame-ancestors "+string.Join(" ",session.Value.Origins)+"; base-uri 'none'; form-action 'none'; object-src 'none'";
            context.Response.Headers.CacheControl="no-store";context.Response.Headers["Referrer-Policy"]="no-referrer";context.Response.Headers["X-Content-Type-Options"]="nosniff";
            string? logoDataUrl=null;
            if(session.Value.Bot.Branding.LogoVersion is Guid version){await using var logo=await logos.DownloadAsync(botId,version,ct);using var bytes=new MemoryStream();await logo.CopyToAsync(bytes,ct);logoDataUrl="data:image/png;base64,"+Convert.ToBase64String(bytes.ToArray());}
            var bootstrap=JsonSerializer.Serialize(new {logoDataUrl,organizationId=session.Value.Bot.Bot.OrganizationId,bot=session.Value.Bot.Branding,token=session.Value.Token},new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Results.Content("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>DocParse</title><link rel='stylesheet' href='/assets/"+Path.GetFileName(css)+"'></head><body class='embedded-body'><div id='root'></div><script nonce='"+nonce+"'>window.__DOCPARSE_EMBED__="+bootstrap+";</script><script type='module' nonce='"+nonce+"' src='/assets/"+Path.GetFileName(js)+"'></script></body></html>","text/html");
        });

    }
}
