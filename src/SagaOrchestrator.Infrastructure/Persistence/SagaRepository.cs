using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Entities;
using SagaOrchestrator.Domain.Enums;
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
                DataType = typeof(TData).FullName ?? typeof(TData).Name
            };
            _context.Sagas.Add(entity);
        }

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

        if (!Enum.TryParse<SagaState>(entity.State, out var state))
            state = SagaState.Failed;

        saga.LoadState(state, entity.CurrentStepIndex, entity.ErrorLog);
        return saga;
    }

    public async Task<bool> TryAddIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
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
            _context.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> IsKeyConsumedAsync(string key, CancellationToken ct = default)
    {
        return await _context.Set<IdempotencyKey>()
            .AnyAsync(k => k.Key == key && k.IsConsumed, ct);
    }

    public async Task<IdempotencyResult> TryClaimKeyAsync(
        string key,
        string ownerId,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var newExpiry = now.Add(ttl);

        // Atomic "Insert-or-Lease-Takeover" logic using PostgreSQL features.
        // 1. If key doesn't exist -> Insert and acquire lock.
        // 2. If key exists but is NOT consumed and lease is expired -> Takeover lock.
        // 3. Otherwise -> Do nothing (return empty result).
        var sql = """
                      INSERT INTO "IdempotencyKeys" ("Key", "CreatedAt", "IsConsumed", "LockedBy", "LockedUntil")
                      VALUES ({0}, {4}, FALSE, {2}, {3})
                      ON CONFLICT ("Key") DO UPDATE
                      SET "LockedBy" = {2}, "LockedUntil" = {3}
                      WHERE "IdempotencyKeys"."IsConsumed" = FALSE
                        AND ("IdempotencyKeys"."LockedUntil" IS NULL OR "IdempotencyKeys"."LockedUntil" < {1})
                      RETURNING "Key";
                  """;

        // CRITICAL FIX: Use ToListAsync() to execute the SQL immediately.
        // We cannot use AnyAsync() here because EF Core attempts to wrap the raw SQL 
        // in a subquery (SELECT EXISTS(...)), which PostgreSQL forbids for INSERT ... RETURNING.
        var result = await _context.Database
            .SqlQueryRaw<string>(sql, key, now, ownerId, newExpiry, now)
            .ToListAsync(ct);

        // If RETURNING clause returned a row, we successfully acquired the lock.
        if (result.Count > 0)
            return IdempotencyResult.Acquired;

        // If we failed to acquire, determine the specific reason (safe read-only check).
        var consumed = await IsKeyConsumedAsync(key, ct);
        return consumed ? IdempotencyResult.AlreadyConsumed : IdempotencyResult.LockedByOther;
    }

    public async Task CompleteKeyAsync(string key, string ownerId, CancellationToken ct = default)
    {
        var rowsAffected = await _context.Set<IdempotencyKey>()
            .Where(k => k.Key == key && k.LockedBy == ownerId)
            .ExecuteUpdateAsync(s => s
                    .SetProperty(k => k.IsConsumed, true)
                    .SetProperty(k => k.LockedUntil, (DateTime?)null)
                    .SetProperty(k => k.LockedBy, (string?)null),
                ct);

        if (rowsAffected != 0)
            return;

        // If we cannot seal it, check if someone already sealed it.
        // This makes completion idempotent under lease-expiry races.
        var alreadyConsumed = await IsKeyConsumedAsync(key, ct);
        if (alreadyConsumed)
            return;

        // Not consumed, but we no longer own the lease -> real problem (TTL too short or worker stalled).
        throw new InvalidOperationException(
            $"Lost lease for key '{key}'. Key is not consumed, but current owner is not '{ownerId}'.");
    }

    public async Task CreateSagaAsync<TData>(Guid sagaId, TData data, CancellationToken ct = default)
        where TData : class
    {
        var sagaEntity = new SagaEntity
        {
            Id = sagaId,
            State = SagaState.Created.ToString(),
            CurrentStepIndex = 0,
            DataType = typeof(TData).FullName ?? typeof(TData).Name,
            DataJson = JsonSerializer.Serialize(data),
            ErrorLog = new List<string>()
        };

        var outboxMessage = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "StartSaga",
            Payload = JsonSerializer.Serialize(new { SagaId = sagaId }),
            CreatedAt = DateTime.UtcNow,
            ProcessedAt = null,
            AttemptCount = 0
        };

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
