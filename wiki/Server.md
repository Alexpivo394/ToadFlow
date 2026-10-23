# Установка сервера

Сервер, веб-интерфейс и PostgreSQL работают на одном Linux-хосте в Docker. Агенты подключаются к этому хосту сами — открывать порты на машинах с Revit не нужно.

## Что нужно

- Linux-хост с **Docker Engine** и **Docker Compose v2** (`docker compose version`).
- 2 ГБ памяти и несколько гигабайт на диске: логи выгрузок хранятся в базе.
- Порт для веб-интерфейса, по умолчанию **8080**. К этому же адресу подключаются агенты, поэтому машины с Revit должны его видеть.
- Желательно — обратный прокси с HTTPS перед портом, см. [HTTPS](#https).

## Первый запуск

```bash
git clone https://github.com/Alexpivo394/ToadFlow.git toadflow
cd toadflow/deploy
cp .env.example .env
nano .env        # заполните переменные, см. ниже
docker compose up -d --build
```

Первая сборка образов занимает несколько минут. Проверить, что всё поднялось:

```bash
docker compose ps                          # три сервиса в состоянии running / healthy
curl http://localhost:8080/api/health      # {"status":"ok"}
```

Откройте `http://<хост>:8080` и войдите как `ADMIN_LOGIN` / `ADMIN_PASSWORD`. Дальше — [Первый запуск](Getting-started).

> [!NOTE]
> Первый администратор создаётся, только пока пользователей в базе нет. Если потом поменять `ADMIN_PASSWORD` в `.env`, пароль существующего администратора не изменится: его меняют в **Настройки → Пользователи**.

Миграции базы применяются автоматически при старте сервера.

## Переменные `.env`

| Переменная | Назначение |
|---|---|
| `POSTGRES_PASSWORD` | Пароль базы данных |
| `JWT_KEY` | Ключ подписи токенов входа, не короче 32 символов: `openssl rand -base64 48`. При смене все пользователи выйдут из системы |
| `ADMIN_LOGIN`, `ADMIN_PASSWORD` | Первый администратор |
| `WEB_PORT` | Порт веб-интерфейса на хосте, по умолчанию 8080 |

Часовой пояс расписаний, таймауты и срок хранения логов задаются в интерфейсе: **Настройки → Общие**.

## Обновление

```bash
cd toadflow
git pull
cd deploy
docker compose up -d --build
```

Данные хранятся в томе `toadflow_postgres` и переживают пересборку контейнеров. Агенты переподключаются сами. Если вышла новая версия агента, обновите его на машинах с Revit — см. [Установка агента](Agent#обновление-и-удаление); устаревший агент помечается на странице **Настройки → Агенты**.

## Резервная копия

```bash
cd toadflow/deploy
docker compose exec postgres pg_dump -U toadflow toadflow | gzip > toadflow-$(date +%F).sql.gz
```

Восстановление на пустой том:

```bash
docker compose up -d postgres
gunzip -c toadflow-YYYY-MM-DD.sql.gz | docker compose exec -T postgres psql -U toadflow toadflow
docker compose up -d
```

Для ежедневной копии достаточно строки в `crontab -e`:

```text
0 3 * * * cd /opt/toadflow/deploy && docker compose exec -T postgres pg_dump -U toadflow toadflow | gzip > /backup/toadflow-$(date +\%F).sql.gz
```

## HTTPS

Сервер отдаёт HTTP. Если интерфейс доступен за пределами доверенной сети, поставьте перед портом `WEB_PORT` обратный прокси с TLS (nginx, Caddy, Traefik). Путь `/hubs/` прокси должен пропускать с WebSocket (`Upgrade` / `Connection`) и держать соединение не меньше часа: через него подключены браузеры и агенты.

Пример для nginx на хосте:

```nginx
server {
    listen 443 ssl;
    server_name toadflow.example.ru;
    ssl_certificate     /etc/ssl/toadflow.crt;
    ssl_certificate_key /etc/ssl/toadflow.key;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 3600s;
    }
}
```

Агентов тогда подключают по адресу `https://toadflow.example.ru`.

## Логи и диагностика

```bash
docker compose logs -f server     # сервер: расписания, задания, подключения агентов
docker compose logs -f web        # nginx
docker compose restart server     # перезапуск сервера без потери данных
```

## Удаление

```bash
cd toadflow/deploy
docker compose down        # остановить, данные сохранятся
docker compose down -v     # остановить и удалить базу
```
