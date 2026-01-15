namespace SagaOrchestrator.Infrastructure.Persistence;

public class SagaEntity
{
    public Guid Id { get; set; }
    public string State { get; set; } = string.Empty;
    public int CurrentStepIndex { get; set; }
    public string DataJson { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public List<string> ErrorLog { get; set; } = new();
    
}