namespace SagaOrchestrator.Domain.Entities;

public class IdempotencyKey
{
    // Unique key: "{SagaId}_{StepName}"
    public string Key { get; set; } = string.Empty;
    
    // DateTime of occasion
    public DateTime CreatedAt { get; set; }
}