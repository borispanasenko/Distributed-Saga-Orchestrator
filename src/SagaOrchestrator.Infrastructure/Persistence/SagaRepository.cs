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
                // Use FullName to reduce collision risk and survive refactors better.
                DataType = typeof(TData).FullName ?? typeof(TData).Name
            };

            _context.Sagas.Add(entity);
        }

        // Update persisted state snapshot
        entity.State = saga.State.ToString();
        entity.CurrentStepIndex = saga.CurrentStepIndex;
        entity.ErrorLog = saga.ErrorLog;
        entity.DataJson = JsonSerializer.Serialize(saga.Data);

        await _context.SaveChangesAsync(ct);
    }

    public async Task<SagaInstance<TData>?> LoadAsync<TData>(
        Guid id,
        List<ISagaStep<TData>> steps,
        CancellationToken ct = default)
        where TData : class
    {
        var entity = await _context.Sagas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity == null) return null;

        var data = JsonSerializer.Deserialize<TData>(entity.DataJson);
        if (data == null) throw new Exception("Failed to deserialize saga data.");

        var saga = new SagaInstance<TData>(entity.Id, data, steps);

        // Safely parse string back to enum (fallback to Failed if data is corrupted)
        if (!Enum.TryParse<SagaState>(entity.State, out var state))
            state = SagaState.Failed;

        // Rehydrate state
        saga.LoadState(state, entity.CurrentStepIndex, entity.ErrorLog);

        return saga;
    }

    public async Task<bool> TryAddIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        // "Turnstile" pattern:
        // We attempt to INSERT a unique key. If it already exists, the DB rejects it.
        // This avoids the "check-then-act" race condition and relies on a UNIQUE/PK constraint.
        var entity = new IdempotencyKey
        {
            Key = key,
            CreatedAt = DateTime.UtcNow
        };

        _context.IdempotencyKeys.Add(entity);

        try
        {
            await _context.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // IMPORTANT:
            // Do NOT call ChangeTracker.Clear() here.
            // It would detach *everything* tracked in this DbContext and can cause silent data loss.
            // We detach only the failed entity so the context stays healthy for subsequent operations.
            _context.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public async Task CreateSagaAsync<TData>(Guid sagaId, TData data, CancellationToken ct = default)
        where TData : class
    {
        // 1) Prepare the Saga row
        var sagaEntity = new SagaEntity
        {
            Id = sagaId,
            State = SagaState.Created.ToString(),
            CurrentStepIndex = 0,
            DataType = typeof(TData).FullName ?? typeof(TData).Name,
            DataJson = JsonSerializer.Serialize(data),
            ErrorLog = new List<string>()
        };

        // 2) Prepare Outbox row ("intent to start processing")
        var outboxMessage = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "StartSaga",
            Payload = JsonSerializer.Serialize(new { SagaId = sagaId }),
            CreatedAt = DateTime.UtcNow,
            ProcessedAt = null,
            AttemptCount = 0
        };

        // NOTE:
        // If you later enable transient-failure retries (ExecutionStrategy),
        // wrap the whole transaction block into:
        //   var strategy = _context.Database.CreateExecutionStrategy();
        //   await strategy.ExecuteAsync(async () => { ... });

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        try
        {
            _context.Sagas.Add(sagaEntity);
            _context.OutboxMessages.Add(outboxMessage);

            await _context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
