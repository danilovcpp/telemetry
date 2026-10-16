# Развёртывание на VPS (Staging)

Гайд описывает, как собрать образ, опубликовать его в Docker Hub (`fkonstantin/telemetry`) и запустить сервис на VPS. PostgreSQL уже работает там в сети `infra`, письма отправляются через Gmail. На старте в окружении `Staging` сервис:

1. сам создаёт базу `device_telemetry` и применяет миграции;
2. создаёт 10 клиентов, 50 устройств и 2 шаблона писем; адреса клиентов указывают на ваш реальный ящик (см. 2.2);
3. пишет в лог API-ключи всех 50 устройств.

```
VPS, docker-сеть infra
├── postgres   (уже есть: host postgres, user/password postgres)
└── telemetry  (fkonstantin/telemetry, порт 8080) ──SMTP──► smtp.gmail.com:587
```

---

## Часть 1. Сборка образа и публикация в Docker Hub

Выполняется на машине разработчика из корня репозитория.

### 1.1. Токен Docker Hub

1. Войдите на https://hub.docker.com под пользователем `fkonstantin`.
2. **Account settings → Personal access tokens → Generate new token**, права **Read & Write**.
3. Сохраните токен: он показывается один раз и используется вместо пароля.

Репозиторий `fkonstantin/telemetry` создавать заранее не обязательно: при первом push он создастся как публичный. Если нужен приватный, создайте его заранее на Docker Hub (**Create repository → Private**) — тогда на VPS понадобится `docker login` (см. 2.4).

> Имя образа задано в `<ContainerRepository>` в `src/DeviceTelemetry.Api/DeviceTelemetry.Api.csproj` и в `image:` в compose-файле ниже. При смене репозитория меняйте оба места.

### 1.2. Сборка и публикация (рекомендуется: Docker не нужен)

Образ собирает .NET SDK. Получается один мультиархитектурный образ для `linux/amd64` и `linux/arm64` с тегами версии (сейчас `1.2.0`) и `latest`. Нужен только .NET SDK 10.

PowerShell:

```powershell
$env:DOTNET_CONTAINER_REGISTRY_UNAME = "fkonstantin"
$env:DOTNET_CONTAINER_REGISTRY_PWORD = "<access token из 1.1>"
dotnet publish src/DeviceTelemetry.Api -c Release -t:PublishContainer -p:ContainerRegistry=docker.io
Remove-Item Env:DOTNET_CONTAINER_REGISTRY_PWORD
```

bash:

```bash
DOTNET_CONTAINER_REGISTRY_UNAME=fkonstantin DOTNET_CONTAINER_REGISTRY_PWORD='<access token>' \
  dotnet publish src/DeviceTelemetry.Api -c Release -t:PublishContainer -p:ContainerRegistry=docker.io
```

В конце вывода должна быть строка о публикации образа `fkonstantin/telemetry:1.2.0, latest` в `docker.io`.

**Новая версия:** поменяйте `<Version>` в `DeviceTelemetry.Api.csproj` или добавьте `-p:Version=1.2.1`. Номер версии станет тегом образа, тег `latest` перезапишется.

### 1.3. Альтернатива: через Docker (Docker Desktop или buildx)

```bash
docker login -u fkonstantin                      # пароль = access token
docker buildx build --platform linux/amd64,linux/arm64 \
  -f src/DeviceTelemetry.Api/Dockerfile \
  -t fkonstantin/telemetry:1.2.0 -t fkonstantin/telemetry:latest \
  --push .
```

Если VPS только на amd64, достаточно `docker build -f src/DeviceTelemetry.Api/Dockerfile -t fkonstantin/telemetry:1.2.0 .` и затем `docker push fkonstantin/telemetry:1.2.0`.

### 1.4. Проверка

На https://hub.docker.com/r/fkonstantin/telemetry/tags должны появиться теги `1.2.0` и `latest` с архитектурами `linux/amd64` и `linux/arm64`.

