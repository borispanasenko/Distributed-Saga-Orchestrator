using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Ledger.Domain;

namespace SagaOrchestrator.Ledger.Persistence;

public class LedgerDbContext : DbContext
{
    public DbSet<LedgerEntry> LedgerEntries { get; set; }

    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var ledger = modelBuilder.Entity<LedgerEntry>();
        ledger.HasKey(e => e.Id);
        ledger.Property(e => e.Amount).HasPrecision(18, 2);
        
        // Unique index for idempotency and Tombstone
        ledger.HasIndex(e => e.ReferenceId).IsUnique();
    }
}