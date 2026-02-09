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

    // Configuration constants
    private const int MaxConcurrencyRetries = 5;
    private const int MaxCompensationAttempts = 7;

    public LedgerService(LedgerDbContext dbContext, ILogger<LedgerService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // ARCHITECTURE:
    // 1. Account.Balance - stored balance (O(1) operations)
    // 2. Account.Version - optimistic concurrency token (Postgres xmin)
    // 3. LedgerEntry.ReferenceId - unique index for idempotency
    // 4. Account.Debit()/Credit() - domain logic with validation
    // 5. Exponential backoff retries for concurrency conflicts (+ jitter)
    // 6. Check Account.IsActive before processing operations

    // ===== 1. DEBIT (withdraw) =====
    public async Task<LedgerOperationResult> TryDebitAsync(
        Guid accountId,
        decimal amount,
        string idempotencyKey,
        CancellationToken ct)
    {
        // Idempotency key validation (fail-fast)
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required", nameof(idempotencyKey));
        
        // Amount must be positive
        if (amount <= 0)
        {
            _logger.LogWarning("Debit {Key} rejected: non-positive amount {Amount}",
                idempotencyKey, amount);
            return LedgerOperationResult.Rejected;
        }
        
        // Amount validation (upper bound)
        if (amount > Account.MaxSingleTransactionAmount)
        {
            _logger.LogWarning("Debit {Key} rejected: amount {Amount} exceeds maximum {Max}",
                idempotencyKey, amount, Account.MaxSingleTransactionAmount);
            return LedgerOperationResult.Rejected;
        }

        // Fast idempotency check (without loading Account)
        var existing = await _dbContext.LedgerEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ReferenceId == idempotencyKey, ct);

        if (existing != null)
        {
            if (existing.Type == LedgerTransactionType.Debit)
            {
                _logger.LogInformation("Debit {Key} already exists (idempotent success)", idempotencyKey);
                return LedgerOperationResult.IdempotentSuccess;
            }

            if (existing.Type == LedgerTransactionType.AbortMarker)
            {
                _logger.LogWarning("Debit {Key} blocked by tombstone 🪦", idempotencyKey);
                return LedgerOperationResult.Rejected;
            }

            _logger.LogWarning("Debit {Key} conflicts with existing {Type}", idempotencyKey, existing.Type);
            return LedgerOperationResult.Conflict;
        }

        // Retry loop with exponential backoff to handle optimistic concurrency conflicts
        for (var attempt = 1; attempt <= MaxConcurrencyRetries; attempt++)
        {
            try
            {
                // Load Account with tracking so optimistic locking can work
                var account = await _dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.Id == accountId, ct);

                // Existence and active status check
                if (account == null || !account.IsActive)
                {
                    _logger.LogWarning("Debit {Key} rejected: Account {AccountId} not found or inactive",
                        idempotencyKey, accountId);
                    return LedgerOperationResult.Rejected;
                }

                // Use domain logic Account.Debit()
                // It validates the balance and throws InvalidOperationException on overdraft
                try
                {
                    account.Debit(amount);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Debit {Key} rejected: {Message}", idempotencyKey, ex.Message);
                    return LedgerOperationResult.Rejected;
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(ex, "Debit {Key} rejected: invalid amount", idempotencyKey);
                    return LedgerOperationResult.Rejected;
                }

                // Create a ledger entry
                var entry = new LedgerEntry(
                    accountId,
                    -amount, // Negative amount for debit
                    LedgerTransactionType.Debit,
                    idempotencyKey,
                    $"Saga Debit - Amount: {amount:C}");

                _dbContext.LedgerEntries.Add(entry);

                // Persist changes (Account.Balance is updated, Version is checked)
                await _dbContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Debit {Key} succeeded: {Amount:C} from account {AccountId}, new balance: {Balance:C}",
                    idempotencyKey, amount, accountId, account.Balance);

                return LedgerOperationResult.Success;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Optimistic locking conflict - someone updated the Account in parallel
                _logger.LogInformation(ex,
                    "Concurrency conflict on debit {Key} (attempt {Attempt}/{Max}). Retrying with backoff...",
                    idempotencyKey, attempt, MaxConcurrencyRetries);

                // Clear ChangeTracker before retrying
                _dbContext.ChangeTracker.Clear();

                if (attempt == MaxConcurrencyRetries)
                {
                    _logger.LogWarning("Debit {Key} failed after {Max} retries due to concurrency conflicts",
                        idempotencyKey, MaxConcurrencyRetries);
                    return LedgerOperationResult.Conflict;
                }

                // Exponential backoff + small jitter (thread-safe in .NET 6+)
                var delayMs = 10 * (1 << (attempt - 1)) + Random.Shared.Next(0, 25);
                await Task.Delay(delayMs, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Parallel insert with the same ReferenceId
                _logger.LogInformation(ex, "Debit {Key} race condition on unique constraint. Re-checking...",
                    idempotencyKey);

                // Clear tracker
                _dbContext.ChangeTracker.Clear();

                // Re-check what was actually inserted
                var raced = await _dbContext.LedgerEntries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.ReferenceId == idempotencyKey, ct);

                if (raced?.Type == LedgerTransactionType.Debit)
                    return LedgerOperationResult.IdempotentSuccess;

                if (raced?.Type == LedgerTransactionType.AbortMarker)
                    return LedgerOperationResult.Rejected;

                return LedgerOperationResult.Conflict;
            }
        }

        return LedgerOperationResult.Conflict;
    }

    // ===== 2. CREDIT (deposit) =====
    public async Task<LedgerOperationResult> TryCreditAsync(
        Guid accountId,
        decimal amount,
        string idempotencyKey,
        CancellationToken ct)
    {
        // Idempotency key validation (fail-fast)
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required", nameof(idempotencyKey));

        // Amount must be positive
        if (amount <= 0)
        {
            _logger.LogWarning("Credit {Key} rejected: non-positive amount {Amount}",
                idempotencyKey, amount);
            return LedgerOperationResult.Rejected;
        }
        
        // Amount validation (upper bound)
        if (amount > Account.MaxSingleTransactionAmount)
        {
            _logger.LogWarning("Credit {Key} rejected: amount {Amount} exceeds maximum {Max}",
                idempotencyKey, amount, Account.MaxSingleTransactionAmount);
            return LedgerOperationResult.Rejected;
        }

        // Fast idempotency check
        var existing = await _dbContext.LedgerEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ReferenceId == idempotencyKey, ct);

        if (existing != null)
        {
            if (existing.Type == LedgerTransactionType.Credit)
            {
                _logger.LogInformation("Credit {Key} already exists (idempotent success)", idempotencyKey);
                return LedgerOperationResult.IdempotentSuccess;
            }

            _logger.LogWarning("Credit {Key} conflicts with existing {Type}", idempotencyKey, existing.Type);
            return LedgerOperationResult.Conflict;
        }

        // Retry loop for concurrency conflicts
        for (var attempt = 1; attempt <= MaxConcurrencyRetries; attempt++)
        {
            try
            {
                var account = await _dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.Id == accountId, ct);

                // Existence and active status check
                if (account == null || !account.IsActive)
                {
                    _logger.LogWarning("Credit {Key} rejected: Account {AccountId} not found or inactive",
                        idempotencyKey, accountId);
                    return LedgerOperationResult.Rejected;
                }

                // Use domain logic
                try
                {
                    account.Credit(amount);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(ex, "Credit {Key} rejected: invalid amount", idempotencyKey);
                    return LedgerOperationResult.Rejected;
                }

                // Create a ledger entry
                var entry = new LedgerEntry(
                    accountId,
                    amount, // Positive amount for credit
                    LedgerTransactionType.Credit,
                    idempotencyKey,
                    $"Saga Credit - Amount: {amount:C}");

                _dbContext.LedgerEntries.Add(entry);
                await _dbContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Credit {Key} succeeded: {Amount:C} to account {AccountId}, new balance: {Balance:C}",
                    idempotencyKey, amount, accountId, account.Balance);

                return LedgerOperationResult.Success;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogInformation(ex,
                    "Concurrency conflict on credit {Key} (attempt {Attempt}/{Max}). Retrying with backoff...",
                    idempotencyKey, attempt, MaxConcurrencyRetries);

                _dbContext.ChangeTracker.Clear();

                if (attempt == MaxConcurrencyRetries)
                {
                    _logger.LogWarning("Credit {Key} failed after {Max} retries",
                        idempotencyKey, MaxConcurrencyRetries);
                    return LedgerOperationResult.Conflict;
                }

                // Exponential backoff + small jitter (thread-safe in .NET 6+)
                var delayMs = 10 * (1 << (attempt - 1)) + Random.Shared.Next(0, 25);
                await Task.Delay(delayMs, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _logger.LogInformation(ex, "Credit {Key} race condition. Re-checking...", idempotencyKey);

                _dbContext.ChangeTracker.Clear();

                var raced = await _dbContext.LedgerEntries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.ReferenceId == idempotencyKey, ct);

                if (raced?.Type == LedgerTransactionType.Credit)
                    return LedgerOperationResult.IdempotentSuccess;

                return LedgerOperationResult.Conflict;
            }
        }

        return LedgerOperationResult.Conflict;
    }

    // ===== 3. COMPENSATE DEBIT (compensation/rollback) =====
    public async Task<LedgerOperationResult> TryCompensateDebitAsync(
        Guid accountId,
        decimal amount,
        string originalDebitIdempotencyKey,
        CancellationToken ct)
    {
        // Original idempotency key validation (fail-fast)
        if (string.IsNullOrWhiteSpace(originalDebitIdempotencyKey))
            throw new ArgumentException("Original debit idempotency key is required", nameof(originalDebitIdempotencyKey));

        // Amount must be positive (requested refund amount; may be overridden by original entry)
        if (amount <= 0)
        {
            _logger.LogWarning("CompensateDebit {Key} rejected: non-positive amount {Amount}",
                originalDebitIdempotencyKey, amount);
            return LedgerOperationResult.Rejected;
        }

        for (var attempt = 1; attempt <= MaxCompensationAttempts; attempt++)
        {
            // Check the state of the original debit
            var original = await _dbContext.LedgerEntries
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.ReferenceId == originalDebitIdempotencyKey, ct);

            if (original != null)
            {
                // Tombstone already exists - compensation is complete
                if (original.Type == LedgerTransactionType.AbortMarker)
                {
                    _logger.LogInformation("Compensation {Key} - tombstone already exists",
                        originalDebitIdempotencyKey);
                    return LedgerOperationResult.IdempotentSuccess;
                }

                // Prefer amount from the original entry to avoid compensating an incorrect input amount.
                // Expect original.Amount to be negative for debit.
                var refundAmount = Math.Abs(original.Amount);

                if (refundAmount <= 0)
                {
                    _logger.LogWarning("Compensation {Key} rejected: original debit amount is invalid ({Amount})",
                        originalDebitIdempotencyKey, original.Amount);
                    return LedgerOperationResult.Rejected;
                }

                // Warn if the requested amount differs from the original debit amount.
                // The original ledger entry is the source of truth.
                if (Math.Abs(amount - refundAmount) > 0.01m) // tolerance for rounding
                {
                    _logger.LogWarning(
                        "Compensation {Key}: requested amount {Requested:C} differs from original {Original:C}. Using original amount.",
                        originalDebitIdempotencyKey, amount, refundAmount);
                }


                // Original debit exists - create a refund
                var refundKey = $"Refund_{originalDebitIdempotencyKey}";

                var refundExists = await _dbContext.LedgerEntries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.ReferenceId == refundKey, ct);

                if (refundExists != null)
                {
                    if (refundExists.Type == LedgerTransactionType.Credit)
                    {
                        _logger.LogInformation("Refund {Key} already exists", refundKey);
                        return LedgerOperationResult.IdempotentSuccess;
                    }

                    _logger.LogWarning("Refund {Key} conflict with existing type {Type}",
                        refundKey, refundExists.Type);
                    return LedgerOperationResult.Conflict;
                }

                // Create a refund with improved audit description
                var result = await CreateRefundAsync(
                    accountId,
                    refundAmount,
                    refundKey,
                    originalDebitIdempotencyKey,
                    original.Id,
                    attempt,
                    ct);

                if (result == LedgerOperationResult.Success ||
                    result == LedgerOperationResult.IdempotentSuccess)
                {
                    _logger.LogInformation(
                        "Refund {RefundKey} for original debit {OriginalKey} succeeded",
                        refundKey, originalDebitIdempotencyKey);
                    return result;
                }

                // On conflict - retry with backoff
                if (result == LedgerOperationResult.Conflict)
                {
                    _logger.LogInformation("Refund {Key} conflict (attempt {Attempt}/{Max})",
                        refundKey, attempt, MaxCompensationAttempts);

                    var delayMs = 10 * (1 << (attempt - 1)) + Random.Shared.Next(0, 25);
                    await Task.Delay(delayMs, ct);
                    continue;
                }

                return result;
            }
            else
            {
                // No original debit - create a tombstone
                _logger.LogWarning(
                    "Original debit {Key} not found. Creating tombstone 🪦 (attempt {Attempt}/{Max})",
                    originalDebitIdempotencyKey, attempt, MaxCompensationAttempts);

                var tombstoneResult = await CreateTombstoneAsync(
                    accountId,
                    originalDebitIdempotencyKey,
                    attempt,
                    ct);

                if (tombstoneResult == LedgerOperationResult.Success ||
                    tombstoneResult == LedgerOperationResult.IdempotentSuccess)
                {
                    return tombstoneResult;
                }

                var delayMs = 10 * (1 << (attempt - 1)) + Random.Shared.Next(0, 25);
                await Task.Delay(delayMs, ct);
            }
        }

        _logger.LogWarning(
            "CompensateDebit {Key} did not converge after {Max} attempts",
            originalDebitIdempotencyKey, MaxCompensationAttempts);

        return LedgerOperationResult.Conflict;
    }

    // ===== Private Helper Methods =====

    /// <summary>
    /// Creates a refund with full audit context
    /// </summary>
    private async Task<LedgerOperationResult> CreateRefundAsync(
        Guid accountId,
        decimal amount,
        string refundKey,
        string originalDebitKey,
        Guid originalEntryId,
        int attempt,
        CancellationToken ct)
    {
        for (var retry = 1; retry <= MaxConcurrencyRetries; retry++)
        {
            try
            {
                // IMPORTANT: Load Account to participate in optimistic locking
                var account = await _dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.Id == accountId, ct);

                if (account == null || !account.IsActive)
                {
                    _logger.LogWarning(
                        "Refund {Key} rejected: Account {AccountId} not found or inactive",
                        refundKey, accountId);
                    return LedgerOperationResult.Rejected;
                }

                // Use domain logic
                try
                {
                    account.Credit(amount);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(ex, "Refund {Key} rejected: invalid amount", refundKey);
                    return LedgerOperationResult.Rejected;
                }

                // Create a refund with detailed audit description
                var refund = new LedgerEntry(
                    accountId,
                    amount,
                    LedgerTransactionType.Credit,
                    refundKey,
                    $"Compensation Refund - Original Debit: {originalDebitKey}, Entry ID: {originalEntryId}, Amount: {amount:C}");

                _dbContext.LedgerEntries.Add(refund);
                await _dbContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Refund created: {RefundKey}, Amount: {Amount:C}, Original: {OriginalKey}",
                    refundKey, amount, originalDebitKey);

                return LedgerOperationResult.Success;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogInformation(ex,
                    "Concurrency conflict creating refund {Key} (retry {Retry}/{Max})",
                    refundKey, retry, MaxConcurrencyRetries);

                _dbContext.ChangeTracker.Clear();

                if (retry == MaxConcurrencyRetries)
                {
                    return LedgerOperationResult.Conflict;
                }

                var delayMs = 10 * (1 << (retry - 1)) + Random.Shared.Next(0, 25);
                await Task.Delay(delayMs, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _logger.LogInformation(ex, "Refund {Key} race condition. Re-checking...", refundKey);

                _dbContext.ChangeTracker.Clear();

                var raced = await _dbContext.LedgerEntries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.ReferenceId == refundKey, ct);

                if (raced?.Type == LedgerTransactionType.Credit)
                    return LedgerOperationResult.IdempotentSuccess;

                return LedgerOperationResult.Conflict;
            }
        }

        return LedgerOperationResult.Conflict;
    }

    /// <summary>
    /// Creates a tombstone while loading Account to participate in optimistic locking
    /// </summary>
    private async Task<LedgerOperationResult> CreateTombstoneAsync(
        Guid accountId,
        string originalDebitKey,
        int attempt,
        CancellationToken ct)
    {
        try
        {
            // IMPORTANT: Load Account to participate in optimistic locking.
            // This ensures the tombstone is created atomically with Version (xmin) verification.
            var account = await _dbContext.Accounts
                .FirstOrDefaultAsync(a => a.Id == accountId, ct);

            if (account == null)
            {
                _logger.LogWarning(
                    "Tombstone creation rejected: Account {AccountId} not found",
                    accountId);
                return LedgerOperationResult.Rejected;
            }

            var tombstone = new LedgerEntry(
                accountId,
                0m,
                LedgerTransactionType.AbortMarker,
                originalDebitKey,
                $"Tombstone (Abort Marker) - Prevents future application of debit {originalDebitKey}");

            _dbContext.LedgerEntries.Add(tombstone);

            // Account is already tracked - its Version will be checked during SaveChanges
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Tombstone created for {Key}", originalDebitKey);
            return LedgerOperationResult.Success;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogInformation(ex,
                "Concurrency conflict creating tombstone for {Key} (attempt {Attempt})",
                originalDebitKey, attempt);

            _dbContext.ChangeTracker.Clear();
            return LedgerOperationResult.Conflict;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _logger.LogInformation(ex,
                "Tombstone race condition for {Key} (attempt {Attempt}). Re-checking...",
                originalDebitKey, attempt);

            _dbContext.ChangeTracker.Clear();

            // Re-check what was inserted
            var raced = await _dbContext.LedgerEntries
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.ReferenceId == originalDebitKey, ct);

            if (raced?.Type == LedgerTransactionType.AbortMarker)
            {
                _logger.LogInformation("Tombstone for {Key} already exists (race)", originalDebitKey);
                return LedgerOperationResult.IdempotentSuccess;
            }

            if (raced?.Type == LedgerTransactionType.Debit)
            {
                _logger.LogInformation(
                    "Debit {Key} was inserted during tombstone creation. Will create refund on the next attempt.",
                    originalDebitKey);
                return LedgerOperationResult.Conflict; // Retry - next iteration will create the refund
            }

            return LedgerOperationResult.Conflict;
        }
    }

    /// <summary>
    /// Detects whether the exception was caused by a unique index constraint violation
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // Postgres: "23505" = unique_violation
        var pgException = ex.InnerException as Npgsql.PostgresException;
        return pgException?.SqlState == "23505";
    }
}
