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
        services.AddDbContext<MemoryDbContext>(o => o.UseNpgsql(dataSource, npg => npg.UseVector()));

        return services;
    }
}
