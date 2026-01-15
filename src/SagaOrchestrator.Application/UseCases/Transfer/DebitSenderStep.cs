using SagaOrchestrator.Domain.Abstractions;

namespace SagaOrchestrator.Application.UseCases.Transfer;

public class DebitSenderStep : ISagaStep<TransferContext>
{
    private readonly ISagaRepository _repository;
    
    public DebitSenderStep(ISagaRepository repository)
    {
        _repository = repository;
    }
    
    public string Name => "DebitSender";

    public async Task ExecuteAsync(TransferContext d, CancellationToken ct)
    {
        var idempotencyKey = $"Debit_{d.SagaId}";
        
        // Checking if this operation was already performed to ensure idempotency
        Console.WriteLine($"[WORKER] 🔍 Checking idempotency key: {idempotencyKey}...");
        
        // Try to reserve the key. If false -> duplicate detected.
        if (!await _repository.TryAddIdempotencyKeyAsync(idempotencyKey, ct))
        {
            Console.WriteLine($"[WORKER] 🛑 SKIP! Operation {idempotencyKey} was already executed.");
            return; // Exit! Do not debit money twice.
        }
        
        Console.WriteLine($"[WORKER] ✅ Key is free. Debiting {d.Amount}...");
        
        // Delay for simulating heavy banking workload and crash-testing
        await Task.Delay(5000, ct);
        
        Console.WriteLine($"[WORKER] Debit complete.");
    }

    public Task CompensateAsync(TransferContext d, CancellationToken ct)
    {
        Console.WriteLine($"[WORKER] ↩️ ROLLBACK debiting {d.Amount}...");
        return Task.CompletedTask;
    }
}