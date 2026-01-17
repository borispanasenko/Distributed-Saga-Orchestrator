namespace SagaOrchestrator.Domain.ValueObjects;

public class TransferSagaData
{
    public Guid SagaId { get; set; }

    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public decimal Amount { get; set; }

    public TransferSagaData() { }
}