using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Application.Engine;
using SagaOrchestrator.Application.Exceptions;
using SagaOrchestrator.Application.UseCases.Transfer;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.ValueObjects;
using SagaOrchestrator.Infrastructure.Persistence;

namespace SagaOrchestrator.API.BackgroundServices;

public sealed class OutboxProcessor : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly string _workerId = Guid.NewGuid().ToString();

    // Configuration constants
    private static readonly TimeSpan EmptyQueueDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransientConflictDelay = TimeSpan.FromSeconds(2);
    private static readonly int MaxAttemptsBeforeDlq = 10;

    public OutboxProcessor(IServiceProvider serviceProvider, ILogger<OutboxProcessor> logger)
    {
        _sp = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxProcessor started. WorkerId={WorkerId}", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var didWork = await TryProcessOneAsync(stoppingToken);

                if (!didWork)
                    await Task.Delay(EmptyQueueDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in outbox loop. Waiting before restart.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> TryProcessOneAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<ISagaRepository>();
        var coordinator = scope.ServiceProvider.GetRequiredService<SagaCoordinator>();
        
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        var now = DateTime.UtcNow;

        // 1. Identify a candidate message (Read-only, non-locking)
        // We look for messages that are unprocessed AND (not locked OR lock expired)
        var candidateId = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && (m.LockedUntil == null || m.LockedUntil < now))
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.Id)
            .FirstOrDefaultAsync(ct);

        if (candidateId == Guid.Empty)
            return false;

        // 2. Atomic Claim (Optimistic Concurrency)
        // We attempt to set LockedUntil. If another worker took it moments ago, this updates 0 rows.
        var lockUntil = now.Add(LeaseTtl);

        var claimed = await db.OutboxMessages
            .Where(m => m.Id == candidateId
                        && m.ProcessedAt == null
                        && (m.LockedUntil == null || m.LockedUntil < now))
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(m => m.LockedUntil, lockUntil)
                    .SetProperty(m => m.LockedBy, _workerId),
                ct);

        if (claimed == 0)
            return true; // Race condition lost, but loop made "progress" by skipping.

        // 3. Load the message (Now strictly owned by this worker)
        var msg = await db.OutboxMessages.AsNoTracking()
            .FirstAsync(m => m.Id == candidateId, ct);

        _logger.LogInformation("Processing OutboxMessage {MessageId} Type={Type} Attempt={AttemptCount}",
            msg.Id, msg.Type, msg.AttemptCount);

        try
        {
            if (msg.Type == "StartSaga")
            {
                var sagaId = ExtractSagaId(msg.Payload);

                // Saga Composition Root
                // Assembling the specific steps required for this saga type.
                var steps = new List<ISagaStep<TransferSagaData>>
                {
                    new DebitSenderStep(repo, loggerFactory.CreateLogger<DebitSenderStep>()),
                    new CreditReceiverStep(repo, loggerFactory.CreateLogger<CreditReceiverStep>())
                };

                // Load Saga State
                var saga = await repo.LoadAsync(sagaId, steps, ct);
                
                if (saga == null)
                {
                    _logger.LogWarning("Saga {SagaId} not found. Marking message processed to avoid infinite loop.", sagaId);
                    await MarkProcessedAsync(db, msg.Id, ct);
                    return true;
                }

                // Execute Logic
                await coordinator.ProcessAsync(saga, ct);
            }
            else
            {
                _logger.LogWarning("Unknown outbox message type: {Type}. Marking processed.", msg.Type);
            }

            // Success -> Mark Processed
            await MarkProcessedAsync(db, msg.Id, ct);
            _logger.LogInformation("OutboxMessage {MessageId} processed successfully.", msg.Id);
            return true;
        }
        catch (RetryLaterException ex)
        {
            // Case A: Transient Conflict (LockedByOther)
            // The resource is busy. This is expected behavior in high-concurrency scenarios.
            _logger.LogInformation("Transient conflict for message {MessageId}: {Reason}. Will retry later.", msg.Id, ex.Message);

            await ReleaseWithBackoffAsync(
                db, 
                msg.Id, 
                attemptIncrement: false, // Do not count against max attempts
                lastError: ex.Message, 
                delayUntil: DateTime.UtcNow.Add(TransientConflictDelay), 
                ct);

            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Lost lease", StringComparison.OrdinalIgnoreCase))
        {
            // Case B: Lost Lease (Warning)
            // The lease expired during execution. Logic ensures idempotency, but we should log this.
            _logger.LogWarning("Lease lost during processing of message {MessageId}: {Message}", msg.Id, ex.Message);

            await ReleaseWithBackoffAsync(
                db, 
                msg.Id, 
                attemptIncrement: true, 
                lastError: ex.Message, 
                delayUntil: DateTime.UtcNow.AddSeconds(5), 
                ct);

            return true;
        }
        catch (Exception ex)
        {
            // Case C: Hard Failure (Bug/Infrastructure)
            _logger.LogError(ex, "Failed processing message {MessageId}", msg.Id);

            var nextAttempt = msg.AttemptCount + 1;
            var delaySeconds = Math.Min(60, 5 * nextAttempt); // Exponential backoff capped at 60s

            await ReleaseWithBackoffAsync(
                db, 
                msg.Id, 
                attemptIncrement: true, 
                lastError: Truncate(ex.Message, 500), 
                delayUntil: DateTime.UtcNow.AddSeconds(delaySeconds), 
                ct);

            if (nextAttempt >= MaxAttemptsBeforeDlq)
            {
                _logger.LogError("Message {MessageId} exceeded max attempts ({Max}). Requires manual intervention.", msg.Id, MaxAttemptsBeforeDlq);
                // Future: Move to DeadLetterQueue table
            }

            return true;
        }
    }

    private static Guid ExtractSagaId(string payload)
    {
        using var doc = JsonDocument.Parse(payload);

        if (!doc.RootElement.TryGetProperty("SagaId", out var sagaIdProp))
            throw new InvalidOperationException("Outbox payload does not contain 'SagaId'.");

        if (!sagaIdProp.TryGetGuid(out var sagaId))
            throw new InvalidOperationException("Outbox payload 'SagaId' is not a valid GUID.");

        return sagaId;
    }

    private static async Task MarkProcessedAsync(SagaDbContext db, Guid messageId, CancellationToken ct)
    {
        await db.OutboxMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.ProcessedAt, DateTime.UtcNow)
                    .SetProperty(m => m.LockedUntil, (DateTime?)null)
                    .SetProperty(m => m.LockedBy, (string?)null),
                ct);
    }

    private static async Task ReleaseWithBackoffAsync(
        SagaDbContext db,
        Guid messageId,
        bool attemptIncrement,
        string lastError,
        DateTime delayUntil,
        CancellationToken ct)
    {
        if (attemptIncrement)
        {
            await db.OutboxMessages
                .Where(m => m.Id == messageId)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.LastError, lastError)
                        .SetProperty(m => m.LockedUntil, delayUntil)
                        .SetProperty(m => m.LockedBy, (string?)null)
                        .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1),
                    ct);
        }
        else
        {
            await db.OutboxMessages
                .Where(m => m.Id == messageId)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.LastError, lastError)
                        .SetProperty(m => m.LockedUntil, delayUntil)
                        .SetProperty(m => m.LockedBy, (string?)null),
                    ct);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max);
}