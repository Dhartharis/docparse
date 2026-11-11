using DocParse.Modules.Integrations.Application;

var policy = new ProviderPolicy(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://inventory.example.test" });
var definition = new ConnectionDefinition("business-1", "https://inventory.example.test/api", true, true, true, 90, new(" Source ", 600, 60, 0, 300));
var count = 0;
void Reject(Action action, ConnectionFailureKind kind = ConnectionFailureKind.Invalid)
{
    try { action(); }
    catch (ConnectionFailure error) when (error.Kind == kind) { count++; return; }
    throw new Exception("Expected connection policy rejection.");
}
var valid = definition.Validate(policy);
if (valid.BaseUrl != "https://inventory.example.test/api/" || valid.Settings.Name != "Source") throw new Exception("Normalization changed identity or lost the path prefix.");
count++;
foreach (var bad in new[] {
    definition with { SourceBusinessId = "" }, definition with { SourceBusinessId = "other business" },
    definition with { SourceBusinessId = "business\n" },
    definition with { SourceBusinessId = new string('a',129) }, definition with { PushKeyDays = 0 },
    definition with { PushKeyDays = 366 }, definition with { Push=false, PullLive=false, PullScheduled=false },
    definition with { Settings=definition.Settings with { Name=" " } },
    definition with { Settings=definition.Settings with { Name="Source\0" } },
    definition with { Settings=definition.Settings with { PriceMaxAgeSeconds=0 } },
    definition with { Settings=definition.Settings with { PriceMaxAgeSeconds=604801 } },
    definition with { Settings=definition.Settings with { StockMaxAgeSeconds=0 } },
    definition with { Settings=definition.Settings with { StockMaxAgeSeconds=86401 } },
    definition with { Settings=definition.Settings with { ClockSkewSeconds=-1 } },
    definition with { Settings=definition.Settings with { ClockSkewSeconds=301 } },
    definition with { Settings=definition.Settings with { SyncIntervalSeconds=59 } },
    definition with { Settings=definition.Settings with { SyncIntervalSeconds=86401 } },
    definition with { Settings=definition.Settings with { SyncIntervalSeconds=null } },
    definition with { Settings=null! }, definition with { BaseUrl=null },
    definition with { BaseUrl="https://user:password@inventory.example.test/" },
    definition with { BaseUrl="https://inventory.example.test/?secret=x" },
    definition with { BaseUrl="https://inventory.example.test/#fragment" },
    definition with { BaseUrl="https://inventory.example.test/\r\n" },
    definition with { BaseUrl="http://inventory.example.test/" }
}) Reject(() => bad.Validate(policy));
Reject(() => (definition with { BaseUrl="https://inventory.example.test.attacker.invalid/" }).Validate(policy), ConnectionFailureKind.OriginNotAllowed);
var pushOnly = definition with { PullLive=false, PullScheduled=false, BaseUrl=null, Settings=definition.Settings with { SyncIntervalSeconds=null } };
pushOnly.Validate(policy); count++;
Reject(() => (pushOnly with { BaseUrl="https://inventory.example.test/" }).Validate(policy));
Reject(() => (pushOnly with { Push=false }).Validate(policy));
var loopback = definition with { BaseUrl="http://127.0.0.1:9999/" };
var localOrigins = new HashSet<string> { "http://127.0.0.1:9999" };
Reject(() => loopback.Validate(new(false, localOrigins)));
loopback.Validate(new(true, localOrigins)); count++;
if (ConnectionDefinition.ValidateKey("provider-key", new string('A',64)) != new string('a',64)) throw new Exception("Expected normalized hexadecimal secret.");
count++;
foreach (var key in new[] { "", "invalid key", "key\nheader", "key\n", new string('k',129) })
    Reject(() => ConnectionDefinition.ValidateKey(key,new string('a',64)));
foreach (var secret in new[] { "", new string('a',63), new string('a',65), new string('a',64)+"\n", new string('g',64), "dp_conversation_key" })
    Reject(() => ConnectionDefinition.ValidateKey("key",secret));
Console.WriteLine($"PASS: {count} connection policy checks (identity, modes, freshness, origins and secret formats), without network calls.");