---

## Часть 2. Развёртывание на VPS

Все команды выполняются на VPS по SSH.

### 2.1. Что уже должно работать

Контейнер PostgreSQL из вашего compose-файла запущен и подключён к внешней сети `infra`. Внутри этой сети он доступен по имени сервиса `postgres`. Проверка:

```bash
docker network inspect infra --format '{{range .Containers}}{{.Name}} {{end}}'
```

В выводе должен быть контейнер PostgreSQL. Отдельная сеть не нужна: telemetry подключится к той же `infra`.

База `device_telemetry` создастся автоматически. Пользователь `postgres` — суперпользователь, прав на это хватит. Ваша база `postgres` (`POSTGRES_DB`) не используется и не меняется.

> **Важно:** тестовые данные создаются, только если в базе `device_telemetry` нет ни одного клиента. Если такая база уже существует с данными (например, после прошлого запуска), seed будет пропущен и ключи не появятся в логе — см. 2.5, как пересоздать данные.

### 2.2. Gmail: пароль приложения

Обычный пароль Google-аккаунта для SMTP не подходит, нужен **пароль приложения**:

1. Включите двухэтапную аутентификацию: https://myaccount.google.com/signinoptions/two-step-verification.
2. Откройте https://myaccount.google.com/apppasswords, введите название (например, `telemetry-staging`) и нажмите **Создать**.
3. Скопируйте 16-символьный пароль **без пробелов** (Google показывает его группами по 4 символа).

Для аккаунтов Google Workspace пароли приложений должен разрешить администратор домена. Если пункта нет — обратитесь к администратору.

**Куда приходят письма.** Тестовые клиенты создаются с плюс-адресами ящика из `NOTIFY_EMAIL`: при `NOTIFY_EMAIL=me@gmail.com` клиент 1 получает адрес `me+customer01@gmail.com`, клиент 10 — `me+customer10@gmail.com`. Gmail доставляет все такие письма в ящик `me@gmail.com`, а по адресу получателя видно, какому клиенту оно ушло.

> Gmail может не показывать во «Входящих» письма, отправленные самому себе через SMTP: они видны в «Отправленных» и «Вся почта». Поэтому `NOTIFY_EMAIL` лучше указать **другой** ящик, не тот, с которого идёт отправка. Если ящик один и тот же — ищите письма запросом `to:me+customer01@gmail.com`.

Ограничение Gmail: около 500 писем в сутки для обычного аккаунта (2000 для Workspace). Каждое событие со статусом `100001` или `100002` — одно письмо.

### 2.3. Файлы на VPS

```bash
mkdir -p ~/telemetry && cd ~/telemetry
```

**`.env`** — секреты, лежат рядом с compose-файлом:

```bash
nano .env
```

```env
# Gmail-аккаунт, с которого отправляются письма
GMAIL_ADDRESS=sender@gmail.com
# Пароль приложения из шага 2.2, 16 символов без пробелов
GMAIL_APP_PASSWORD=abcdefghijklmnop
# Ящик, куда должны приходить уведомления тестовых клиентов
NOTIFY_EMAIL=me@gmail.com
```

```bash
chmod 600 .env
```

**`docker-compose.yml`:**

```bash
nano docker-compose.yml
```

