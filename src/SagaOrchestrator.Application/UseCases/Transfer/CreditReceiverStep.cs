using SagaOrchestrator.Domain.Abstractions;

namespace SagaOrchestrator.Application.UseCases.Transfer;

public class CreditReceiverStep : ISagaStep<TransferContext>
{
    private readonly ISagaRepository _repository;
    
    public CreditReceiverStep(ISagaRepository repository)
    {
        _repository = repository;
    }
    
    public string Name => "CreditReceiver";

    public async Task ExecuteAsync(TransferContext d, CancellationToken ct)
    {
        var idempotencyKey = $"Credit_{d.SagaId}";
        
        Console.WriteLine($"[CreditReceiver] 🔍 Checking idempotency key: {idempotencyKey}...");
        
        // 1. Attempt to reserve the key. If false -> duplicate detected.
        if (!await _repository.TryAddIdempotencyKeyAsync(idempotencyKey, ct))
        {
            Console.WriteLine($"[CreditReceiver] 🛑 SKIP! Operation {idempotencyKey} was already executed.");
            return;
        }
        
        Console.WriteLine($"[CreditReceiver] ✅ Key is free. Crediting {d.Amount} to receiver...");
        
        // 2. Simulate credit processing time
        await Task.Delay(2000, ct);
        
        Console.WriteLine($"[CreditReceiver] Credit complete.");
    }

    public Task CompensateAsync(TransferContext d, CancellationToken ct)
    {
        Console.WriteLine($"[CreditReceiver] ↩️ ROLLBACK crediting {d.Amount}...");
        return Task.CompletedTask;
    }
}