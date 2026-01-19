using SagaOrchestrator.Application.Exceptions;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.Enums;
using SagaOrchestrator.Domain.ValueObjects;

namespace SagaOrchestrator.Application.UseCases.Transfer;

public class CreditReceiverStep : ISagaStep<TransferSagaData>
{
    private readonly ISagaRepository _repository;

    public CreditReceiverStep(ISagaRepository repository) => _repository = repository;

    public string Name => "CreditReceiver";

    public async Task ExecuteAsync(TransferSagaData d, CancellationToken ct)
    {
        var idempotencyKey = $"Credit_{d.SagaId}";
        var ownerId = Guid.NewGuid().ToString();
        var leaseDuration = TimeSpan.FromMinutes(2);

        Console.WriteLine($"[CreditReceiver] Checking lease for key: {idempotencyKey}");

        var claim = await _repository.TryClaimKeyAsync(idempotencyKey, ownerId, leaseDuration, ct);

        switch (claim)
        {
            case IdempotencyResult.Acquired:
                Console.WriteLine($"[CreditReceiver] Lease acquired. Crediting {d.Amount}...");
                try
                {
                    await Task.Delay(2000, ct);
                    await _repository.CompleteKeyAsync(idempotencyKey, ownerId, ct);
                    Console.WriteLine("[CreditReceiver] Credit finalized.");
                    return;
                }
                catch
                {
                    Console.WriteLine("[CreditReceiver] Failed/interrupted. Will retry later.");
                    throw;
                }

            case IdempotencyResult.AlreadyConsumed:
                Console.WriteLine("[CreditReceiver] Already completed earlier. Skipping.");
                return;

            case IdempotencyResult.LockedByOther:
                Console.WriteLine("[CreditReceiver] Locked by another worker. Retry later.");
                throw new RetryLaterException($"Lease conflict for {idempotencyKey}");
        }
    }

    public Task CompensateAsync(TransferSagaData d, CancellationToken ct)
    {
        Console.WriteLine($"[CreditReceiver] Compensating credit {d.Amount}...");
        return Task.CompletedTask;
    }
}