```yaml
services:
  telemetry:
    image: fkonstantin/telemetry:1.2.0
    container_name: telemetry
    restart: unless-stopped
    ports:
      - "8080:8080"                     # внешний порт API: <порт на VPS>:8080
    environment:
      # Именно "Staging": включает миграции на старте, создание тестовых данных и логирование ключей.
      ASPNETCORE_ENVIRONMENT: "Staging"

      # Хост postgres — имя сервиса БД в сети infra. База device_telemetry создаётся автоматически.
      ConnectionStrings__TelemetryDatabase: "Host=postgres;Port=5432;Database=device_telemetry;Username=postgres;Password=postgres"

      # Gmail SMTP. Значения подставляются из .env; без них compose не запустится.
      Smtp__Host: "smtp.gmail.com"
      Smtp__Port: "587"
      Smtp__Security: "StartTls"
      Smtp__Username: "${GMAIL_ADDRESS:?укажите GMAIL_ADDRESS в .env}"
      Smtp__Password: "${GMAIL_APP_PASSWORD:?укажите GMAIL_APP_PASSWORD в .env}"
      Smtp__FromEmail: "${GMAIL_ADDRESS:?укажите GMAIL_ADDRESS в .env}"
      Smtp__FromName: "Device Telemetry (Staging)"

      # Адреса тестовых клиентов: NOTIFY_EMAIL с суффиксом +customerNN (см. 2.2).
      StagingSeed__RecipientEmail: "${NOTIFY_EMAIL:?укажите NOTIFY_EMAIL в .env}"
    networks:
      - infra

networks:
  infra:
    name: infra
    external: true                      # та же сеть, что у PostgreSQL
```

#### Что заполнить или проверить

| Где | Параметр | Значение | Когда менять |
|---|---|---|---|
| `.env` | `GMAIL_ADDRESS` | ваш Gmail-адрес | обязательно |
| `.env` | `GMAIL_APP_PASSWORD` | пароль приложения из 2.2 | обязательно |
| `.env` | `NOTIFY_EMAIL` | ящик для писем тестовых клиентов | обязательно |
| compose | `image` | `fkonstantin/telemetry:1.2.0` | при новой версии образа |
| compose | `ports` | `8080:8080` | если порт 8080 на VPS занят (меняйте только левую часть) |
| compose | `ASPNETCORE_ENVIRONMENT` | `Staging` | не менять: другое значение отключит seed |
| compose | `ConnectionStrings__TelemetryDatabase` | `Host=postgres;…;Username=postgres;Password=postgres` | если сменятся имя сервиса БД, пользователь или пароль |
| compose | `Smtp__FromEmail` | = `GMAIL_ADDRESS` | можно указать другой адрес, только если он добавлен в Gmail как «Отправлять письма от имени»; иначе Gmail подставит основной |

Необязательные параметры: значения по умолчанию подходят, менять только при необходимости. Добавляются в блок `environment`:

| Переменная | По умолчанию | Назначение |
|---|---|---|
| `Smtp__Timeout` | `00:00:30` | таймаут соединения с SMTP |
| `Outbox__PollingInterval` | `00:00:05` | как часто проверяется очередь писем |
| `Outbox__MaxAttempts` | `8` | число попыток отправки письма |
| `Outbox__BaseRetryDelay` / `Outbox__MaxRetryDelay` | `00:00:30` / `01:00:00` | задержки между повторами |
| `Telemetry__MaxPayloadBytes` | `8192` | максимальный размер `payload` |
| `Kestrel__Limits__MaxRequestBodySize` | `32768` | максимальный размер тела запроса |
| `RateLimiting__Telemetry__PermitLimit` / `__Window` | `600` / `00:01:00` | лимит запросов телеметрии с одного IP |
| `RateLimiting__Mobile__PermitLimit` / `__Window` | `120` / `00:01:00` | лимит запросов к мобильному API с одного IP |
| `Logging__LogLevel__Default` | `Information` | уровень логов |

### 2.4. Запуск

Перед первым запуском убедитесь, что VPS может подключаться к Gmail. Некоторые хостинги по умолчанию блокируют исходящий SMTP:

```bash
timeout 5 bash -c '</dev/tcp/smtp.gmail.com/587' && echo "587 открыт" || echo "587 закрыт"
```

Если порт закрыт — откройте его в настройках хостинга или фаервола либо используйте порт 465 (см. таблицу неполадок).

Для приватного репозитория выполните `docker login -u fkonstantin` (пароль — access token).

```bash
cd ~/telemetry
docker compose config --quiet && echo "конфигурация в порядке"   # проверит, что .env заполнен
docker compose up -d
docker compose logs -f telemetry
```

