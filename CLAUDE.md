# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                                            # whole solution, should be 0 warnings
dotnet run --project src/DeviceTelemetry.Api            # http://localhost:5146; Development auto-applies migrations
dotnet run --project src/DeviceTelemetry.Api -- migrate                      # apply migrations and exit
dotnet run --project src/DeviceTelemetry.Api -- issue-device-key <deviceCode> # print a new device API key once
dotnet run --project src/DeviceTelemetry.Api -- sync-templates               # overwrite reference email templates (DefaultTemplates)
dotnet run --project src/DeviceTelemetry.Api -- set-customer-language <id|all> <en|ru|de|->
dotnet test tests/DeviceTelemetry.UnitTests
dotnet test tests/DeviceTelemetry.IntegrationTests      # real PostgreSQL, see below
dotnet test tests/DeviceTelemetry.IntegrationTests --filter "FullyQualifiedName~SendTelemetryCommandHandlerTests"
dotnet tool restore && dotnet ef migrations add <Name> --project src/DeviceTelemetry.Infrastructure --output-dir Persistence/Migrations
docker build -f src/DeviceTelemetry.Api/Dockerfile .    # repo root as build context
```

Handler, silence-monitor and mobile query tests are integration tests (real PostgreSQL): the handlers use transactions and `ExecuteUpdate`, which EF InMemory does not support, so there is no InMemory provider in the solution. Integration tests use `DEVICETELEMETRY_TEST_POSTGRES` (a server connection string with CREATEDB rights) if set, otherwise Testcontainers (requires Docker). Each run creates and drops its own database. Migrations are created through `DesignTimeDbContextFactory` in Infrastructure, so no database is needed for that.

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
- A 400 is final for a device, so only invalid *content* is rejected. Invalid metadata (`occurredAtUtc` in the future, `notifyEmail`, `reportIntervalS`) is ignored and kept on the event in `Rejected*` columns; the event is accepted. Keep it that way for new auxiliary fields.
- Idempotency hash compatibility: `ComputeContentHash` keeps the original three lines; later fields (only `channels` so far) are appended only when sent, so hashes of older firmware and of events stored earlier never change. `notifyEmail`/`reportIntervalS` are never hashed.
- Device state transitions (`LastSeenAtUtc`, `NotifyEmail`, `ReportIntervalS`, `SilentSinceUtc`) are set-based `ExecuteUpdateAsync` statements inside `IApplicationDbContext.BeginTransactionAsync`, in the same transaction as the event. The telemetry handler updates the device row first (row lock) so the silence monitor and concurrent messages cannot race. `DetectSilentDevicesCommandHandler` marks with a conditional update (`SilentSinceUtc IS NULL AND LastSeenAtUtc = <read value>`). Do not replace these with tracked-entity updates.
- Status codes 190000–199999 are server-only (`TelemetryStatusCodes`); devices sending them get 400. Server events have `Source = Server`, no `messageId`, and go through `TelemetryNotifier` like device events. Notification recipient: `Device.NotifyEmail`, else the customer's email.
- Device settings: `POST /api/telemetry/settings` (`UpdateDeviceSettingsCommand`) and the `notifyEmail`/`notifyLanguage`/`reportIntervalS` fields of telemetry messages share `DeviceSettingsResolver` (validation: invalid values are rejected per field, never a 400) and `DeviceContactRecorder` (one locked device-row update inside the caller's transaction: last contact, settings, end of silence → 190002, effective address change → 190003 emailed to the *previous* address). Last write wins. Never put email addresses into event payloads (public mobile API).
- Email language: `Device.NotifyLanguage` (protocol field `notifyLanguage`, same rules as `notifyEmail`) → `Customer.Language` → `NotificationLanguages.Default` (en). Templates are unique per (`StatusCode`, `Language`) with NULLS NOT DISTINCT; `Language = NULL` is a neutral fallback. `TelemetryNotifier` picks requested language → default → neutral, and the most specific existing template decides even when inactive.
- Email templates: reference set in `Infrastructure/Persistence/Seeding/DefaultTemplates.cs`, one per code per supported language (no template for 100010 on purpose). Placeholders are `{Name}`; `{{`/`}}` are escaped braces. `{Channels}` is the only optional placeholder (`EmailTemplateRenderer.OptionalPlaceholders`).
- Never log `SendTelemetryCommand.DeviceKey`, `EmailMessage` contents, SMTP credentials or payloads. The command is a class rather than a record so it has no auto-generated `ToString`. The only exception is `StagingDataSeeder`: it runs only when the environment is Staging and the database is empty, and logs the generated device keys once by design.
- The container image is built with `dotnet publish -t:PublishContainer` (settings are in `DeviceTelemetry.Api.csproj`); Docker is not required. Runtime configuration lives in `.env`, which is git-ignored; keep `.env.example` in sync when adding settings.
- Mobile read API (`MobileController`, `Application/Mobile/Queries/*`) is deliberately `[AllowAnonymous]` for the MVP; keep that explicit. Read handlers use `AsNoTracking` + `Select` projections into DTOs, never entities. Event history uses keyset pagination on (`OccurredAtUtc` DESC, `Id` DESC), matching index `IX_TelemetryEvents_DeviceId_OccurredAtUtc_Id`. Do not project a "first related row" as an object in EF (`x.Events....Select(e => new {...}).FirstOrDefault()`): Npgsql turns it into `ROW_NUMBER()` over the whole table. Use scalar subqueries instead. `MobileQueriesTests` asserts the generated SQL.
- `docs/mobile-api.openapi.json` is generated, not hand-written: after changing mobile endpoints or DTOs, run the API in Development and `python scripts/export-mobile-openapi.py`, then update `docs/mobile-api.md`. `RawJsonSchemaTransformer` keeps the document honest (free-form `payload`, `code`/`errors` on problem details). JSON number handling is `Strict` in both MVC and HTTP JSON options; the OpenAPI generator reads the latter.
- Outbox delivery is at-least-once. The outbox id is used as the SMTP `Message-ID`.
- `TelemetryNotifier` records its decision on the event (`TelemetryEvent.Notification`: Queued/Disabled/NoTemplate/NoAddress/Failed, plus language and subject; a subject containing `@` is not stored). The mobile history maps Queued to queued/sent/failed by the outbox row (scalar subqueries, index on `TelemetryEventId`). `TelemetryNotifier.SelectTemplate` is the one template-choice rule; `/notifications` reuses it. A second index on the same columns as an existing one must be named (`HasIndex(…, "IX_…")`), or EF replaces the existing one.
- Device Link (`Infrastructure/DeviceLink`, endpoints in `Api/Http/DeviceLinkEndpoints.cs`): WebSocket `/api/device/v1/link`, authenticated by `X-Device-Id` + `X-Device-Key` before the upgrade (`AuthenticateDeviceQuery`). `DeviceLinkRegistry` (one session per device, a newer one closes the older with 4409), `LiveHub` (one device stream for all viewers; viewers are reserved before the upgrade for the 429 limits and `Activate`d after it; at most 2 queued frames per viewer, values dropped before statuses), `LogSynchronizer` (one `file.read` at a time; storage write before the conditional metadata update; devices may answer with fewer bytes than asked). All state is in process memory: single instance only. Never log live values, file contents or frames.
- Device Link features are off by default (`DeviceLink:Enabled`, `MobileApi:LiveEnabled`, `MobileApi:HistoryEnabled`); only Development/Staging turn them on, because the mobile API has no auth. `MobileFeatures.For` computes the card's `features` and gates the live/logs/data routes (`404 feature.not_available`). `/data/{path}` looks the path up in `DeviceLogFiles` only; `LogFileStorage` re-validates and confines it to `<DataRoot>/<deviceId>/`.
- `tests/DeviceTelemetry.IntegrationTests/DeviceLink` runs the real API through `WebApplicationFactory` (own database, short timings, outbox/silence workers removed) against `FakeDevice`. `scripts/device-link-emulator.py` is the manual counterpart.
