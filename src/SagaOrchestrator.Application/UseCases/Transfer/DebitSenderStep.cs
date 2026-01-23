using SagaOrchestrator.Application.Exceptions;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Enums;
using SagaOrchestrator.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace SagaOrchestrator.Application.UseCases.Transfer;

public class DebitSenderStep : ISagaStep<TransferSagaData>
{
    private readonly ISagaRepository _repository;
    private readonly ILogger<DebitSenderStep> _logger;

    public DebitSenderStep(ISagaRepository repository, ILogger<DebitSenderStep> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public string Name => "DebitSender";

    public async Task ExecuteAsync(TransferSagaData d, CancellationToken ct)
    {
        var idempotencyKey = $"Debit_{d.SagaId}";
        var ownerId = Guid.NewGuid().ToString();

        // TTL should exceed worst-case execution time with buffer.
        // In real world, measure and tune this (plus heartbeats if needed).
        var leaseDuration = TimeSpan.FromMinutes(2);

        _logger.LogInformation("[DebitSender] Checking lease for key: {Key}", idempotencyKey);

        var claim = await _repository.TryClaimKeyAsync(idempotencyKey, ownerId, leaseDuration, ct);

        switch (claim)
        {
            case IdempotencyResult.Acquired:
                _logger.LogInformation("[DebitSender] Lease acquired. Debiting {Amount}...", d.Amount);
                try
                {
                    // Simulate external call
                    await Task.Delay(5000, ct);

                    // Seal idempotency (may throw LostLeaseException depending on repo behavior)
                    await _repository.CompleteKeyAsync(idempotencyKey, ownerId, ct);

                    _logger.LogInformation("[DebitSender] Debit finalized.");
                    return;
                }
                catch
                {
                    // Do NOT release lease manually — let it expire.
                    // Retrying is handled by the outbox processor.
                    _logger.LogInformation("[DebitSender] Failed/interrupted. Will retry later.");
                    throw;
                }

            case IdempotencyResult.AlreadyConsumed:
                _logger.LogInformation("[DebitSender] Already completed earlier. Skipping.");
                return;

            case IdempotencyResult.LockedByOther:
                _logger.LogInformation("[DebitSender] Locked by another worker. Retry later.");
                throw new RetryLaterException($"Lease conflict for {idempotencyKey}");
        }
    }

    public async Task CompensateAsync(TransferSagaData d, CancellationToken ct)
    {
        _logger.LogWarning("⏪ [DebitSender] COMPENSATING: Refunding {Amount} back to account...", d.Amount);
        await Task.Delay(500, ct);
        
        _logger.LogInformation("✅ [DebitSender] Refund completed successfully.");
    }
}
