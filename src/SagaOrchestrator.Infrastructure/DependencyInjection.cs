using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Infrastructure.Persistence;

namespace SagaOrchestrator.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<SagaDbContext>(options =>
            options.UseNpgsql(connectionString));
        
        services.AddScoped<ISagaRepository, SagaRepository>();
        
        return services;
    }
    
}