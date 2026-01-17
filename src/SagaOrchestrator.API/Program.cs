using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.ValueObjects;
using SagaOrchestrator.Infrastructure.Persistence;
using SagaOrchestrator.API.BackgroundServices;
using SagaOrchestrator.Application.Engine;

var builder = WebApplication.CreateBuilder(args);

// 1) Database configuration
// The DbContext is scoped per request, which is exactly what we want
// for transactional consistency.
builder.Services.AddDbContext<SagaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2) Repository registration
// The API depends only on the abstraction.
// All persistence details (EF, transactions, outbox) are hidden inside the repository.
builder.Services.AddScoped<ISagaRepository, SagaRepository>();

// Needed by the OutboxProcessor
builder.Services.AddScoped<SagaCoordinator>();

// Registration of the Outbox consumer
builder.Services.AddHostedService<OutboxProcessor>();

// 3) Standard API services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 4) Apply database migrations on startup
// This ensures that the schema (including Outbox tables) is always up to date.
// For production this is a conscious decision, but for this project it is appropriate.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// 5) Thin, atomic endpoint
// The endpoint does not start background work.
// It only records intent (Saga + Outbox) and returns 202 Accepted.
app.MapPost("/transfers", async (
    TransferRequest request,
    ISagaRepository repository,        // Depend on abstraction, not EF
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    // Build saga input data (pure domain data, no infrastructure concerns)
    var sagaData = new TransferSagaData
    {
        FromUserId = request.FromUserId,
        ToUserId = request.ToUserId,
        Amount = request.Amount
    };

    // All complexity (transactions, outbox, durability) lives inside the repository
    var sagaId = Guid.NewGuid();
    await repository.CreateSagaAsync(sagaId, sagaData, ct);

    logger.LogInformation(
        "Saga {SagaId} created and queued for processing via Outbox.",
        sagaId);

    // 202 Accepted is semantically correct:
    // the request is accepted and guaranteed to be processed asynchronously
    return Results.Accepted(
        $"/sagas/{sagaId}",
        new { SagaId = sagaId, Status = "Queued" });
})
.WithName("CreateTransfer")
.WithOpenApi();

app.Run();

// Request DTO
// This is a transport-level contract, not a domain model.
public record TransferRequest(Guid FromUserId, Guid ToUserId, decimal Amount);
