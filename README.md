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

Ошибки уведомлений (нет шаблона, нет адреса, ошибка в шаблоне, сбой SMTP) никогда не откатывают сохранённое событие. Получатель письма — `Device.NotifyEmail` (адрес, который сообщил сам прибор), иначе email клиента.

**Контроль молчания.** `SilenceMonitorBackgroundService` раз в `SilenceMonitor:CheckInterval` находит устройства с известным `ReportIntervalS`, от которых нет сообщений дольше `max(3 × интервал, 10 мин)`, и создаёт серверное событие `190001`. Первое сообщение после этого создаёт `190002`. Подробно — в [docs/device-protocol.md](docs/device-protocol.md).

## HTTP API

Протокол устройства подробно, включая правила его расширения, — [docs/device-protocol.md](docs/device-protocol.md); задание для прошивки — [docs/firmware-integration.md](docs/firmware-integration.md). Настройки прибора (имя, адрес и язык уведомлений, часовой пояс, интервал отчёта, порог отключения, справочник каналов) — `POST /api/telemetry/settings`.

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

## Мобильный API (чтение)

Задание для разработчика мобильного приложения — [docs/mobile-app-integration.md](docs/mobile-app-integration.md); справочник API — [docs/mobile-api.md](docs/mobile-api.md), OpenAPI — [docs/mobile-api.openapi.json](docs/mobile-api.openapi.json). Ниже — краткая сводка.

> **Временно публичный (MVP).** Endpoint-ы `/api/mobile/*` работают без аутентификации и явно помечены `[AllowAnonymous]`. `customerId` и `deviceId` в URL не доказывают права доступа: любой, кто достучится до сервиса, может прочитать устройства и историю любого клиента, перебирая числовые id. Перебор только замедляет лимит запросов с одного IP (`RateLimiting:Mobile`, по умолчанию 120 в минуту). До публичного production-запуска нужны аутентификация пользователя и проверка, что запрошенный клиент или устройство принадлежат ему.

Все id здесь внутренние: `customerId` = `Customer.Id`, `deviceId` = `Device.Id` (поле `id` из списка устройств), а не `DeviceCode`. Ошибки — в том же формате Problem Details с полем `code`. Нечисловой id в пути даёт 400. При `ASPNETCORE_ENVIRONMENT=Development` полное описание лежит в OpenAPI: `GET /openapi/v1.json`, тег «Mobile (temporarily public)».

| Endpoint | Ответ | Ошибки |
|---|---|---|
| `GET /api/mobile/customers/{customerId}/devices` | устройства клиента `[{id, deviceCode, name}]`, сортировка по `name`, затем по `id`; клиент без устройств — `[]` | 404 `customer.not_found` |
| `GET /api/mobile/devices/{deviceId}` | `{id, deviceCode, name, customerId, lastEventAtUtc, lastStatusCode}` | 404 `device.not_found` |
| `GET /api/mobile/devices/{deviceId}/events?limit=&cursor=` | страница `{items, nextCursor, hasMore}` | 400 `pagination.invalid_limit` / `pagination.invalid_cursor`, 404 `device.not_found` |
| WebSocket `GET /api/mobile/devices/{deviceId}/live` | кадры `status` и `values` (живые значения через Device Link) | 404 `feature.not_available`, 429 |
| `GET /api/mobile/devices/{deviceId}/logs?ch=&month=` | `{months}` или `{days:[{path,size,complete}]}` | 400 `logs.invalid_*`, 404 `feature.not_available` / `log.not_found` |
| `GET`/`HEAD /api/mobile/devices/{deviceId}/data/{path}` | байты копии файла, `Range`/`ETag` | 404, 416, 503 `sync.pending` / `device.offline` |

**История событий** сортируется по `occurredAtUtc` по убыванию, затем по `id` по убыванию. `limit` — от 1 до 100, по умолчанию 20; значение вне диапазона даёт 400, а не тихую подрезку. Первая страница запрашивается без `cursor`. Пока `hasMore = true`, передавайте полученный `nextCursor` без изменений. Курсор непрозрачный: это позиция последнего события страницы (`occurredAtUtc`, `id`), поэтому события с одинаковым временем не теряются и не повторяются. Используется keyset-пагинация, а не `OFFSET`: стоимость страницы не растёт с глубиной. `payload` отдаётся JSON-объектом в том виде, в каком его прислало устройство, или `null`.

