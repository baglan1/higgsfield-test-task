using MemoryService.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using Pgvector.EntityFrameworkCore;

namespace MemoryService.Infrastructure;

/// <summary>
/// Used by the <c>dotnet ef</c> tool at design time (e.g. <c>migrations add</c>, <c>migrations script</c>).
/// Builds a configured DbContext without spinning up the full host. The connection string is a placeholder —
/// design-time operations don't open a connection unless you also run <c>database update</c>.
/// </summary>
public sealed class MemoryDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MemoryDbContext>
{
    public MemoryDbContext CreateDbContext(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
                      ?? "Host=localhost;Port=5432;Username=memory;Password=memory;Database=memory";

        var dsBuilder = new NpgsqlDataSourceBuilder(connStr);
        dsBuilder.UseVector();
        dsBuilder.MapEnum<MemoryType>("memory_type");
        var dataSource = dsBuilder.Build();

        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql(dataSource, npg => npg.UseVector())
            .Options;

        return new MemoryDbContext(options);
    }
}
