# Развёртывание на VPS (Staging)

Гайд описывает, как собрать образ, опубликовать его в Docker Hub (`fkonstantin/telemetry`) и запустить сервис на VPS рядом с уже работающим контейнером PostgreSQL. На старте в окружении `Staging` сервис:

1. сам создаёт базу `device_telemetry` и применяет миграции;
2. создаёт 10 клиентов, 50 устройств и 2 шаблона писем;
3. пишет в лог API-ключи всех 50 устройств.

```
VPS
├── postgres   (уже есть; host: postgres, user/password: postgres)
├── telemetry  (fkonstantin/telemetry, порт 8080)
└── mailpit    (тестовый SMTP: перехватывает письма; веб-интерфейс на 127.0.0.1:8025)
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

Образ собирает .NET SDK. Получается один мультиархитектурный образ для `linux/amd64` и `linux/arm64` с тегами `1.0.0` и `latest`. Нужен только .NET SDK 10.

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

В конце вывода должна быть строка о публикации образа `fkonstantin/telemetry:1.0.0, latest` в `docker.io`.

**Новая версия:** добавьте `-p:Version=1.0.1`. Тот же номер станет тегом образа, тег `latest` перезапишется.

### 1.3. Альтернатива: через Docker (Docker Desktop или buildx)

```bash
docker login -u fkonstantin                       # пароль = access token
docker buildx build --platform linux/amd64,linux/arm64 \
  -f src/DeviceTelemetry.Api/Dockerfile \
  -t fkonstantin/telemetry:1.0.0 -t fkonstantin/telemetry:latest \
  --push .
```

Если VPS только на amd64, достаточно `docker build -f src/DeviceTelemetry.Api/Dockerfile -t fkonstantin/telemetry:1.0.0 .` и затем `docker push fkonstantin/telemetry:1.0.0`.

### 1.4. Проверка

На https://hub.docker.com/r/fkonstantin/telemetry/tags должны появиться теги `1.0.0` и `latest` с архитектурами `linux/amd64` и `linux/arm64`.

---

## Часть 2. Развёртывание на VPS

Все команды выполняются на VPS по SSH.

### 2.1. Проверка базы данных

```bash
docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Networks}}'
```

Найдите контейнер PostgreSQL. Его имя понадобится в 2.2; ниже предполагается, что он называется `postgres`.

База `device_telemetry` создастся автоматически. Пользователь `postgres` — суперпользователь, прав на это хватит.

> **Важно:** тестовые данные создаются, только если в базе `device_telemetry` нет ни одного клиента. Если такая база уже существует с данными, seed будет пропущен и ключи не появятся в логе.

### 2.2. Общая сеть для API и PostgreSQL

Контейнер API найдёт хост `postgres`, только если оба контейнера подключены к одной пользовательской Docker-сети. Создайте сеть и подключите к ней существующий контейнер БД с алиасом `postgres`:

```bash
docker network create telemetry-db
docker network connect --alias postgres telemetry-db postgres   # последний аргумент — имя контейнера из 2.1
```

Проверка — в выводе должно быть имя контейнера БД:

```bash
docker network inspect telemetry-db --format '{{range .Containers}}{{.Name}} {{end}}'
```

> `docker network connect` не переживает **пересоздание** контейнера БД (`docker compose up` с изменениями, `docker rm`). Обычный перезапуск (`docker restart`) не страшен. Если PostgreSQL поднят через свой compose-файл, надёжнее добавить сеть туда:
>
> ```yaml
> services:
>   postgres:
>     networks: [default, telemetry-db]
> networks:
>   telemetry-db:
>     external: true
> ```

### 2.3. docker-compose.yml

```bash
mkdir -p ~/telemetry && cd ~/telemetry
nano docker-compose.yml
```

Вставьте содержимое:

```yaml
services:
  telemetry:
    image: fkonstantin/telemetry:1.0.0
    container_name: telemetry
    restart: unless-stopped
    ports:
      - "8080:8080"                     # внешний порт API: <порт на VPS>:8080
    environment:
      # Именно "Staging": включает миграции на старте, создание тестовых данных и логирование ключей.
      ASPNETCORE_ENVIRONMENT: "Staging"

      # База создаётся автоматически. Host = алиас контейнера БД из шага 2.2.
      ConnectionStrings__TelemetryDatabase: "Host=postgres;Port=5432;Database=device_telemetry;Username=postgres;Password=postgres"

      # Почта. По умолчанию все письма перехватывает mailpit (см. 2.6); для настоящей отправки — раздел 3.
      Smtp__Host: "mailpit"
      Smtp__Port: "1025"
      Smtp__Security: "None"
      Smtp__Username: ""
      Smtp__Password: ""
      Smtp__FromEmail: "telemetry@example.com"
      Smtp__FromName: "Device Telemetry (Staging)"
    networks:
      - default
      - telemetry-db
    depends_on:
      - mailpit

  mailpit:
    image: axllent/mailpit:v1.27
    container_name: telemetry-mailpit
    restart: unless-stopped
    ports:
      - "127.0.0.1:8025:8025"           # веб-интерфейс только локально, доступ через SSH-туннель

