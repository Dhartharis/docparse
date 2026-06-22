using DocParse.Application.Services;
using DocParse.Infrastructure.MongoDB;
using DocParse.Infrastructure.OpenAI;
using DocParse.Infrastructure.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS para el frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// PostgreSQL + pgvector
builder.Services.AddDbContext<VectorDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration["PostgreSQL:ConnectionString"],
        o => o.UseVector()
    )
);

// Dependency Injection
builder.Services.AddSingleton<MongoDbContext>();
builder.Services.AddSingleton<DocumentRepository>();
builder.Services.AddSingleton<OpenAIClient>();
builder.Services.AddScoped<DocumentExtractionService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddHttpClient<EmbeddingService>();
builder.Services.AddHttpClient<QueryService>();
builder.Services.AddScoped<EmbeddingService>();
builder.Services.AddScoped<QueryService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

app.Run();