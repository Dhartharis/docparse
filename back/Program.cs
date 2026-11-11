using DocParse.Modules.Knowledge;
using DocParse.Modules.Bots;
using DocParse.Modules.Chat;
using DocParse.Modules.Usage;
using DocParse.Modules.Integrations;
using DocParse.Modules.OperationalData;
using DocParse.Modules.Knowledge.Infrastructure;
using Azure.Storage.Blobs;
using DocParse.Infrastructure.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using DocParse.Modules.Access;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

if (args.Contains("--migrate"))
{
    await SchemaMigrator.ApplyAsync(builder.Configuration["PostgreSQL:ConnectionString"]!);
    return;
}

if (args.Contains("--print-access-schema"))
{
    using var context = new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>()
        .UseNpgsql(builder.Configuration["PostgreSQL:ConnectionString"]).Options);
    Console.Write(context.Database.GenerateCreateScript());
    return;
}

builder.Services.AddAccessModule(builder.Configuration);
builder.Services.AddDataProtection().SetApplicationName("DocParse")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".keys")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/api/integrations/v1/connections")) return;
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(statusCode: 429, title: "The operational request could not be completed.",
            extensions: new Dictionary<string, object?> { ["code"] = "rate_limited", ["traceId"] = context.HttpContext.TraceIdentifier })
            .ExecuteAsync(context.HttpContext);
    };
    options.AddPolicy("operational-hooks", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("authentication", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// Services
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DocParse.Infrastructure.ApiExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Paste the accessToken returned by /api/auth/login. Local Identity tokens are opaque, not JWTs."
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Reference = new Microsoft.OpenApi.Models.OpenApiReference { Id = "Bearer", Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme }
        }] = Array.Empty<string>()
    });
});

// Web client origins are deployment configuration.
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebClient", policy =>
    {
        policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? ["http://127.0.0.1:5173", "http://localhost:5173"])
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddSingleton(_ => new BlobServiceClient(builder.Configuration["AzureStorage:ConnectionString"],
    new BlobClientOptions(BlobClientOptions.ServiceVersion.V2026_06_06)));
builder.Services.AddKnowledgeModule(builder.Configuration);
builder.Services.AddBotsModule(builder.Configuration);
builder.Services.AddChatModule(builder.Configuration);
builder.Services.AddUsageModule(builder.Configuration);
builder.Services.AddIntegrationsModule(builder.Configuration, builder.Environment);
builder.Services.AddOperationalDataModule();

var app = builder.Build();

if (args.Contains("--bootstrap-local-admin"))
{
    await LocalAdminBootstrap.RunAsync(app.Services, app.Environment, app.Configuration);
    return;
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.Use(async(context,next)=>{if(context.Request.ContentType?.Contains("json",StringComparison.OrdinalIgnoreCase)==true){var limit=context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();if(limit is {IsReadOnly:false})limit.MaxRequestBodySize=context.Request.Path.StartsWithSegments("/api/integrations/v1/connections")?1_048_576:65536;}await next();});
var widgetAssets = builder.Configuration["Widget:AssetsPath"] ?? Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "front", "dist"));
if (Directory.Exists(widgetAssets)) app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(widgetAssets) });
app.UseHttpsRedirection();
app.UseCors("WebClient");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapAccessModule();
app.MapKnowledgeModule();
app.MapBotsModule();
app.MapUsageModule();
app.MapIntegrationsModule();
app.MapOperationalDataModule();
app.MapControllers();
app.MapGet("/health", async (KnowledgeDbContext database, BlobServiceClient blobServiceClient) =>
{
    var databaseAvailable = await database.Database.CanConnectAsync();
    await blobServiceClient.GetPropertiesAsync();

    return databaseAvailable
        ? Results.Ok(new { status = "ok" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.Run();
