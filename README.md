# DeviceTelemetry

Сервис приёма телеметрии от устройств. Каждое сообщение сохраняется в PostgreSQL. Если для кода статуса есть активный шаблон, клиент-владелец устройства получает Email: письмо ставится в transactional outbox и отправляется фоновым процессом.

## Как обрабатывается сообщение

1. `POST /api/telemetry/send`: контроллер проверяет контракт запроса (обязательные поля, формат JSON, временная зона, payload — JSON-объект) и передаёт `SendTelemetryCommand` обработчику.
2. Обработчик (`SendTelemetryCommandHandler`) проверяет бизнес-правила (размер payload, допустимое время события) и одним запросом загружает устройство по `DeviceCode` вместе с именем и адресом клиента.
3. API-ключ из заголовка `X-Device-Key` сверяется с хешем, сохранённым у устройства.
4. Если передан `messageId`, обработчик ищет уже принятое событие (подробности — в разделе «Идемпотентность»).
5. Создаётся `TelemetryEvent`. Если для статуса есть активный шаблон и у клиента указан корректный Email, письмо формируется сразу, и в `OutboxMessages` записываются готовые адрес, тема и тело.
6. Событие и запись Outbox сохраняются одним `SaveChanges`, то есть в одной транзакции. Ключи — GUID v7, сгенерированные приложением: id события известен до сохранения, поэтому отдельная транзакция не нужна.
7. Ответ — `202 Accepted` с `eventId`, `receivedAtUtc` и `notificationQueued`. Письмо в этот момент только поставлено в очередь.
8. `OutboxBackgroundService` раз в `Outbox:PollingInterval` захватывает пачку сообщений (`UPDATE … FOR UPDATE SKIP LOCKED` с lease) и отправляет их по одному через MailKit. При ошибке назначается повтор с экспоненциальной задержкой; после `Outbox:MaxAttempts` попыток сообщение получает статус `Failed`, а `LastError` хранит последнюю ошибку.

Ошибки уведомлений (нет шаблона, у клиента нет Email, ошибка в шаблоне, сбой SMTP) никогда не откатывают сохранённое событие.

## HTTP API

```http
POST /api/telemetry/send
Content-Type: application/json
X-Device-Key: dtk_...

{ "deviceId": "uid", "statusCode": 100001 }
```

Необязательные поля: `messageId` (GUID), `occurredAtUtc` (ISO 8601 с `Z` или смещением; по умолчанию — время получения), `payload` (JSON-объект, по умолчанию не больше 8 KB; хранится как `jsonb`).

| Статус | Когда |
|---|---|
| 202 | событие сохранено |
| 200 | повтор с тем же `messageId` и тем же содержимым; возвращается исходное событие (`duplicate: true`) |
| 400 | невалидный запрос |
| 401 | устройство не зарегистрировано или ключ неверен (ответ одинаковый, чтобы нельзя было перебирать коды устройств) |
| 409 | `messageId` уже использован с другим содержимым |
| 413 | тело больше `Kestrel:Limits:MaxRequestBodySize` (32 KB) |
| 429 | превышен лимит запросов с одного IP (`RateLimiting:Telemetry`) |
| 503 | PostgreSQL временно недоступен (есть `Retry-After`) |

Ошибки возвращаются в формате RFC 9457 Problem Details с полем `code`, например `device.authentication_failed`.

### Идемпотентность

Уникальный индекс `(DeviceId, MessageId)` защищает инвариант даже при параллельных повторах. Повтор с тем же содержимым (статус, переданное устройством `occurredAtUtc`, payload) возвращает исходное событие и не создаёт нового уведомления. Если содержимое другое, возвращается 409, исходное событие не меняется. Сообщения без `messageId` всегда считаются новыми.

## Шаблоны писем

Таблица `Templates`: не больше одного шаблона на `StatusCode`. Уведомление отправляется, только если у шаблона `IsActive = true`. При `IsHtml = true` подставляемые значения HTML-экранируются.

Поддерживаемые плейсхолдеры: `{DeviceId}` и `{DeviceCode}` (внешний код устройства), `{DeviceName}`, `{CustomerName}`, `{StatusCode}`, `{EventId}`, `{OccurredAtUtc}`, `{ReceivedAtUtc}`. Литеральные фигурные скобки записываются как `{{` и `}}`.

Неизвестный плейсхолдер, незакрытая скобка или пустое значение считаются ошибкой шаблона. Письмо в этом случае не создаётся, событие сохраняется, а в лог пишется ошибка `template.invalid` с перечнем проблем. Пустыми строками ничего не заменяется.

