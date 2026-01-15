# 🎻 Distributed Saga Orchestrator

A robust transaction engine implementing the **Orchestration Saga Pattern** in .NET 8, designed for distributed systems requiring high data consistency.

> **Status:** Active Development (MVP Phase)

## 🎯 Core Concept
This project demonstrates how to handle distributed transactions across microservices without 2PC (Two-Phase Commit), ensuring **Eventual Consistency** and **Idempotency**.

## 🚀 Features (Implemented)

* **Clean Architecture:** Strict separation of concerns (Domain, Application, Infrastructure, API).
* **Saga State Machine:** Centralized coordinator manages the workflow state and transitions.
* **Idempotency:** Deduplication using a dedicated `IdempotencyKey` store to prevent double-spending.
* **Compensation Mechanism:** Automatic rollback (Undo steps) in case of business rule violations or failures.
* **Persistence:** State is persisted in **PostgreSQL** (EF Core) after every step.
* **Dockerized:** Full environment setup via `docker-compose`.

## 🚧 Roadmap (Upcoming Features)

The current implementation uses background tasks for simplicity. The goal is to evolve this into a production-grade Fintech engine:

* [ ] **Transactional Outbox Pattern:** Replacing `Task.Run` with an `OutboxMessages` table to guarantee at-least-once delivery even if the process crashes.
* [ ] **Optimistic Concurrency Control:** Adding `RowVersion` (ETag) to handle concurrent updates to the Saga state.
* [ ] **Message Broker Integration:** migrating from direct service calls to **MassTransit (RabbitMQ)**.

## 🛠 Tech Stack

* **.NET 8** (C#)
* **PostgreSQL** (Database)
* **Entity Framework Core** (ORM)
* **Docker & Docker Compose**
* **Swagger/OpenAPI**

## 🏗 Architecture Overview

The project follows the **Clean Architecture** principles:

1.  **Domain:** Core entities (`SagaInstance`, `IdempotencyKey`) and interfaces. No dependencies.
2.  **Application:** Business logic (`SagaCoordinator`, `DebitSenderStep`, `CreditReceiverStep`).
3.  **Infrastructure:** Database implementation (`SagaRepository`, `SagaDbContext`).
4.  **API:** REST endpoints.
5.  **ConsoleClient:** CLI tool for administrative tasks (Manual Saga Recovery/Creation).

## 🏃‍♂️ How to Run

### 1. Start Infrastructure
```bash
docker-compose up -d
```

### 2. Apply Migrations

```bash
dotnet ef database update --project src/SagaOrchestrator.Infrastructure --startup-project src/SagaOrchestrator.API
```

### 3. Run the API

```bash
dotnet run --project src/SagaOrchestrator.API
```
Open Swagger at: http://localhost:5091/swagger

### 4. Run the Admin CLI

```bash
dotnet run --project src/SagaOrchestrator.ConsoleClient
```

## 📝 License

MIT