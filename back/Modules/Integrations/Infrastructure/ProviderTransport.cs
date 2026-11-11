using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DocParse.Modules.Integrations.Application;

namespace DocParse.Modules.Integrations.Infrastructure;

public static class ProviderDestination
{
    public static bool IsPublic(IPAddress value)
    {
        if (value.IsIPv4MappedToIPv6) value = value.MapToIPv4();
        var bytes = value.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2 || bytes[1] == 88 && bytes[2] == 99)) &&
                !value.Equals(IPAddress.Parse("168.63.129.16")) &&
                !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        // Fail closed outside global unicast; reject transition, documentation and special-purpose ranges.
        return value.ScopeId == 0 && (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 0x02 || bytes[2] == 0x0d && bytes[3] == 0xb8)) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) && !(bytes[0] == 0x3f && bytes[1] == 0xff);
    }
    public static Uri Endpoint(string baseUrl, ProviderPolicy policy)
    {
        var definition = new ConnectionDefinition("probe",baseUrl,false,true,false,0,new("probe",600,60,0,null)).Validate(policy);
        return new Uri(new Uri(definition.BaseUrl!),"integrations/v1/operational-data/query");
    }
}

public interface IProviderTransport
{
    Task<byte[]> QueryAsync(Uri endpoint, string keyId, string secretHex, byte[] body, CancellationToken ct);
}
public sealed class ProviderTransport(ProviderPolicy policy, TimeProvider clock) : IProviderTransport
{
    public async Task<byte[]> QueryAsync(Uri endpoint, string keyId, string secretHex, byte[] body, CancellationToken ct)
    {
        var local = policy.Development && (endpoint.IsLoopback || endpoint.Host == "host.docker.internal");
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect=false, UseProxy=false, UseCookies=false, AutomaticDecompression=DecompressionMethods.None, MaxResponseHeadersLength=16,
            ConnectCallback=async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,token);
                if (addresses.Length == 0 || !local && addresses.Any(address => !ProviderDestination.IsPublic(address))) throw new LiveQueryFailure("connection_origin");
                var socket = new Socket(SocketType.Stream,ProtocolType.Tcp);
                try { await socket.ConnectAsync(addresses,context.DnsEndPoint.Port,token); return new NetworkStream(socket,true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout=Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post,endpoint) { Version=HttpVersion.Version11, VersionPolicy=HttpVersionPolicy.RequestVersionExact, Content=new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json") { CharSet="utf-8" };
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString();
        var canonical = string.Join("\n","FYR-OPDATA-V1","POST",endpoint.PathAndQuery,keyId,timestamp,nonce,Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        request.Headers.Add("X-Fyr-Key-Id",keyId); request.Headers.Add("X-Fyr-Timestamp",timestamp); request.Headers.Add("X-Fyr-Request-Id",nonce);
        request.Headers.Add("X-Fyr-Signature","v1="+Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(secretHex),Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant());
        using var response = await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if (!response.IsSuccessStatusCode)
        {
            int? retry = int.TryParse(response.Headers.RetryAfter?.ToString(),out var delay) && delay is >=1 and <=3600 ? delay : null;
            throw new LiveQueryFailure(response.StatusCode switch { HttpStatusCode.Unauthorized => "provider_credentials", HttpStatusCode.Forbidden => "provider_scope", HttpStatusCode.TooManyRequests => "provider_rate", _ => "provider_unavailable" },retry);
        }
        var content = response.Content.Headers.ContentType;
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 ||
            content is null || !string.Equals(content.MediaType,"application/json",StringComparison.OrdinalIgnoreCase) ||
            content.CharSet is not null && !string.Equals(content.CharSet.Trim('"'),"utf-8",StringComparison.OrdinalIgnoreCase) || response.Content.Headers.ContentLength > 1_048_576)
            throw new LiveQueryFailure("provider_response");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int size;
        while ((size = await stream.ReadAsync(buffer,ct)) > 0)
        {
            if (output.Length+size > 1_048_576) throw new LiveQueryFailure("provider_response");
            output.Write(buffer,0,size);
        }
        return output.ToArray();
    }
}
