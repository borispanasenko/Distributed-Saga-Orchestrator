namespace SagaOrchestrator.Domain.ValueObjects;

public class TransferSagaData
{
    public Guid SagaId { get; init; }

    public Guid FromUserId { get; init; }
    public Guid ToUserId { get; init; }
    public decimal Amount { get; init; }

    public TransferSagaData() { }
}