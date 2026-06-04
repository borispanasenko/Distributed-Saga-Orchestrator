using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Entities;
using SagaOrchestrator.Domain.Enums;

namespace SagaOrchestrator.Tests.Saga;

public sealed class FakeSagaRepository : ISagaRepository
{
    public int SaveCount { get; private set; }

    public List<object> SavedSagas { get; } = new();

    public Task SaveAsync<TData>(SagaInstance<TData> saga, CancellationToken ct = default)
        where TData : class
    {
        SaveCount++;
        SavedSagas.Add(saga);
        return Task.CompletedTask;
    }

    public Task<SagaInstance<TData>?> LoadAsync<TData>(
        Guid id,
        List<ISagaStep<TData>> steps,
        CancellationToken ct = default)
        where TData : class
    {
        return Task.FromResult<SagaInstance<TData>?>(null);
    }

    public Task<bool> IsKeyConsumedAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    public Task<IdempotencyResult> TryClaimKeyAsync(
        string key,
        string ownerId,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        return Task.FromResult(IdempotencyResult.Acquired);
    }

    public Task CompleteKeyAsync(string key, string ownerId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task CreateSagaAsync<TData>(Guid sagaId, TData data, CancellationToken ct = default)
        where TData : class
    {
        return Task.CompletedTask;
    }
}