networks:
  telemetry-db:
    external: true                      # сеть из шага 2.2
```

#### Что заполнить или проверить

| Параметр | Значение по умолчанию | Когда менять |
|---|---|---|
| `image` | `fkonstantin/telemetry:1.0.0` | при новой версии или другом логине Docker Hub |
| `ports` у `telemetry` | `8080:8080` | если порт 8080 на VPS занят (меняйте только левую часть) |
| `ASPNETCORE_ENVIRONMENT` | `Staging` | не менять: другое значение отключит seed |
| `ConnectionStrings__TelemetryDatabase` | `Host=postgres;…;Username=postgres;Password=postgres` | если отличаются алиас, порт, пользователь или пароль БД |
| `Smtp__*` | mailpit | для отправки настоящих писем (раздел 3) |
| `Smtp__FromEmail` | `telemetry@example.com` | обязательно корректный адрес; при настоящем SMTP — адрес вашего домена |
| `networks.telemetry-db` | `telemetry-db` | если в 2.2 вы назвали сеть иначе |

Необязательные параметры: значения по умолчанию подходят, менять только при необходимости. Добавляются в тот же блок `environment`:

| Переменная | По умолчанию | Назначение |
|---|---|---|
| `Outbox__PollingInterval` | `00:00:05` | как часто фоновый процесс проверяет очередь писем |
| `Outbox__MaxAttempts` | `8` | число попыток отправки письма |
| `Outbox__BaseRetryDelay` / `Outbox__MaxRetryDelay` | `00:00:30` / `01:00:00` | задержки между повторами |
| `Telemetry__MaxPayloadBytes` | `8192` | максимальный размер `payload` |
| `Kestrel__Limits__MaxRequestBodySize` | `32768` | максимальный размер тела запроса |
| `RateLimiting__Telemetry__PermitLimit` / `__Window` | `600` / `00:01:00` | лимит запросов с одного IP |
| `Logging__LogLevel__Default` | `Information` | уровень логов |

### 2.4. Запуск

Для приватного репозитория сначала выполните `docker login -u fkonstantin` (пароль — access token).

```bash
docker compose up -d
docker compose ps
```

Первый старт:

```bash
docker compose logs -f telemetry
```

Ожидаемые строки:

```
info: Microsoft.EntityFrameworkCore.Migrations[20402]
      Applying migration '20261016121531_InitialSchema'.
warn: DeviceTelemetry.Infrastructure.Persistence.Seeding.StagingDataSeeder[0]
      Staging seed created 10 customers and 50 devices and notification templates for status codes 100001, 100002. ...
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

Получить все ключи заново можно только полным пересозданием тестовых данных — удалением базы:

