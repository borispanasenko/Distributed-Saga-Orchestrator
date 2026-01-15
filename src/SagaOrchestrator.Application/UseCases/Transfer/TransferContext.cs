namespace SagaOrchestrator.Application.UseCases.Transfer;

public class TransferContext
{
    public Guid SagaId { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public decimal Amount { get; set; }
}