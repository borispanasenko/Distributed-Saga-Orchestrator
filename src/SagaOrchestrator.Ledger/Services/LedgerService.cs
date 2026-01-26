using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SagaOrchestrator.Ledger.Contracts;
using SagaOrchestrator.Ledger.Domain;
using SagaOrchestrator.Ledger.Persistence;

namespace SagaOrchestrator.Ledger.Services;

public class LedgerService : ILedgerService
{
    private readonly LedgerDbContext _dbContext;
    private readonly ILogger<LedgerService> _logger;

    public LedgerService(LedgerDbContext dbContext, ILogger<LedgerService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // IMPORTANT DB ASSUMPTION:
    // ReferenceId MUST be unique across ledger entries (unique index/constraint).
    // This is what makes idempotency safe under concurrency.
    //
    // Example (EF Core model config):
    // modelBuilder.Entity<LedgerEntry>()
    //     .HasIndex(e => e.ReferenceId)
    //     .IsUnique();

    // 1) DEBIT (money out)
    public async Task<LedgerOperationResult> TryDebitAsync(
        Guid accountId,
        decimal amount,
        string idempotencyKey,
        CancellationToken ct)
    {
        // Fast-path: if we already have a record with the same key, interpret it.
        // This avoids doing balance check work on retries.
        var existing = await _dbContext.LedgerEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ReferenceId == idempotencyKey, ct);

        if (existing != null)
        {
            // Same debit already applied -> idempotent success.
            if (existing.Type == LedgerTransactionType.Debit)
                return LedgerOperationResult.IdempotentSuccess;

            // Tombstone exists -> the debit was "aborted" earlier and must never be applied.
            // Treat as non-retryable from saga perspective.
            if (existing.Type == LedgerTransactionType.AbortMarker)
            {
                _logger.LogWarning("Debit {Key} blocked by Tombstone 🪦 (abort marker).", idempotencyKey);
                return LedgerOperationResult.Rejected;
            }

            // Different type under same key is a data/logic issue -> surface as conflict.
            return LedgerOperationResult.Conflict;
        }

        // Balance calculation NOTE:
        // Summing all entries is O(N) and not production-scalable.
        // In real systems, keep a separate balance table with optimistic concurrency,
        // or maintain snapshots/materialized views.
        var currentBalance = await _dbContext.LedgerEntries
            .Where(e => e.AccountId == accountId)
            .SumAsync(e => e.Amount, ct);

        // Overdraft limit (test value). In production typically 0 unless explicitly allowed.
        var overdraftLimit = -50000m;

        if (currentBalance - amount < overdraftLimit)
        {
            _logger.LogWarning(
                "Rejected Debit {Key}. Insufficient funds. Balance: {Balance}, Amount: {Amount}",
                idempotencyKey, currentBalance, amount);

            return LedgerOperationResult.Rejected;
        }

        // Insert the debit entry.
        var entry = new LedgerEntry(
            accountId,
            -amount,
            LedgerTransactionType.Debit,
            idempotencyKey,
            "Saga Debit");

        _dbContext.LedgerEntries.Add(entry);

        try
        {
            await _dbContext.SaveChangesAsync(ct);
            return LedgerOperationResult.Success;
        }
        catch (DbUpdateException ex)
        {
            // Concurrency/idempotency collision path.
            // Another worker may have inserted the same ReferenceId in parallel.
            _logger.LogInformation(ex, "DbUpdateException on Debit {Key}. Re-checking existing entry...", idempotencyKey);

            var after = await _dbContext.LedgerEntries.AsNoTracking()
                .FirstOrDefaultAsync(e => e.ReferenceId == idempotencyKey, ct);

            if (after == null)
            {
                // If we can't see it, treat as retryable conflict.
                return LedgerOperationResult.Conflict;
            }

            if (after.Type == LedgerTransactionType.Debit)
                return LedgerOperationResult.IdempotentSuccess;

            if (after.Type == LedgerTransactionType.AbortMarker)
            {
                _logger.LogWarning("Debit {Key} raced with Tombstone 🪦.", idempotencyKey);
                return LedgerOperationResult.Rejected;
            }

            return LedgerOperationResult.Conflict;
        }
    }

    // 2) CREDIT (money in)
    public async Task<LedgerOperationResult> TryCreditAsync(
        Guid accountId,
        decimal amount,
        string idempotencyKey,
        CancellationToken ct)
    {
        // Credits are typically always allowed (no balance check),
        // but still must be idempotent.

        var existing = await _dbContext.LedgerEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ReferenceId == idempotencyKey, ct);

        if (existing != null)
        {
            if (existing.Type == LedgerTransactionType.Credit)
                return LedgerOperationResult.IdempotentSuccess;

            // If some other type occupies the same idempotency key, treat as conflict.
            // (You may want to alert/monitor this.)
            return LedgerOperationResult.Conflict;
        }

