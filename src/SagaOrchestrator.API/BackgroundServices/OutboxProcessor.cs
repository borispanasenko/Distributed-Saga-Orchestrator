using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Application.Engine;
using SagaOrchestrator.Application.UseCases.Transfer;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.ValueObjects;
using SagaOrchestrator.Infrastructure.Persistence;

namespace SagaOrchestrator.API.BackgroundServices;

public class OutboxProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly string _workerId;

    public OutboxProcessor(IServiceProvider serviceProvider, ILogger<OutboxProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        // Unique worker instance id (useful for debugging in multi-worker scenarios)
        _workerId = Guid.NewGuid().ToString("N");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Outbox Processor started. WorkerID: {WorkerId}", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // BackgroundService is a singleton, but DbContext is scoped -> create a scope each iteration
                using var scope = _serviceProvider.CreateScope();

                var dbContext = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
                var repository = scope.ServiceProvider.GetRequiredService<ISagaRepository>();
                var coordinator = scope.ServiceProvider.GetRequiredService<SagaCoordinator>();

                var now = DateTime.UtcNow;

                // 1) Scout for a candidate id (read-only)
                var candidateId = await dbContext.OutboxMessages
                    .Where(m => m.ProcessedAt == null && (m.LockedUntil == null || m.LockedUntil < now))
                    .OrderBy(m => m.CreatedAt) // FIFO
                    .Select(m => m.Id)
                    .FirstOrDefaultAsync(stoppingToken);

                if (candidateId == Guid.Empty)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                // 2) Atomically claim the message (CRITICAL: include ProcessedAt == null in WHERE)
                var lockExpiry = DateTime.UtcNow.AddMinutes(1);

                var rowsAffected = await dbContext.OutboxMessages
                    .Where(m =>
                        m.Id == candidateId &&
                        m.ProcessedAt == null &&
                        (m.LockedUntil == null || m.LockedUntil < now))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.LockedUntil, lockExpiry)
                        .SetProperty(m => m.LockedBy, _workerId),
                        stoppingToken);

                if (rowsAffected == 0)
                {
                    // Another worker claimed it first (TOCTOU avoided by atomic update)
                    _logger.LogDebug("🔒 Claim failed (race) for {MessageId}. Skipping.", candidateId);
                    continue;
                }

                // 3) Load the message that we just claimed (use AsNoTracking to avoid stale tracking issues)
                var message = await dbContext.OutboxMessages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == candidateId, stoppingToken);

                if (message == null)
                    continue;

                _logger.LogInformation("📨 Processing {MessageId} (Type: {Type})", message.Id, message.Type);

                try
                {
                    if (message.Type == "StartSaga")
                    {
                        var payload = JsonSerializer.Deserialize<SagaPayload>(message.Payload);

                        if (payload != null)
                        {
                            var steps = new List<ISagaStep<TransferSagaData>>
                            {
                                new DebitSenderStep(repository),
                                new CreditReceiverStep(repository)
                            };

                            var saga = await repository.LoadAsync(payload.SagaId, steps, stoppingToken);

                            if (saga != null)
                            {
                                await coordinator.ProcessAsync(saga, stoppingToken);
                            }
                            else
                            {
                                _logger.LogWarning("⚠️ Saga {SagaId} not found (MessageId: {MessageId}).", payload.SagaId, message.Id);
                            }
                        }
                    }

                    // 4) Mark success (atomic finalize)
                    await dbContext.OutboxMessages
                        .Where(m => m.Id == message.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.ProcessedAt, DateTime.UtcNow)
                            .SetProperty(m => m.LockedUntil, (DateTime?)null)
                            .SetProperty(m => m.LockedBy, (string?)null),
                            stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error processing {MessageId}", message.Id);

                    // Truncate error to fit the DB field
                    var errorMsg = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;

                    // Compute next retry based on the (freshly read) AttemptCount.
                    // Simple linear backoff: 5s, 10s, 15s...
                    var nextAttempt = message.AttemptCount + 1;
                    var nextRetry = DateTime.UtcNow.AddSeconds(5 * nextAttempt);

                    await dbContext.OutboxMessages
                        .Where(m => m.Id == message.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                            .SetProperty(m => m.LastError, errorMsg)
                            .SetProperty(m => m.LockedUntil, nextRetry)
                            .SetProperty(m => m.LockedBy, _workerId),
                            stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔥 Critical outbox loop error");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private sealed class SagaPayload
    {
        public Guid SagaId { get; set; }
    }
}
