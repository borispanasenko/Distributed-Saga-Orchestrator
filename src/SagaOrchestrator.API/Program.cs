using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Domain.Abstractions;
using SagaOrchestrator.Domain.ValueObjects;
using SagaOrchestrator.Infrastructure.Persistence;
using SagaOrchestrator.API.BackgroundServices;
using SagaOrchestrator.Application.Engine;
using SagaOrchestrator.Ledger.Persistence; 
using SagaOrchestrator.Ledger.Contracts;
using SagaOrchestrator.Ledger.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 1.1) SERILOG configuration
builder.Host.UseSerilog((context, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration) // Load logging settings from appsettings
        .Enrich.FromLogContext()                       // Propagate contextual data (critical for SagaId correlation)
        .WriteTo.Console()                             // Console output for local visibility
        .WriteTo.Seq("http://localhost:5341")); // Centralized log store for cross-service tracing (Seq)

// 1.2) Connection string and Database configuration
// The DbContext is scoped per request, which is exactly what we want
// for transactional consistency.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<SagaDbContext>(options =>
    options.UseNpgsql(connectionString));

// Ledger context (same connection string, but logically isolated)
builder.Services.AddDbContext<LedgerDbContext>(options =>
    options.UseNpgsql(connectionString));

// 2) Repository registration
// The API depends only on the abstraction.
// All persistence details (EF, transactions, outbox) are hidden inside the repository.
builder.Services.AddScoped<ISagaRepository, SagaRepository>();

// LedgerService registration, now it's available for injection in steps
builder.Services.AddScoped<ILedgerService, LedgerService>();

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
    
    var ledgerDb = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
    ledgerDb.Database.Migrate();
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
        SagaId = Guid.NewGuid(),
        FromUserId = request.FromUserId,
        ToUserId = request.ToUserId,
        Amount = request.Amount
    };

    // All complexity (transactions, outbox, durability) lives inside the repository
    var sagaId = sagaData.SagaId;
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
