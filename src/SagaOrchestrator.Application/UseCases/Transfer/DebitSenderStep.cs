using SagaOrchestrator.Domain.Abstractions;
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
        
        Console.WriteLine($"[DebitSender] 🔍 Checking idempotency key: {idempotencyKey}...");
        
        // 1. Attempt to reserve the key. If false -> duplicate detected.
        if (!await _repository.TryAddIdempotencyKeyAsync(idempotencyKey, ct))
        {
            Console.WriteLine($"[DebitSender] 🛑 SKIP! Operation {idempotencyKey} was already executed.");
            return; // Exit! Do not debit money twice.
        }
        
        Console.WriteLine($"[DebitSender] ✅ Key is free. Debiting {d.Amount}...");
        
        // 2. Simulate heavy banking debit processing workload
        await Task.Delay(5000, ct);
        
        Console.WriteLine($"[DebitSender] Debit complete.");
    }

    public Task CompensateAsync(TransferSagaData d, CancellationToken ct)
    {
        Console.WriteLine($"[DebitSender] ↩️ ROLLBACK debiting {d.Amount}...");
        return Task.CompletedTask;
    }
}