Ожидаемые строки:

```
info: Microsoft.EntityFrameworkCore.Migrations[20402]
      Applying migration '20261016121531_InitialSchema'.
warn: DeviceTelemetry.Infrastructure.Persistence.Seeding.StagingDataSeeder[0]
      Staging seed created 10 customers (me+customer01@gmail.com - me+customer10@gmail.com) and 50 devices and notification templates for status codes 100001, 100002. ...
warn: DeviceTelemetry.Infrastructure.Persistence.Seeding.StagingDataSeeder[0]
      Staging API key for device stg-device-001 (customer Staging Customer 01): dtk_...
...
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://[::]:8080
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Staging
```

При самом первом старте перед `Applying migration` может появиться строка `fail: Microsoft.EntityFrameworkCore.Database.Connection[20004] … database 'device_telemetry'`. Это нормально: EF проверяет базу, которой ещё нет, и затем создаёт её.

### 2.5. Сохранение API-ключей

**Ключи выводятся только при первом старте**, потому что в базе хранятся только их хеши. Сразу сохраните их в файл:

```bash
docker compose logs --no-log-prefix telemetry | grep "Staging API key" | sed 's/^ *//' > ~/telemetry/device-keys.txt
chmod 600 ~/telemetry/device-keys.txt
wc -l ~/telemetry/device-keys.txt                 # должно быть 50
```

После `docker compose down` или пересоздания контейнера старые логи пропадут, а ключи не будут выведены повторно (данные уже есть). Новый ключ для устройства можно выдать в любой момент; старый при этом перестаёт работать:

```bash
docker compose run --rm telemetry issue-device-key stg-device-001
```

Получить все ключи заново (или применить новый `NOTIFY_EMAIL`) можно только пересозданием тестовых данных — удалением базы `device_telemetry`. Имя контейнера PostgreSQL можно посмотреть в выводе команды из 2.1:

```bash
docker compose stop telemetry
docker exec -it <контейнер-postgres> psql -U postgres -c 'DROP DATABASE device_telemetry WITH (FORCE);'
docker compose start telemetry                     # база, данные и ключи создадутся заново
```

### 2.6. Проверка работы

Отправка телеметрии (ключ устройства `stg-device-001` из `device-keys.txt`):

```bash
KEY=$(grep 'stg-device-001 ' ~/telemetry/device-keys.txt | grep -o 'dtk_[A-Za-z0-9_-]*')

curl -i -X POST http://localhost:8080/api/telemetry/send \
  -H "Content-Type: application/json" \
  -H "X-Device-Key: $KEY" \
  -d '{"deviceId":"stg-device-001","statusCode":100001,"payload":{"temperature":25.4}}'
```

Ожидается `HTTP/1.1 202 Accepted` с `"notificationQueued":true`. Для статуса без шаблона (например, `200`) ответ тоже 202, но с `"notificationQueued":false` — письмо не отправляется.

Через несколько секунд в ящик `NOTIFY_EMAIL` придёт письмо «Error 100001 on Staging Sensor 001» на адрес `…+customer01@…`. В логе появится строка:

```bash
docker compose logs telemetry | grep "Outbox message"
#   Outbox message ... for event ... sent on attempt 1 in 850 ms
```

Состояние очереди писем:

```bash
docker exec -it <контейнер-postgres> psql -U postgres -d device_telemetry \
  -c 'SELECT "Status", "Attempts", "LastError", "CreatedAtUtc" FROM "OutboxMessages" ORDER BY "CreatedAtUtc" DESC LIMIT 10;'
```

`Sent` — письмо принято Gmail. `Pending` с `LastError` — будет повтор. `Failed` — попытки исчерпаны, причина указана в `LastError`.

Тестовые данные:

