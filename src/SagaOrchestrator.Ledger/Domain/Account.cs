using System.ComponentModel.DataAnnotations;

namespace SagaOrchestrator.Ledger.Domain;

public class Account
{
    // Business constants (kept in the domain to avoid spreading rules)
    public const decimal MaxSingleTransactionAmount = 1_000_000_000m; // $1B
    public const decimal OverdraftLimit = -50_000m;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;

    // Stored balance (source of truth for operations)
    public decimal Balance { get; private set; } = 0m;

    // Status and metadata
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    // Navigation property (do NOT use for balance calculations!)
    public ICollection<LedgerEntry> Entries { get; private set; } = new List<LedgerEntry>();

    // Constructor for EF Core
    protected Account() { }

    public Account(Guid id, string name)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Account id cannot be empty", nameof(id));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Account name cannot be empty", nameof(name));

        Id = id;
        Name = name;
    }

    // Domain logic: debit funds
    public void Debit(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Debit amount must be positive", nameof(amount));

        if (amount > MaxSingleTransactionAmount)
            throw new ArgumentException("Amount exceeds maximum single transaction limit", nameof(amount));

        if (Balance - amount < OverdraftLimit)
            throw new InvalidOperationException("Insufficient funds");

        Balance -= amount;
    }

    // Domain logic: credit funds
    public void Credit(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Credit amount must be positive", nameof(amount));

        if (amount > MaxSingleTransactionAmount)
            throw new ArgumentException("Amount exceeds maximum single transaction limit", nameof(amount));

        Balance += amount;
    }

    // Optional: methods to manage account status
    public void Deactivate()
    {
        if (!IsActive)
            throw new InvalidOperationException("Account is already inactive");

        IsActive = false;
    }

    public void Activate()
    {
        if (IsActive)
            throw new InvalidOperationException("Account is already active");

        IsActive = true;
    }
}
