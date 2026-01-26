namespace SagaOrchestrator.Ledger.Domain;

public class LedgerEntry
{
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
    public LedgerTransactionType Type { get; private set; }
    public string ReferenceId { get; private set; } = null!; // Idempotency key
    public DateTime CreatedAt { get; private set; }
    public string? Reason { get; private set; }

    // For EF Core
    private LedgerEntry() { }

    public LedgerEntry(Guid accountId, decimal amount, LedgerTransactionType type, string referenceId, string? reason = null)
    {
        Id = Guid.NewGuid();
        AccountId = accountId;
        Amount = amount;
        Type = type;
        ReferenceId = referenceId;
        CreatedAt = DateTime.UtcNow;
        Reason = reason;
    }
}