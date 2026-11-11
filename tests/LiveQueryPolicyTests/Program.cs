using System.Net;
using System.Net.Sockets;
using System.Text;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Contracts;
using DocParse.Modules.Integrations.Infrastructure;
using DocParse.Modules.OperationalData.Application;
using DocParse.Modules.OperationalData.Contracts;
using DocParse.Modules.OperationalData.Infrastructure;
using Microsoft.Extensions.Configuration;

var count=0;
void Check(bool value) { if (!value) throw new Exception("Live query policy assertion failed."); count++; }
void Reject(Action action) { try { action(); } catch(Exception error) when(error is LiveQueryFailure or OperationalDataInvalid) { count++; return; } throw new Exception("Expected rejection."); }
foreach(var ip in new[] {"0.0.0.0","10.0.0.1","100.64.0.1","127.0.0.1","169.254.169.254","168.63.129.16","192.0.2.1","172.16.0.1","192.168.1.1","198.18.0.1","198.51.100.1","203.0.113.1","224.0.0.1","255.255.255.255","::1","::","fe80::1","fc00::1","ff02::1","::ffff:127.0.0.1","64:ff9b::7f00:1","2001:db8::1","2001::1","2002:7f00::1"}) Check(!ProviderDestination.IsPublic(IPAddress.Parse(ip)));
foreach(var ip in new[] {"8.8.8.8","1.1.1.1","2606:4700:4700::1111","2001:4860:4860::8888"}) Check(ProviderDestination.IsPublic(IPAddress.Parse(ip)));
var query=new LiveQuery(["catalog","price","availability"],"externalIds",["offer-1"]);
LiveQueryRules.Validate(query);count++;
foreach(var invalid in new[] {query with{Fields=[]},query with{Fields=["price","price"]},query with{Fields=["secret"]},query with{Values=[]},query with{Values=["id\n"]},query with{Values=["a","a"]},query with{SelectorKind="url"},query with{SelectorKind="text",Values=["a","b"]},query with{Values=new string[51]}}) Reject(()=>LiveQueryRules.Validate(invalid));
var now=DateTimeOffset.Parse("2026-11-11T12:00:00Z");
var offer=new OperationalOffer("offer-1","p",null,"Milk","Carton","L","12",now,true,new(true,null,"1.00","USD","inclusive",now,now.AddMinutes(1)),new("tracked",new(true,null,"8"),new(false,"not_provided",null),now,now.AddMinutes(1)));
LiveEvidence.Check(offer,"12",false,offer with{Price=offer.Price with{ObservedAt=now.AddMinutes(-1)}});count++;
Reject(()=>LiveEvidence.Check(offer,"13",false,null));
Reject(()=>LiveEvidence.Check(offer,"12",true,null));
Reject(()=>LiveEvidence.Check(offer,"12",false,offer with{Name="Other"}));
Reject(()=>LiveEvidence.Check(offer,"12",false,offer with{Price=offer.Price with{Amount="1.01"}}));
LiveEvidence.Check(offer with{Version="99999999999999999999999999999999999999"},"12",true,null);count++;
Check(LiveEvidence.View(offer,600,60,now).PriceCurrent);
Check(!LiveEvidence.View(offer,600,60,now.AddMinutes(1)).AvailabilityCurrent);
var json="""
{"contractVersion":"1","sourceBusinessId":"biz","results":[{"selectorValue":"offer-1","status":"found","offers":[{"externalId":"offer-1","productId":"p","sku":null,"name":"Milk","presentation":"Carton","unitCode":"L","version":"12","updatedAt":"2026-11-11T12:00:00Z","active":true,"price":{"state":"known","amount":"1.00","currency":"USD","taxTreatment":"inclusive","observedAt":"2026-11-11T12:00:00Z","validUntil":"2026-11-11T12:01:00Z"},"availability":{"tracking":"tracked","available":{"state":"known","quantity":"8"},"reserved":{"state":"unknown","reason":"not_provided"},"observedAt":"2026-11-11T12:00:00Z","validUntil":"2026-11-11T12:01:00Z"}}]}]}
""";
var projection=new OperationalProjection(new ConfigurationBuilder().Build());
Check(LiveResponseCodec.Parse(Encoding.UTF8.GetBytes(json),"biz",query,projection,now,0)[0].Offers[0].Price.Amount=="1.00");
foreach(var invalid in new[] {json.Replace("\"sourceBusinessId\":\"biz\"","\"sourceBusinessId\":\"other\""),json.Replace("\"found\"","\"ambiguous\""),json.Replace("\"selectorValue\":\"offer-1\"","\"selectorValue\":\"other\""),json.Replace("\"version\":\"12\"","\"version\":\"12\",\"version\":\"12\""),json.Replace("\"amount\":\"1.00\"","\"amount\":1"),json.Replace("\"results\":","\"extra\":true,\"results\":")}) Reject(()=>LiveResponseCodec.Parse(Encoding.UTF8.GetBytes(invalid),"biz",query,projection,now,0));
Reject(()=>LiveResponseCodec.Parse(Encoding.UTF8.GetBytes(json),"biz",query with{Fields=["catalog"]},projection,now,0));
// Exercise the actual socket callback: even an approved HTTPS origin cannot dial loopback in Production.
var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
try {
    var port=((IPEndPoint)listener.LocalEndpoint).Port;
    var transport=new ProviderTransport(new(false,new HashSet<string>{$"https://127.0.0.1:{port}"}),TimeProvider.System);
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try { await transport.QueryAsync(new Uri($"https://127.0.0.1:{port}/query"),"key",new string('a',64),[],timeout.Token); throw new Exception("Expected blocked destination."); }
    catch(HttpRequestException error) { Check(error.InnerException is LiveQueryFailure {Code:"connection_origin"}); }
    Check(!listener.Pending());
} finally {listener.Stop();}
Console.WriteLine($"PASS: {count} live-query policy checks: bounded selectors/fields, strict responses, exact values, freshness, revision/tombstone evidence and real socket destination blocking.");
