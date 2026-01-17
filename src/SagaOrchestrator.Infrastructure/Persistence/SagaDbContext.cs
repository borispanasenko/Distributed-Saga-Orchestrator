using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Domain.Entities;

namespace SagaOrchestrator.Infrastructure.Persistence;

public class SagaDbContext: DbContext
{
    public DbSet<SagaEntity> Sagas { get; set; }
    
    public DbSet<IdempotencyKey> IdempotencyKeys { get; set; }
    
    public DbSet<OutboxMessage> OutboxMessages { get; set; }
    
    public SagaDbContext(DbContextOptions<SagaDbContext> options) : base(options) { }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SagaEntity>(b =>
        {
            b.HasKey(x => x.Id); // Primary Key
            b.Property(x => x.State).IsRequired(); 
            b.Property(x => x.DataJson).HasColumnType("jsonb"); // Using modern Postgres JSONB data format 
        });
        
        modelBuilder.Entity<IdempotencyKey>(b =>
        {
            b.HasKey(x => x.Key);
        });
        
        // Outbox Configuration
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).IsRequired();
            
            // Using JSONB for efficient Postgres data storing
            b.Property(x => x.Payload).HasColumnType("jsonb"); 
            
            // Index #1: For quick unprocessed message search
            // Filter makes index small and quick
            b.HasIndex(x => x.ProcessedAt)
                .HasFilter("\"ProcessedAt\" IS NULL"); 
             
            // Index #2: For sorting by creation time (FIFO - First In, First Out)
            b.HasIndex(x => x.CreatedAt);
        });
        
        base.OnModelCreating(modelBuilder);
    }
}