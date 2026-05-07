using MemoryService.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector.EntityFrameworkCore;

namespace MemoryService.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        var dsBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dsBuilder.UseVector();
        dsBuilder.MapEnum<MemoryType>("memory_type");
        var dataSource = dsBuilder.Build();

        services.AddSingleton(dataSource);
        services.AddDbContext<MemoryDbContext>(o => o.UseNpgsql(dataSource, npg =>
        {
            npg.UseVector();
            // Register the enum at the EF layer too. Data-source-level MapEnum lets Npgsql read/write
            // the value, but EF's parameter binding needs its own registration to send DbType correctly.
            npg.MapEnum<MemoryType>("memory_type");
        }));

        return services;
    }
}