```bash
docker compose stop telemetry
docker exec -it postgres psql -U postgres -c 'DROP DATABASE device_telemetry WITH (FORCE);'
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

Ожидается `HTTP/1.1 202 Accepted` с `"notificationQueued":true`. Для статуса без шаблона (например, `200`) ответ тоже 202, но с `"notificationQueued":false`.

Письма, перехваченные mailpit: на своём компьютере откройте туннель и зайдите на http://localhost:8025.

```bash
ssh -L 8025:127.0.0.1:8025 <user>@<vps-ip>
```

В течение примерно 5 секунд после запроса там появится письмо для `customer01@example.com` с темой «Error 100001 on Staging Sensor 001».

Тестовые данные:

| Что | Значения |
|---|---|
| Клиенты | `Staging Customer 01` … `10`, email `customer01@example.com` … `customer10@example.com` |
| Устройства | `stg-device-001` … `stg-device-050`; устройство N принадлежит клиенту ((N−1) mod 10) + 1 |
| Шаблоны | `100001` — HTML-письмо об ошибке, `100002` — текстовое письмо о низком заряде |

Подробное описание API (коды ответов, `messageId`, `occurredAtUtc`, `payload`) — в [README.md](README.md).

---

## Часть 3. Настоящая отправка писем (необязательно)

Если письма должны уходить реальным получателям, замените блок `Smtp__*` данными вашего почтового провайдера, а сервис `mailpit` и `depends_on` можно удалить:

```yaml
      Smtp__Host: "smtp.example.com"
      Smtp__Port: "587"
      Smtp__Security: "StartTls"          # для порта 465: "SslOnConnect"
      Smtp__Username: "<логин SMTP>"
      Smtp__Password: "<пароль SMTP>"
      Smtp__FromEmail: "telemetry@your-domain.com"
      Smtp__FromName: "Device Telemetry"
```

Учтите, что тестовые клиенты имеют адреса `@example.com` — письма на них не будут доставлены. Пароль SMTP не стоит хранить в compose-файле, если он лежит в git. Создайте рядом файл `.env` (`chmod 600`) со строкой `SMTP_PASSWORD=...` и укажите в compose `Smtp__Password: "${SMTP_PASSWORD}"`.

---

## Обновление и обслуживание

```bash
cd ~/telemetry
# обновить версию: поменять тег в image: (например, 1.0.1), затем
docker compose pull && docker compose up -d
docker compose logs --tail=100 telemetry
docker compose restart telemetry
docker compose down          # остановить; данные в PostgreSQL сохраняются
```

Миграции новых версий применяются автоматически при старте. Seed при обновлении не повторяется.

## Если что-то не работает

| Симптом в `docker compose logs telemetry` | Причина и решение |
|---|---|
| `Name or service not known` / `Resource temporarily unavailable` для `postgres` | API и БД не в одной сети: повторите шаг 2.2, проверьте `docker network inspect telemetry-db` |
| `password authentication failed for user "postgres"` | неверный пароль в `ConnectionStrings__TelemetryDatabase` |
| `OptionsValidationException ... Smtp` | не заполнены `Smtp__Host` или `Smtp__FromEmail`, либо `FromEmail` — некорректный адрес |
| `Staging seed skipped: the database already contains customers` | база уже содержит данные; ключи — в сохранённом файле или через `issue-device-key` (2.5) |
| Нет строк `Staging seed` | `ASPNETCORE_ENVIRONMENT` не равен `Staging` (проверьте написание) |
| Контейнер постоянно перезапускается | смотрите первую ошибку в `docker compose logs telemetry`; пока БД недоступна, сервис падает и `restart: unless-stopped` повторяет запуск |
| `exec format error` | архитектура образа не совпадает с VPS: соберите мультиархитектурный образ (1.2 или 1.3) |
| Ответ `401 device.authentication_failed` | неверный ключ или `deviceId`; ключ должен соответствовать именно этому устройству |

## Безопасность

- Этот стенд — тестовый. Ключи устройств лежат в логах контейнера и в `device-keys.txt`; не используйте Staging-окружение для реальных устройств.
- API работает по HTTP на порту 8080, а устройства передают ключ в заголовке. Для доступа извне поставьте перед сервисом reverse proxy с HTTPS (nginx, Caddy, Traefik) и откройте наружу только его.
- Пароль `postgres/postgres` годится только для изолированного стенда. Не публикуйте порт 5432 контейнера БД наружу.
