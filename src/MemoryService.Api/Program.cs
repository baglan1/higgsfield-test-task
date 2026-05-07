using MemoryService.Api.Configuration;
using MemoryService.Api.Endpoints;
using MemoryService.Api.Middleware;
using MemoryService.Core.Configuration;
using MemoryService.Infrastructure;
using MemoryService.Llm;
using MemoryService.Recall;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.Configure<MemoryServiceOptions>(o => MemoryServiceOptionsBinder.Bind(o, builder.Configuration));

var connStr = builder.Configuration.GetConnectionString("Postgres")
              ?? "Host=localhost;Port=5432;Username=memory;Password=memory;Database=memory";
builder.Services.AddMemoryInfrastructure(connStr);
builder.Services.AddLlmProviders();
builder.Services.AddRecallPipeline();
builder.Services.AddScoped<MemoryService.Api.Services.TurnService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<BearerAuthMiddleware>();
app.UseMiddleware<ProblemMiddleware>();

// Apply EF Core migrations on startup. Retries briefly so we tolerate a slow Postgres warmup
// in docker-compose / CI scenarios.
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (true)
    {
        try { await db.Database.MigrateAsync(); break; }
        catch when (DateTime.UtcNow < deadline) { await Task.Delay(500); }
    }
}

app.MapHealthEndpoints();
app.MapTurnsEndpoints();
app.MapRecallEndpoints();
app.MapSearchEndpoints();
app.MapMemoriesEndpoints();

app.Run();

public partial class Program;
