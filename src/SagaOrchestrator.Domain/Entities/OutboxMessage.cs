namespace SagaOrchestrator.Domain.Entities;

public class OutboxMessage
{
    public Guid Id { get; set; }
    
    // Message type (e.g. "StartSaga")
    public string Type { get; set; } = string.Empty;
    
    // JSON data payload
    public string Payload { get; set; } = string.Empty;
    
    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    
    // Reliability
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    
    // Concurrency Locking
    public DateTime? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
}