Тема и тело письма фиксируются в момент постановки в очередь, поэтому последующие правки шаблона не меняют уже поставленные письма.

## Аутентификация устройств

У каждого устройства свой API-ключ (256 бит). В БД хранится только SHA-256-хеш ключа, а `DeviceCode` секретом не считается. Выдать ключ (старый при этом перестаёт работать):

```bash
dotnet run --project src/DeviceTelemetry.Api -- issue-device-key <deviceCode>
```

Ключ выводится один раз. Его нужно передавать только по HTTPS: TLS терминируется перед сервисом (reverse proxy или ingress). Если сервис стоит за прокси, настройте forwarded headers, иначе rate limit будет считать всех клиентов одним IP.

## Конфигурация

| Секция | Назначение |
|---|---|
| `ConnectionStrings:TelemetryDatabase` | PostgreSQL |
| `Smtp` | `Host`, `Port`, `Security` (`StartTls`/`SslOnConnect`/`None`), `Username`, `Password`, `FromEmail`, `FromName`, `Timeout` |
| `Outbox` | `BatchSize`, `PollingInterval`, `MaxAttempts`, `BaseRetryDelay`, `MaxRetryDelay`, `LockDuration` |
| `Telemetry` | `MaxPayloadBytes`, `MaxClockSkew` |
| `RateLimiting:Telemetry` | `PermitLimit`, `Window` |
| `Kestrel:Limits:MaxRequestBodySize` | максимальный размер тела запроса |

Настройки проверяются при запуске: без SMTP-хоста или корректного `FromEmail` приложение не стартует. Секреты в репозиторий не кладутся. Для локальной разработки используйте user secrets:

```bash
dotnet user-secrets --project src/DeviceTelemetry.Api set "ConnectionStrings:TelemetryDatabase" "Host=localhost;Database=device_telemetry;Username=postgres;Password=..."
dotnet user-secrets --project src/DeviceTelemetry.Api set "Smtp:Password" "..."
```

В production передавайте настройки через переменные окружения (`ConnectionStrings__TelemetryDatabase`, `Smtp__Host`, `Smtp__Username`, `Smtp__Password`, `Smtp__FromEmail`) или хранилище секретов.

### Локальный почтовый сервер

`appsettings.Development.json` ожидает SMTP без TLS на `localhost:1025`, например Mailpit (веб-интерфейс — http://localhost:8025):

```bash
docker run -d -p 1025:1025 -p 8025:8025 axllent/mailpit
```

## Миграции

```bash
dotnet tool restore                                                     # dotnet-ef из dotnet-tools.json
dotnet ef migrations add <Name> --project src/DeviceTelemetry.Infrastructure --output-dir Persistence/Migrations
dotnet run --project src/DeviceTelemetry.Api -- migrate                 # применить к базе из конфигурации
```

В Development миграции применяются автоматически при старте (`Database:ApplyMigrationsOnStartup`). В production запускайте `migrate` отдельным шагом развёртывания до старта экземпляров. Альтернатива — `dotnet ef migrations bundle`.

## Запуск и тесты

```bash
dotnet run --project src/DeviceTelemetry.Api
dotnet test tests/DeviceTelemetry.UnitTests
dotnet test tests/DeviceTelemetry.IntegrationTests
```

Интеграционные тесты работают с настоящим PostgreSQL. Если задана переменная `DEVICETELEMETRY_TEST_POSTGRES` (строка подключения с правом создавать базы), тесты используют этот сервер; иначе Testcontainers запускает `postgres:17-alpine`, для чего нужен Docker. Каждый прогон создаёт и затем удаляет отдельную базу.

## Ограничения и гарантии

- **Email доставляется по принципу at-least-once.** Если SMTP-сервер принял письмо, а процесс упал до отметки `Sent`, письмо будет отправлено ещё раз после истечения lease. В оба письма записывается одинаковый `Message-ID` (id outbox-записи), по которому почтовые системы могут распознать дубликат. Гарантии exactly-once у SMTP нет.
- Письма со статусом `Failed` повторно не отправляются автоматически. Найти их можно запросом `SELECT * FROM "OutboxMessages" WHERE "Status" = 'Failed'`; чтобы отправить заново, установите `Status = 'Pending'`, `Attempts = 0`.
- Отправленные записи Outbox и история событий пока не удаляются. Политику хранения и очистки нужно добавить до роста объёмов.
- Управления устройствами, клиентами и шаблонами через API нет. Данные заводятся SQL-запросами, ключи выдаются командой `issue-device-key`.
