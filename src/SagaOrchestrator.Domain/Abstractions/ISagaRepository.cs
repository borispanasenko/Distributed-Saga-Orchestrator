using SagaOrchestrator.Domain.Entities;

namespace SagaOrchestrator.Domain.Abstractions;

public interface ISagaRepository
{
    Task SaveAsync<TData>(SagaInstance<TData> saga, CancellationToken ct = default) 
        where TData : class;
    
    Task<SagaInstance<TData>?> LoadAsync<TData>(Guid id, List<ISagaStep<TData>> steps, CancellationToken ct = default) 
        where TData : class;
    
    Task<bool> TryAddIdempotencyKeyAsync(string key, CancellationToken ct = default);
}