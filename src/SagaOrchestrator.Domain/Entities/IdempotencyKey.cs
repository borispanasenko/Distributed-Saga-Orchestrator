namespace SagaOrchestrator.Domain.Entities;

public class IdempotencyKey
{
    // Unique key: "{StepName}_{SagaId}"
    public string Key { get; set; } = string.Empty;
    
    // DateTime of occasion
    public DateTime CreatedAt { get; set; }
    
    // Operation status, true once the operation is fully completed and sealed.
    public bool IsConsumed { get; set; }
    
    // Locking until the end of certain period of time to prevent concurrent execution.
    public DateTime? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
}