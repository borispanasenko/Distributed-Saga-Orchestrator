using SagaOrchestrator.Domain.Abstractions;

namespace SagaOrchestrator.Tests.Saga;

public sealed class TestSagaStep<TData> : ISagaStep<TData>
    where TData : class
{
    private readonly Func<TData, CancellationToken, Task> _execute;
    private readonly Func<TData, CancellationToken, Task> _compensate;

    public TestSagaStep(
        string name,
        Func<TData, CancellationToken, Task>? execute = null,
        Func<TData, CancellationToken, Task>? compensate = null)
    {
        Name = name;
        _execute = execute ?? ((_, _) => Task.CompletedTask);
        _compensate = compensate ?? ((_, _) => Task.CompletedTask);
    }

    public string Name { get; }

    public int ExecuteCount { get; private set; }
    public int CompensateCount { get; private set; }

    public async Task ExecuteAsync(TData data, CancellationToken ct = default)
    {
        ExecuteCount++;
        await _execute(data, ct);
    }

    public async Task CompensateAsync(TData data, CancellationToken ct = default)
    {
        CompensateCount++;
        await _compensate(data, ct);
    }
}