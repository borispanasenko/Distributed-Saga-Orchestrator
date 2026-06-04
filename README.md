# Mini Saga Transaction Engine

A .NET 8 MVP transaction workflow engine exploring the orchestration-based Saga pattern, durable state, idempotency, compensation, and outbox-based asynchronous processing.

> **Status:** Active Development / MVP Phase

## Core Concept

This project demonstrates how to coordinate multi-step transactional workflows without relying on a single database transaction or 2PC.

The current version focuses on:

* explicit workflow state;
* durable persistence;
* idempotent execution;
* compensation for failed steps;
* outbox-based asynchronous processing.

The project is currently implemented as a single .NET solution with separate Domain, Application, Infrastructure, API, Console Client, and Ledger modules. These boundaries are intentionally explicit so the design can evolve toward stronger service separation later.

---

## Features

* **Clean Architecture-style structure:** separation between Domain, Application, Infrastructure, API, Console Client, and Ledger modules.
* **Saga coordinator:** central workflow coordinator manages saga execution, state transitions, and failed-step handling.
* **Durable saga state:** saga state is persisted in PostgreSQL through EF Core.
* **Idempotency storage:** dedicated `IdempotencyKey` entity helps prevent duplicate execution of the same logical operation.
* **Compensation logic:** failed workflows can execute compensating steps to undo previously completed work.
* **Transactional Outbox:** API records saga intent and an outbox message atomically before asynchronous processing.
* **Background processing:** `OutboxProcessor` processes queued saga work outside the request path.
* **Ledger module:** simple ledger model used by transfer steps to simulate debit/credit business operations.
* **Docker Compose setup:** local PostgreSQL setup for development.
* **Structured logging:** Serilog setup with console output and optional Seq sink.
* **Swagger/OpenAPI:** API documentation for manual testing.
* **Console Client:** CLI tool for creating and resuming sagas manually.

---

## Current Scope

Implemented:

* create transfer request through API;
* persist saga and outbox message atomically;
* process queued saga work in background;
* execute debit and credit transfer steps;
* persist saga state in PostgreSQL;
* use idempotency records to reduce duplicate execution risk;
* support compensation logic for failed workflows;
* resume saga execution through the console client.

Not implemented yet:

* real multi-service deployment;
* message broker integration;
* production-grade retry/backoff policies;
* dead-letter handling for failed outbox messages;
* optimistic concurrency / row version handling;
* production-grade tracing, metrics, and alerting.

---

## Tech Stack

* **.NET 8** / C#
* **ASP.NET Core**
* **Entity Framework Core**
* **PostgreSQL**
* **Docker & Docker Compose**
* **Serilog**
* **Swagger/OpenAPI**

---

## Architecture Overview

The solution is organized into several modules:

1. **Domain**  
   Core saga entities, value objects, enums, and abstractions.

2. **Application**  
   Saga coordinator and transfer workflow steps.

3. **Infrastructure**  
   EF Core persistence, saga repository, migrations, and outbox storage.

4. **Ledger**  
   Simple debit/credit ledger model used by transfer workflow steps.

5. **API**  
   REST endpoint for creating transfer sagas and queuing work through the outbox.

6. **ConsoleClient**  
   CLI tool for manual saga creation and recovery/resume scenarios.

---

## Main Flow

```text
POST /transfers
Create SagaInstance
Create OutboxMessage
Return 202 Accepted

OutboxProcessor reads queued message
Load saga from PostgreSQL
Execute debit sender step
Execute credit receiver step
Persist state transitions
Complete saga or run compensation on failure
```

---

## How to Run

### 1. Start infrastructure

```bash
docker-compose up -d
```

### 2. Apply migrations

The API applies Saga and Ledger migrations on startup in the current local development setup.

If you want to apply Saga migrations manually:

```bash
dotnet ef database update \
  --project src/SagaOrchestrator.Infrastructure \
  --startup-project src/SagaOrchestrator.API
```

### 3. Run the API

```bash
dotnet run --project src/SagaOrchestrator.API
```

Open Swagger:

```text
http://localhost:5091/swagger
```

### 4. Run the Console Client

```bash
dotnet run --project src/SagaOrchestrator.ConsoleClient
```

---

## Roadmap

* Add stronger optimistic concurrency control for saga state updates.
* Harden outbox processing with retries, backoff, and dead-letter handling.
* Add clearer operational visibility for failed and stuck sagas.
* Add message broker integration, for example RabbitMQ / MassTransit.
* Optionally split Ledger and Saga processing into separate services.
* Add automated tests around saga success, failure, compensation, and idempotency scenarios.

---

## License

MIT