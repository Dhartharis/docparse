using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocParse.Modules.Access;

internal static class EmailTemplates {
 private static readonly IReadOnlyDictionary<string,Dictionary<string,string>> Catalogs=new[]{"en","es","vi"}.ToDictionary(locale=>locale,locale=>{
  using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("DocParse.Modules.Access.Email."+locale+".json")!;return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
 });
 public static string Locale(string? locale)=>locale is "es" or "vi"?locale:"en";
 public static (string Subject,string Body) Render(string kind,string? language,IReadOnlyDictionary<string,string> values){
  var locale=Locale(language);string Text(string key){var value=Catalogs[locale].GetValueOrDefault(key)??Catalogs["en"][key];foreach(var pair in values)value=value.Replace("{"+pair.Key+"}",HtmlEncoder.Default.Encode(pair.Value));return value;}
  var title=Text(kind+".title");var content="<p>"+Text(kind+".text")+"</p>";
  if(values.TryGetValue("link",out var link))content+="<p><a style='display:inline-block;background:#7441ec;color:white;text-decoration:none;padding:13px 22px;border-radius:24px' href=\""+HtmlEncoder.Default.Encode(link)+"\">"+Text(kind+".action")+"</a></p>";
  if(Catalogs["en"].ContainsKey(kind+".code"))content+="<p style='overflow-wrap:anywhere;font-size:12px;color:#aaa8af'>"+Text(kind+".code")+"</p>";
  return(Text(kind+".subject"),"<!doctype html><html lang='"+locale+"'><body style='margin:0;background:#08070b;color:#f6f5f7;font:15px/1.7 Arial,sans-serif'><div style='max-width:520px;margin:32px auto;padding:28px;border:1px solid #30263e;border-radius:18px'><p style='color:#ffe000;letter-spacing:2px'>DOCPARSE</p><h1 style='font-size:26px;font-weight:400'>"+title+"</h1>"+content+"<p style='font-size:11px;color:#aaa8af;margin-top:30px'>"+Text("footer")+"</p></div></body></html>");
 }
}