| Что | Значения |
|---|---|
| Клиенты | `Staging Customer 01` … `10`, адреса `<NOTIFY_EMAIL с +customer01>` … `+customer10` |
| Устройства | `stg-device-001` … `stg-device-050`; устройство N принадлежит клиенту ((N−1) mod 10) + 1 |
| Шаблоны | `100001` — HTML-письмо об ошибке, `100002` — текстовое письмо о низком заряде |

Подробное описание API (коды ответов, `messageId`, `occurredAtUtc`, `payload`) — в [README.md](README.md).

---

## Обновление и обслуживание

```bash
cd ~/telemetry
# обновить версию: поменять тег в image: (например, 1.2.1), затем
docker compose pull && docker compose up -d
docker compose logs --tail=100 telemetry
docker compose restart telemetry
docker compose down          # остановить; данные в PostgreSQL сохраняются
```

Миграции новых версий применяются автоматически при старте. Seed при обновлении не повторяется.

## Если что-то не работает

| Симптом | Причина и решение |
|---|---|
| `docker compose up`: `required variable GMAIL_... is missing` | не заполнен `.env` или он лежит не в одной папке с `docker-compose.yml` |
| `network infra declared as external, but could not be found` | сеть `infra` не создана или называется иначе: `docker network ls` |
| `Name or service not known` для `postgres` | контейнер БД не в сети `infra` или его сервис называется не `postgres` (проверка — 2.1) |
| `password authentication failed for user "postgres"` | неверный пароль в `ConnectionStrings__TelemetryDatabase` |
| `OptionsValidationException ... Smtp` или `... StagingSeed` | некорректный `GMAIL_ADDRESS` или `NOTIFY_EMAIL` |
| `Staging seed skipped: the database already contains customers` | база уже с данными; ключи — в `device-keys.txt` или через `issue-device-key`, пересоздание — 2.5 |
| Нет строк `Staging seed` | `ASPNETCORE_ENVIRONMENT` не равен `Staging` (проверьте написание) |
| В логе `Outbox message ... failed ... SMTP authentication failed`, в `LastError` есть `535 5.7.8` | неверный пароль приложения или в `GMAIL_APP_PASSWORD` попали пробелы; создайте новый пароль (2.2), исправьте `.env`, затем `docker compose up -d` |
| `SMTP connection failed: ... timed out` | хостинг блокирует порт 587. Попробуйте 465: `Smtp__Port: "465"`, `Smtp__Security: "SslOnConnect"`; либо попросите хостинг открыть исходящий SMTP |
| `SMTP server replied 550` / `554`, сообщение в `Failed` сразу | Gmail отклонил письмо (лимит, подозрение на спам). Подробности — в `LastError` |
| Письма уходят (`Sent`), но их нет во «Входящих» | отправка самому себе: смотрите «Отправленные» и «Вся почта» либо укажите другой `NOTIFY_EMAIL` (2.2) |
| `exec format error` | архитектура образа не совпадает с VPS: соберите мультиархитектурный образ (1.2 или 1.3) |
| Ответ `401 device.authentication_failed` | неверный ключ или `deviceId`; ключ должен соответствовать именно этому устройству |

Временные ошибки SMTP не теряют письма: они повторяются с растущей задержкой до 8 попыток, примерно 1,5 часа. Если исправить настройки в этот промежуток, письма уйдут сами.

## Безопасность

- Этот стенд — тестовый. Ключи устройств лежат в логах контейнера и в `device-keys.txt`. Не используйте Staging-окружение для реальных устройств.
- Пароль приложения Gmail даёт полный доступ к отправке почты от вашего имени. Храните его только в `.env` (`chmod 600`), не коммитьте, а при утечке отзовите на https://myaccount.google.com/apppasswords.
- API работает по HTTP на порту 8080, а устройства передают ключ в заголовке. Для доступа извне поставьте перед сервисом reverse proxy с HTTPS (nginx, Caddy, Traefik) и откройте наружу только его.
- Пароль `postgres/postgres` годится только для изолированного стенда. Не публикуйте порт 5432 контейнера БД наружу.
