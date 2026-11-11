using System.Security.Cryptography;
using DocParse.Modules.OperationalData.Contracts;
using DocParse.Modules.Integrations.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace DocParse.Modules.Integrations;

internal static class PushEndpoints
{
    public static void MapPushEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/integrations/v1/connections/{connectionId:guid}").AllowAnonymous().RequireRateLimiting("operational-hooks");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (PushRejected error) { return Problem(context.HttpContext, error.Status, error.Code); }
            catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Problem(context.HttpContext, 413, "payload_too_large"); }
            catch (OperationalDataInvalid) { return Problem(context.HttpContext, 400, "validation"); }
            catch (NpgsqlException) { return Problem(context.HttpContext, 503, "source_unavailable"); }
        });
        group.MapPost("/events", async (Guid connectionId, HttpContext context, PushInbox inbox, IOperationalProjection projection, TimeProvider clock) =>
        {
            if (!context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() && !context.Request.IsHttps)
                throw new PushRejected(400, "validation");
            var content = context.Request.ContentType?.Split(';', StringSplitOptions.TrimEntries);
            if (content is null || !content[0].Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                content.Skip(1).Any(value => !value.Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase) && !value.Equals("charset=\"utf-8\"", StringComparison.OrdinalIgnoreCase)) ||
                context.Request.Headers.ContentEncoding.Count != 0) throw new PushRejected(415, "unsupported_media_type");
            var body = await ReadBodyAsync(context, 1_048_576);
            var (key, signature) = await VerifyAsync(connectionId, context, body, inbox, clock);
            var message = OperationalEventCodec.Parse(body);
            if (message.SourceBusinessId != key.Source.SourceBusinessId) throw new PushRejected(403, "insufficient_scope");
            if (message.OccurredAt > clock.GetUtcNow().AddSeconds(key.Source.ClockSkewSeconds)) throw new PushRejected(400, "validation");
            projection.Validate(message.Change, clock.GetUtcNow(), key.Source.ClockSkewSeconds);
            var result = await inbox.AcceptAsync(key, signature, message, SHA256.HashData(body), context.RequestAborted);
            if (result.Duplicate) return Results.Ok(result.Receipt);
            return Results.Accepted($"/api/integrations/v1/connections/{connectionId}/receipts/{result.Receipt.ReceiptId}", result.Receipt);
        });
        group.MapGet("/receipts/{receiptId:guid}", async (Guid connectionId, Guid receiptId, HttpContext context, PushInbox inbox, TimeProvider clock) =>
        {
            if (!context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() && !context.Request.IsHttps) throw new PushRejected(400, "validation");
            var body = await ReadBodyAsync(context, 0);
            var (key, signature) = await VerifyAsync(connectionId, context, body, inbox, clock);
            var receipt = await inbox.GetReceiptAsync(key, signature, receiptId, context.RequestAborted);
            return receipt is null ? Problem(context, 404, "not_found") : Results.Ok(receipt);
        });
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context, int maximum)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            if (output.Length + count > maximum) throw new PushRejected(maximum == 0 ? 400 : 413, maximum == 0 ? "validation" : "payload_too_large");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static async Task<(Application.VerifiedPushKey Key, OperationalSignature Signature)> VerifyAsync(Guid id, HttpContext context, byte[] body, PushInbox inbox, TimeProvider clock)
    {
        string Header(string name)
        {
            if (!context.Request.Headers.TryGetValue(name, out var values) || values.Count != 1 || values[0] is not string value) throw new PushRejected(401, "invalid_credentials");
            return value;
        }
        var signature = OperationalSignature.Parse(Header("X-Fyr-Key-Id"), Header("X-Fyr-Timestamp"), Header("X-Fyr-Request-Id"), Header("X-Fyr-Signature"), clock.GetUtcNow());
        var key = await inbox.ResolveAsync(id, signature.KeyId, context.RequestAborted) ?? throw new PushRejected(401, "invalid_credentials");
        signature.Verify(key.SecretHex, context.Request.Method.ToUpperInvariant(), context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "", body);
        return (key, signature);
    }
    private static IResult Problem(HttpContext context, int status, string code)
    {
        if (status is 429 or 503) context.Response.Headers.RetryAfter = "60";
        return Results.Problem(statusCode: status, title: "The operational request could not be completed.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier });
    }
}
