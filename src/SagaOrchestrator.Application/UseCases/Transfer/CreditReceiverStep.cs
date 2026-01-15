using SagaOrchestrator.Domain.Abstractions;

namespace SagaOrchestrator.Application.UseCases.Transfer;

public class CreditReceiverStep : ISagaStep<TransferContext>
{
    public string Name => "CreditReceiver";

    public async Task ExecuteAsync(TransferContext d, CancellationToken ct)
    {
        Console.WriteLine($"[WORKER] Crediting {d.Amount} to receiver...");
        
        // Simulating processing time
        await Task.Delay(2000, ct);
        
        Console.WriteLine($"[WORKER] Credit complete.");
    }

    public Task CompensateAsync(TransferContext d, CancellationToken ct)
    {
        Console.WriteLine($"[WORKER] ↩️ ROLLBACK crediting...");
        return Task.CompletedTask;
    }
}