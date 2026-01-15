using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Entities;
using SagaOrchestrator.Domain.ValueObjects;

namespace SagaOrchestrator.Infrastructure.Persistence;

public class SagaRepository : ISagaRepository
{
    private readonly SagaDbContext _context;
    
    public SagaRepository(SagaDbContext context)
    {
        _context = context;
    }

    public async Task SaveAsync<TData>(SagaInstance<TData> saga, CancellationToken ct = default)
        where TData : class
    {
        var entity = await _context.Sagas.FirstOrDefaultAsync(s => s.Id == saga.Id, ct);
        
        if (entity == null)
        {
            entity = new SagaEntity
            {
                Id = saga.Id,
                DataType = typeof(TData).Name
            };
            _context.Sagas.Add(entity);
        }
        
        // Update fields
        entity.State = saga.State.ToString();
        entity.CurrentStepIndex = saga.CurrentStepIndex;
        entity.ErrorLog = saga.ErrorLog;
        entity.DataJson = JsonSerializer.Serialize(saga.Data);
        
        await _context.SaveChangesAsync(ct);
    }

    public async Task<SagaInstance<TData>?> LoadAsync<TData>(Guid id, List<ISagaStep<TData>> steps,
        CancellationToken ct = default)
        where TData : class
    {
        var entity = await _context.Sagas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity == null) return null;
        
        var data = JsonSerializer.Deserialize<TData>(entity.DataJson);
        if (data == null) throw new Exception("Failed to deserialize saga data");
        
        var saga = new SagaInstance<TData>(entity.Id, data, steps);
        
        // Safely Parse String back to Enum
        if (!Enum.TryParse<SagaState>(entity.State, out var state))
        {
            state = SagaState.Failed; // Fallback just in case
        }
        
        // Rehydrate state
        saga.LoadState(state, entity.CurrentStepIndex, entity.ErrorLog);
        
        return saga;
    }

    public async Task<bool> TryAddIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        // 1. Quick check
        var exists = await _context.IdempotencyKeys.AnyAsync(k => k.Key == key, ct);
        if (exists) return false;
        
        // 2. Attempt to insert
        _context.IdempotencyKeys.Add(new IdempotencyKey
        {
            Key = key,
            CreatedAt = DateTime.UtcNow
        });
        
        try
        {
            await _context.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Race condition: key was inserted by another thread just now
            return false;
        }
    }
}