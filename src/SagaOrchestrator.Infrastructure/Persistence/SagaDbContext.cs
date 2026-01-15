using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Domain.Entities;

namespace SagaOrchestrator.Infrastructure.Persistence;

public class SagaDbContext: DbContext
{
    public DbSet<SagaEntity> Sagas { get; set; }
    
    public DbSet<IdempotencyKey> IdempotencyKeys { get; set; }
    
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
        
        base.OnModelCreating(modelBuilder);
    }
}