```http
GET /api/mobile/devices/15/events?limit=2
```
```json
{
  "items": [
    { "id": "01a1455b-94b5-70f3-aa9c-7a54c081237d", "statusCode": 200,
      "occurredAtUtc": "2026-10-16T10:05:00Z", "receivedAtUtc": "2026-10-16T15:36:32.18105Z",
      "payload": { "t": "10:05:00Z", "v": 3.3 } },
    { "id": "01a1455b-9485-758a-8877-2392d5e71be6", "statusCode": 200,
      "occurredAtUtc": "2026-10-16T10:05:00Z", "receivedAtUtc": "2026-10-16T15:36:32.133399Z",
      "payload": null }
  ],
  "nextCursor": "CN8rbPBsbgABoUVblIV1ioh3I5LV5xvm",
  "hasMore": true
}
```

**Карточка устройства.** `lastEventAtUtc` и `lastStatusCode` берутся из самого позднего события по времени устройства (`occurredAtUtc`). Это последний **известный** статус, а не гарантированное текущее состояние: heartbeat-контракта нет, а событие, буферизованное устройством и пришедшее позже, на карточку не влияет. Если событий нет, оба поля равны `null`.

**Список устройств не пагинируется:** устройства заводятся вручную, и у клиента их немного. Если это изменится, пагинацию нужно добавить так же, через курсор.

## Device Link (живые значения и файлы данных)

