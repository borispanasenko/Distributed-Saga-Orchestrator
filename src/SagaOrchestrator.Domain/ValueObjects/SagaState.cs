namespace SagaOrchestrator.Domain.ValueObjects;

public enum SagaState
{
    Created,
    Running,
    Completed,
    Failed,
    Compensating,
    Compensated,
    FatalError
}