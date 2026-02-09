using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Ledger.Domain;

namespace SagaOrchestrator.Ledger.Persistence;

public class LedgerDbContext : DbContext
{
    public DbSet<Account> Accounts { get; set; } = null!;
    public DbSet<LedgerEntry> LedgerEntries { get; set; } = null!;

    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ===== Account configuration =====
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(100);
            
            entity.Property(e => e.Balance)
                .HasPrecision(18, 2)
                .IsRequired();
            
            entity.Property(e => e.IsActive)
                .IsRequired()
                .HasDefaultValue(true);
            
            entity.Property(e => e.CreatedAt)
                .IsRequired();
            
            // Optimistic concurrency token (Postgres xmin)
            #pragma warning disable CS0618
            entity.UseXminAsConcurrencyToken();
            #pragma warning restore CS0618
        });

        // ===== LedgerEntry configuration =====
        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .IsRequired();
            
            entity.Property(e => e.ReferenceId)
                .IsRequired()
                .HasMaxLength(200); // Length limit for index performance
            
            entity.Property(e => e.CreatedAt)
                .IsRequired();
            
            // Global unique index for idempotency
            entity.HasIndex(e => e.ReferenceId)
                .IsUnique()
                .HasDatabaseName("IX_LedgerEntry_ReferenceId_Unique");
            
            // Index on AccountId for fast account-based queries
            entity.HasIndex(e => e.AccountId)
                .HasDatabaseName("IX_LedgerEntry_AccountId");
            
            // Composite index for "transactions by account ordered by time" queries
            entity.HasIndex(e => new { e.AccountId, e.CreatedAt })
                .HasDatabaseName("IX_LedgerEntry_AccountId_CreatedAt");
            
            // Account relationship (strict: accounts with transactions cannot be deleted)
            entity.HasOne(e => e.Account)
                  .WithMany(e => e.Entries)
                  .HasForeignKey(e => e.AccountId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