Приборы SG Manager держат WebSocket `/api/device/v1/link` (заголовки `X-Device-Id`, `X-Device-Key`). Через него сервер просит живые значения для мобильных зрителей (`LiveHub`: один поток прибора на всех зрителей, продление аренды каждые 30 с, `live.stop` через 10 с после ухода последнего) и копирует файлы данных (`LogSynchronizer`: при подключении и каждые 5 минут, первый раз — полностью, дальше — хвосты; файл, который ждёт пользователь, — вне очереди). Копии лежат в `Storage:DataRoot/<deviceId>/<path>`, метаданные — в `DeviceLogFiles`. Соединения хранятся в памяти процесса: сервис рассчитан на один экземпляр. Протокол — [docs/device-protocol.md, раздел 7](docs/device-protocol.md#7-device-link-v1-16); эмулятор прибора — `scripts/device-link-emulator.py`.

Функции выключены по умолчанию и включаются только в Development и Staging: мобильный API пока без аутентификации. В production `features` в карточке пустой, а маршруты live/logs/data отвечают `404 feature.not_available`.

## Шаблоны писем

Таблица `Templates`: не больше одного шаблона на пару `StatusCode` + `Language` (`en`, `ru`, `de` или `NULL` — без языка). Язык письма — язык устройства (`notifyLanguage` в протоколе), иначе язык клиента (`Customer.Language`), иначе английский. Шаблон выбирается на языке письма, иначе английский, иначе без языка; если выбранный шаблон выключен (`IsActive = false`), письма нет. При `IsHtml = true` подставляемые значения HTML-экранируются.

Поддерживаемые плейсхолдеры: `{DeviceId}` и `{DeviceCode}` (внешний код устройства), `{DeviceName}`, `{CustomerName}`, `{StatusCode}`, `{EventId}`, `{OccurredAtUtc}`, `{ReceivedAtUtc}`, `{Channels}` (`ch2 (130AA15P20), ch5 (130AA15P21)`). Литеральные фигурные скобки записываются как `{{` и `}}`.

Неизвестный плейсхолдер, незакрытая скобка или пустое значение считаются ошибкой шаблона. Исключение — `{Channels}`: без списка каналов он даёт пустую строку, потому что старые прошивки шлют события о каналах без списка. Письмо в этом случае не создаётся, событие сохраняется, а в лог пишется ошибка `template.invalid` с перечнем проблем. Пустыми строками ничего не заменяется.

Эталонный набор шаблонов (коды `100000`–`100008`, `190001`, `190002` на английском, русском и немецком; для регулярного отчёта `100010` шаблона нет намеренно) задан в `DefaultTemplates`. Немецкие тексты стоит вычитать носителю языка. Язык клиента задаётся командой `set-customer-language <customerId|all> <en|ru|de|->`. Staging добавляет недостающие при каждом старте. Команда `sync-templates` перезаписывает эталонные коды в любой базе и перечисляет остальные:

```bash
dotnet run --project src/DeviceTelemetry.Api -- sync-templates
```

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
| `RateLimiting:Telemetry` | `PermitLimit`, `Window` — приём телеметрии, на IP |
| `RateLimiting:Mobile` | `PermitLimit` (120), `Window` (1 мин) — мобильный API, на IP |
| `SilenceMonitor` | `CheckInterval` (1 мин), `ThresholdIntervals` (3), `MinThreshold` (10 мин) — контроль молчания |
| `StagingSeed` | `RecipientEmail` — ящик для писем тестовых клиентов (только Staging) |
| `DeviceLink` | `Enabled` (false), `PingInterval` (30 с), `PongTimeout` (60 с), `HelloTimeout` (30 с) |
| `MobileApi` | `LiveEnabled`, `HistoryEnabled` (false; true в Development и Staging) |
| `Live` | `PeriodMs` (1000), `StopDelayS` (10), `RenewIntervalS` (30), `MaxViewersPerIp` (4), `MaxViewersPerDevice` (20) |
| `Sync` | `IntervalS` (300), `BackfillPauseMs` (250), `LivePauseMs` (1000), `ReadLength` (8192), `RequestTimeout` (30 с) |
| `Storage` | `DataRoot` (`data`; в контейнере `/data`) — копии файлов данных |
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

## Staging: тестовые данные

При `ASPNETCORE_ENVIRONMENT=Staging` приложение при старте применяет миграции (`appsettings.Staging.json`) и, если в базе ещё нет ни одного клиента, создаёт:

- 10 клиентов `Staging Customer 01…10` с адресами `customerNN@example.com`;
- 50 устройств `stg-device-001…050`, по 5 на клиента, у каждого свой API-ключ;
- эталонные шаблоны писем (`DefaultTemplates`) — при каждом старте, только для отсутствующих кодов.

**Ключи выводятся в лог один раз — при первом заполнении**, потому что в БД хранятся только их хеши. Дальнейшие перезапуски ничего не меняют. Если ключи потеряны, выдайте новый командой `issue-device-key` или пересоздайте базу. Несколько экземпляров, стартующих одновременно, заполняют базу один раз (используется advisory lock). В Development и Production тестовые данные не создаются.

```bash
docker compose logs api | grep "Staging API key"
```

## Docker-образ и `.env`

`.env` (в git не попадает, шаблон — `.env.example`) содержит все переменные окружения сервиса: окружение, строку подключения, SMTP, Outbox, лимиты, логирование. Пароль PostgreSQL сгенерирован случайно. Значения пишутся без кавычек, так этот файл читают `docker run --env-file` и `docker compose`.

Образ `fkonstantin/telemetry` (теги `$(Version)` и `latest`, linux/amd64 и linux/arm64, базовый образ `mcr.microsoft.com/dotnet/aspnet:10.0`, непривилегированный пользователь, порт 8080) собирается средствами .NET SDK, Docker daemon для этого не нужен. Публикация в Docker Hub и развёртывание на VPS рядом с существующим контейнером PostgreSQL — по шагам в [DEPLOY.md](DEPLOY.md).

Локальный архив образа без публикации:

```bash
dotnet publish src/DeviceTelemetry.Api -c Release -t:PublishContainer   -p:ContainerArchiveOutputPath=artifacts/telemetry-1.1.0.tar
```

`docker-compose.yml` в корне репозитория поднимает полный стенд из `.env`: API, собственный PostgreSQL и Mailpit (веб-интерфейс почты — http://localhost:8025):

```bash
docker compose up -d
```

## Ограничения и гарантии

- **Email доставляется по принципу at-least-once.** Если SMTP-сервер принял письмо, а процесс упал до отметки `Sent`, письмо будет отправлено ещё раз после истечения lease. В оба письма записывается одинаковый `Message-ID` (id outbox-записи), по которому почтовые системы могут распознать дубликат. Гарантии exactly-once у SMTP нет.
- Письма со статусом `Failed` повторно не отправляются автоматически. Найти их можно запросом `SELECT * FROM "OutboxMessages" WHERE "Status" = 'Failed'`; чтобы отправить заново, установите `Status = 'Pending'`, `Attempts = 0`.
- Отправленные записи Outbox и история событий пока не удаляются. Политику хранения и очистки нужно добавить до роста объёмов.
- Управления устройствами, клиентами и шаблонами через API нет. Данные заводятся SQL-запросами, ключи выдаются командой `issue-device-key`.
