# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                                            # whole solution, should be 0 warnings
dotnet run --project src/DeviceTelemetry.Api            # http://localhost:5146; Development auto-applies migrations
dotnet run --project src/DeviceTelemetry.Api -- migrate                      # apply migrations and exit
dotnet run --project src/DeviceTelemetry.Api -- issue-device-key <deviceCode> # print a new device API key once
dotnet test tests/DeviceTelemetry.UnitTests
dotnet test tests/DeviceTelemetry.IntegrationTests      # real PostgreSQL, see below
dotnet test tests/DeviceTelemetry.UnitTests --filter "FullyQualifiedName~SendTelemetryCommandHandlerTests"
dotnet tool restore && dotnet ef migrations add <Name> --project src/DeviceTelemetry.Infrastructure --output-dir Persistence/Migrations
docker build -f src/DeviceTelemetry.Api/Dockerfile .    # repo root as build context
```

Integration tests use `DEVICETELEMETRY_TEST_POSTGRES` (a server connection string with CREATEDB rights) if set, otherwise Testcontainers (requires Docker). Each run creates and drops its own database. Migrations are created through `DesignTimeDbContextFactory` in Infrastructure, so no database is needed for that.

## Build configuration

- .NET 10. `Directory.Build.props` sets the TFM, nullable, implicit usings and `InvariantGlobalization` (the unit test project turns it off to test culture independence).
- Central package management with transitive pinning: versions live **only** in `Directory.Packages.props`. Pins there override vulnerable or misaligned transitive packages (`System.Security.Cryptography.Pkcs`, `Microsoft.OpenApi`, `Microsoft.EntityFrameworkCore.Relational`). Never put `Version=` in a `.csproj`.

## Architecture

Clean Architecture: `Api → Application → Domain`, `Api → Infrastructure → Application`.

- **Domain**: `Customer` 1─* `Device` 1─* `TelemetryEvent`, plus `Template`. `BaseAuditableEntity` holds audit fields only; each entity declares its own key (`TelemetryEvent.Id` is an application-generated GUID v7). Status codes are `int` everywhere.
- **Application**: a hand-rolled CQRS layer (no MediatR) in `Common/`. Handlers return `Result<T>`, and expected failures are `DomainError`s (dot-separated `Code` plus a `DomainErrorType` that maps to HTTP status), never exceptions. `AddApplication()` registers handlers and FluentValidation validators by reflection. Handlers call their validator explicitly; there is no pipeline.
  - `IApplicationDbContext` exposes the EF `DbSet`s directly. There is no repository or unit-of-work layer. `OutboxMessage` lives in `Application/Outbox` so that handlers can enqueue in the same `SaveChanges` call.
  - The persistence layer translates unique violations into `UniqueConstraintViolationException`, so handlers stay provider-agnostic.
  - `EmailTemplateRenderer` is a concrete class (no interface) with an explicit placeholder list and strict failure on unknown placeholders.
- **Infrastructure**: `ApplicationDbContext` (Npgsql) with Fluent configurations in `Persistence/Configurations`, migrations in `Persistence/Migrations`, and audit timestamps from `TimeProvider`. `Outbox/` holds `PostgresOutboxStore`, which claims with raw SQL `UPDATE … FOR UPDATE SKIP LOCKED` plus a lease, and `OutboxProcessor`, which holds the retry and backoff logic and is unit-tested against `InMemoryOutboxStore`. `OutboxBackgroundService` creates one DI scope per batch. `EmailSender` (MailKit) maps SMTP failures to `EmailSendException.IsTransient`.
- **Api**: `TelemetryController` (`POST /api/telemetry/send`) maps the request DTO to the command and turns `Result` into 202/200 or problem details via `DomainErrorResults.Problem`. Exception handlers turn `BadHttpRequestException` into its own status and transient Npgsql errors into 503. The per-IP rate limiter policy is `telemetry`.

## Conventions and invariants

- Never send email inside a request. Queue an `OutboxMessage` with fully rendered content, in the same `SaveChanges` as the business change.
- Notification problems (no template, no email, template error) are logged and must never prevent the telemetry event from being saved.
- Idempotency relies on the unique index `(DeviceId, MessageId)` and `ContentHash`. Do not replace it with a pre-check `SELECT`.
- Unknown devices and wrong keys deliberately return the same 401 `device.authentication_failed`; the distinction appears only in logs.
- Never log `SendTelemetryCommand.DeviceKey`, `EmailMessage` contents, SMTP credentials or payloads. The command is a class rather than a record so it has no auto-generated `ToString`. The only exception is `StagingDataSeeder`: it runs only when the environment is Staging and the database is empty, and logs the generated device keys once by design.
- The container image is built with `dotnet publish -t:PublishContainer` (settings are in `DeviceTelemetry.Api.csproj`); Docker is not required. Runtime configuration lives in `.env`, which is git-ignored; keep `.env.example` in sync when adding settings.
- Outbox delivery is at-least-once. The outbox id is used as the SMTP `Message-ID`.
