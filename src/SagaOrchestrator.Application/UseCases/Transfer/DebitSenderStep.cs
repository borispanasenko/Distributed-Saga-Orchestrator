using SagaOrchestrator.Application.Exceptions;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Enums;
using SagaOrchestrator.Domain.ValueObjects;

namespace SagaOrchestrator.Application.UseCases.Transfer;

public class DebitSenderStep : ISagaStep<TransferSagaData>
{
    private readonly ISagaRepository _repository;

    public DebitSenderStep(ISagaRepository repository) => _repository = repository;

    public string Name => "DebitSender";

    public async Task ExecuteAsync(TransferSagaData d, CancellationToken ct)
    {
        var idempotencyKey = $"Debit_{d.SagaId}";
        var ownerId = Guid.NewGuid().ToString();

        // TTL should exceed worst-case execution time with buffer.
        // In real world, measure and tune this (plus heartbeats if needed).
        var leaseDuration = TimeSpan.FromMinutes(2);

        Console.WriteLine($"[DebitSender] Checking lease for key: {idempotencyKey}");

        var claim = await _repository.TryClaimKeyAsync(idempotencyKey, ownerId, leaseDuration, ct);

        switch (claim)
        {
            case IdempotencyResult.Acquired:
                Console.WriteLine($"[DebitSender] Lease acquired. Debiting {d.Amount}...");
                try
                {
                    // Simulate external call
                    await Task.Delay(5000, ct);

                    // Seal idempotency (may throw LostLeaseException depending on repo behavior)
                    await _repository.CompleteKeyAsync(idempotencyKey, ownerId, ct);

                    Console.WriteLine("[DebitSender] Debit finalized.");
                    return;
                }
                catch
                {
                    // Do NOT release lease manually — let it expire.
                    // Retrying is handled by the outbox processor.
                    Console.WriteLine("[DebitSender] Failed/interrupted. Will retry later.");
                    throw;
                }

            case IdempotencyResult.AlreadyConsumed:
                Console.WriteLine("[DebitSender] Already completed earlier. Skipping.");
                return;

            case IdempotencyResult.LockedByOther:
                Console.WriteLine("[DebitSender] Locked by another worker. Retry later.");
                throw new RetryLaterException($"Lease conflict for {idempotencyKey}");
        }
    }

    public Task CompensateAsync(TransferSagaData d, CancellationToken ct)
    {
        Console.WriteLine($"[DebitSender] Compensating debit {d.Amount}...");
        return Task.CompletedTask;
    }
}
