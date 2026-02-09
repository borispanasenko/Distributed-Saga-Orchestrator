namespace SagaOrchestrator.Ledger.Domain;

public class LedgerEntry
{
    public Guid Id { get; private set; }
    
    // Foreign key + navigation property
    public Guid AccountId { get; private set; }
    public Account Account { get; private set; } = null!; // Required relationship for EF Core
    
    public decimal Amount { get; private set; }
    public LedgerTransactionType Type { get; private set; }
    
    // Global idempotency key (duplicate protection)
    public string ReferenceId { get; private set; } = null!;
    
    public string? Description { get; private set; }
    public DateTime CreatedAt { get; private set; }
    
    // Constructor for EF Core (called when materializing from the database)
    protected LedgerEntry() { }
    
    // Constructor for creating new ledger entries
    public LedgerEntry(
        Guid accountId, 
        decimal amount, 
        LedgerTransactionType type, 
        string referenceId, 
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(referenceId))
            throw new ArgumentException("ReferenceId is required", nameof(referenceId));
        
        // Generate values only when creating a new entity
        Id = Guid.NewGuid();
        AccountId = accountId;
        Amount = amount;
        Type = type;
        ReferenceId = referenceId;
        Description = description;
        CreatedAt = DateTime.UtcNow;
    }
}