        var entry = new LedgerEntry(
            accountId,
            amount, // positive amount for credit
            LedgerTransactionType.Credit,
            idempotencyKey,
            "Saga Credit");

        _dbContext.LedgerEntries.Add(entry);

        try
        {
            await _dbContext.SaveChangesAsync(ct);
            return LedgerOperationResult.Success;
        }
        catch (DbUpdateException ex)
        {
            // Another worker may have inserted the same key concurrently.
            _logger.LogInformation(ex, "DbUpdateException on Credit {Key}. Re-checking existing entry...", idempotencyKey);

            var after = await _dbContext.LedgerEntries.AsNoTracking()
                .FirstOrDefaultAsync(e => e.ReferenceId == idempotencyKey, ct);

            if (after == null)
                return LedgerOperationResult.Conflict;

            if (after.Type == LedgerTransactionType.Credit)
                return LedgerOperationResult.IdempotentSuccess;

            return LedgerOperationResult.Conflict;
        }
    }

    // 3) COMPENSATE DEBIT (refund debit or create tombstone)
    public async Task<LedgerOperationResult> TryCompensateDebitAsync(
        Guid accountId,
        decimal amount,
        string originalDebitIdempotencyKey,
        CancellationToken ct)
    {
        // Goal:
        // - If the original debit exists -> create a refund credit (idempotent).
        // - If the original debit does NOT exist -> create a tombstone (AbortMarker) for the debit key,
        //   so the debit can never be applied later (network delay / out-of-order delivery).
        //
        // Concurrency rules:
        // - Refund uses a derived unique key: Refund_{originalDebitKey}
        // - Tombstone uses the ORIGINAL debit key to "occupy" it.

        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Always re-check the current state on each attempt.
            var original = await _dbContext.LedgerEntries.AsNoTracking()
                .FirstOrDefaultAsync(e => e.ReferenceId == originalDebitIdempotencyKey, ct);

            if (original != null)
            {
                // If a tombstone is already there, we consider compensation done.
                if (original.Type == LedgerTransactionType.AbortMarker)
                    return LedgerOperationResult.IdempotentSuccess;

                // If original debit exists, we refund it (idempotently).
                var refundKey = $"Refund_{originalDebitIdempotencyKey}";

                var refundExisting = await _dbContext.LedgerEntries.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.ReferenceId == refundKey, ct);

                if (refundExisting != null)
                {
                    if (refundExisting.Type == LedgerTransactionType.Credit)
                        return LedgerOperationResult.IdempotentSuccess;

                    // Something else under refundKey is unexpected.
                    return LedgerOperationResult.Conflict;
                }

                var refund = new LedgerEntry(
                    accountId,
                    amount, // refund = credit back
                    LedgerTransactionType.Credit,
                    refundKey,
                    "Saga Compensation (Refund)");

                _dbContext.LedgerEntries.Add(refund);

                try
                {
                    await _dbContext.SaveChangesAsync(ct);
                    return LedgerOperationResult.Success;
                }
                catch (DbUpdateException ex)
                {
                    _logger.LogInformation(
                        ex,
                        "DbUpdateException while creating Refund for {Key} (attempt {Attempt}/{Max}). Re-checking...",
                        originalDebitIdempotencyKey, attempt, maxAttempts);

                    // Retry: on the next loop iteration we will re-check refund existence.
                }
            }
            else
            {
                // Original debit is missing -> create tombstone on the ORIGINAL debit key.
                _logger.LogWarning(
                    "Original Debit {Key} not found. Creating Tombstone 🪦 (attempt {Attempt}/{Max}).",
                    originalDebitIdempotencyKey, attempt, maxAttempts);

                var tombstone = new LedgerEntry(
                    accountId,
                    0m,
                    LedgerTransactionType.AbortMarker,
                    originalDebitIdempotencyKey,
                    "Tombstone (AbortMarker)");

                _dbContext.LedgerEntries.Add(tombstone);

                try
                {
                    await _dbContext.SaveChangesAsync(ct);
                    return LedgerOperationResult.Success;
                }
                catch (DbUpdateException ex)
                {
                    // Race scenario:
                    // - while we were creating the tombstone, the debit might have been inserted,
                    //   or another worker might have inserted tombstone/refund.
                    _logger.LogInformation(
                        ex,
                        "DbUpdateException while creating Tombstone for {Key} (attempt {Attempt}/{Max}). Re-checking...",
                        originalDebitIdempotencyKey, attempt, maxAttempts);

                    // Retry: on the next iteration we will observe the new state and choose refund vs idempotent.
                }
            }
        }

        // If we can't converge after several attempts, treat as retryable conflict.
        // The saga/outbox can retry later.
        _logger.LogWarning(
            "CompensateDebit {Key} did not converge after {MaxAttempts} attempts. Returning Conflict.",
            originalDebitIdempotencyKey, maxAttempts);

        return LedgerOperationResult.Conflict;
    }
}
