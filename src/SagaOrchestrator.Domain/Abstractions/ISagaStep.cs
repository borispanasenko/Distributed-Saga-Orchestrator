namespace SagaOrchestrator.Domain.Abstractions;

public interface ISagaStep<TData> where TData : class
{
    string Name { get; }
    Task ExecuteAsync(TData data, CancellationToken ct = default);
    Task CompensateAsync(TData data, CancellationToken ct = default);
}