# Установка сервера

Сервер, веб-интерфейс, движок коллизий и PostgreSQL работают на одном Linux-хосте в Docker. Агенты подключаются к этому хосту сами — открывать порты на машинах с Revit не нужно.

## Что нужно

- Linux-хост с **Docker Engine** и **Docker Compose v2** (`docker compose version`).
- **4 ядра и 8 ГБ памяти** — на эти значения рассчитаны лимиты по умолчанию. Для больших частей (сотни тысяч элементов) движку коллизий нужно 8 ГБ: задайте `CLASH_ENGINE_MEMORY=8g` и добавьте хосту памяти.
- Диск под слепки коллизий: **число моделей × размер слепка × число выгрузок за 14 дней**. Слепки удачных утверждённых прогонов младше 14 дней не удаляются; остальные — по настройке «сколько последних выгрузок хранить» (**Настройки → Хранение и пороги коллизий**). Сверх этого — база и кеш движка (до 30 ГБ).
- Порт для веб-интерфейса, по умолчанию **8080**. К этому же адресу подключаются агенты, поэтому машины с Revit должны его видеть.
- Желательно — обратный прокси с HTTPS перед портом, см. [HTTPS](#https).

## Сервисы и тома

| Сервис | Что делает |
|---|---|
| `postgres` | База данных |
| `server` | API, расписания, очередь заданий, картинки коллизий |
| `clash-engine` | Движок коллизий: берёт у сервера прогоны по одному |
| `web` | nginx: веб-интерфейс и прокси к серверу |

| Том | Что хранит |
|---|---|
| `toadflow_postgres` | База |
| `toadflow_clash` | Слепки моделей, картинки коллизий, архивы отчётов |
| `toadflow_clash-engine-cache` | Кеш слепков движка; его можно удалить — движок скачает слепки заново |

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
docker compose ps                          # четыре сервиса; server — healthy
curl http://localhost:8080/api/health      # {"status":"ok","db":"ok","clashVolumeFreeGb":…}
```

`/api/health` отвечает 503, если сервер не видит базу. По этому ответу Docker считает контейнер `server` здоровым; `web` и `clash-engine` стартуют только после этого.

Откройте `http://<хост>:8080` и войдите как `ADMIN_LOGIN` / `ADMIN_PASSWORD`. Дальше — [Первый запуск](Getting-started).

> [!NOTE]
> Первый администратор создаётся, только пока пользователей в базе нет. Если потом поменять `ADMIN_PASSWORD` в `.env`, пароль существующего администратора не изменится: его меняют в **Настройки → Пользователи**.

Миграции базы применяются автоматически при старте сервера.

## Переменные `.env`

| Переменная | Назначение |
|---|---|
| `POSTGRES_PASSWORD` | Пароль базы данных |
| `JWT_KEY` | Ключ подписи токенов входа, не короче 32 символов: `openssl rand -base64 48`. При смене все пользователи выйдут из системы |
| `CLASH_ENGINE_KEY` | Общий ключ сервера и движка коллизий, не короче 32 символов: `openssl rand -base64 48`. Обязателен |
| `ADMIN_LOGIN`, `ADMIN_PASSWORD` | Первый администратор |
| `WEB_PORT` | Порт веб-интерфейса на хосте, по умолчанию 8080 |
| `CLASH_ENGINE_CPUS` | Ядер у движка коллизий, по умолчанию 2. Не больше, чем у хоста, иначе контейнер не запустится |
| `CLASH_ENGINE_MEMORY` | Память движка, по умолчанию `4g` |
| `SERVER_MEMORY` | Память сервера, по умолчанию `2g` |
| `CLASH_IMAGE_THREADS` | Сколько картинок коллизий сервер рисует одновременно, по умолчанию 2 |

Часовой пояс расписаний, таймауты и срок хранения логов задаются в интерфейсе: **Настройки → Общие**.

## Вход

- Галочка **«Оставаться в системе»** на странице входа продлевает вход на 30 дней. Без неё вход заканчивается вместе с токеном входа. На общем компьютере галочку не ставят.
- Смена пароля или роли и отключение пользователя завершают все его входы.
- С одного адреса — не больше 10 попыток входа в минуту и 5 заявок на регистрацию за 10 минут; лишние получают ответ 429. Адрес клиента сервер берёт из `X-Forwarded-For`, только если запрос пришёл из частной сети: от nginx контейнера `web` или прокси в локальной сети.

## Обновление

```bash
cd toadflow
git pull
cd deploy
docker compose up -d --build
```

Данные хранятся в томах и переживают пересборку контейнеров. Агенты переподключаются сами. Если вышла новая версия агента, обновите его на машинах с Revit — см. [Установка агента](Agent#обновление-и-удаление); устаревший агент помечается на странице **Настройки → Агенты**.

### С 1.7.1 на 2.0.0

Версия 2.0.0 добавляет коллизии: сервис `clash-engine`, том `clash`, обязательную переменную `CLASH_ENGINE_KEY` и миграцию базы. Обратной миграции нет, поэтому сначала — копия базы.

1. Выключите в **Расписаниях** те, что сработают во время обновления.
2. Сделайте копию базы, пока сервер ещё 1.7.1:
   ```bash
   cd toadflow/deploy
   docker compose exec -T postgres pg_dump -U toadflow toadflow | gzip > /backup/toadflow-1.7.1-$(date +%F).sql.gz
   ```
3. Получите код 2.0.0 и добавьте в `.env` переменные из [таблицы](#переменные-env): обязательно `CLASH_ENGINE_KEY`, лимиты — если у хоста не 4 ядра и 8 ГБ.
   ```bash
   cd .. && git fetch --tags && git checkout 2.0.0 && cd deploy
   nano .env
   docker compose config > /dev/null    # ошибки нет — переменные заданы
   ```
4. Соберите и запустите:
   ```bash
   docker compose up -d --build
   docker compose ps                     # server — healthy, clash-engine — running
   docker compose logs server            # миграция Clash2_0 применена, ошибок нет
   ```
5. Обновите агентов до 2.0.0. Задания коллизий получают только агенты 2.0.0 и новее; агенты 1.7.1 продолжают выгружать NWC и проверять LOI.
6. Включите расписания.

### Откат

Откат возвращает код и данные на момент копии; всё, что сделано в 2.0.0 после обновления, пропадает.

```bash
cd toadflow/deploy
docker compose down
cd .. && git checkout 1.7.1 && cd deploy
docker volume rm toadflow_postgres
docker compose up -d postgres
gunzip -c /backup/toadflow-1.7.1-YYYY-MM-DD.sql.gz | docker compose exec -T postgres psql -U toadflow toadflow
docker compose up -d --build
```

Без копии 1.7.1 можно запустить на базе 2.0.0: новые таблицы и столбцы ей не мешают. Роль «Проектировщик» и заявки на регистрацию 1.7.1 не знает — до её старта их убирают:

```sql
UPDATE users SET role = 'Viewer' WHERE role = 'Designer';
DELETE FROM users WHERE is_pending;
```

Перед этим сверьте схему: таблица `users` и значения столбца `role` должны быть такими, как ожидает 1.7.1. Запись `…_Clash2_0` в `__EFMigrationsHistory` остаётся; 1.7.1 её не трогает, и при повторном обновлении миграция не выполнится второй раз.

Агентов 2.0.0 откатывать не нужно: они работают и с сервером 1.7.1.

## Резервная копия

Копия — это база и том `clash`. Сначала снимается база, потом том: база ссылается на слепки, и в томе, снятом позже, есть все слепки, на которые она ссылается.

```bash
cd toadflow/deploy
docker compose exec -T postgres pg_dump -U toadflow toadflow | gzip > /backup/toadflow-$(date +%F).sql.gz
docker run --rm -v toadflow_clash:/d -v /backup:/b alpine tar czf /b/clash-$(date +%F).tgz --exclude=exports --exclude=uploads -C /d .
```

`exports` (готовые архивы отчётов, живут сутки) и `uploads` (недописанные загрузки агентов) в копию не входят.

Восстановление на пустые тома:

```bash
docker compose up -d postgres
gunzip -c /backup/toadflow-YYYY-MM-DD.sql.gz | docker compose exec -T postgres psql -U toadflow toadflow
docker run --rm -v toadflow_clash:/d -v /backup:/b alpine tar xzf /b/clash-YYYY-MM-DD.tgz -C /d
docker compose up -d
```

Если тома нет, коллизии и проверки сохраняются в базе, а 3D, картинки и новые прогоны ждут свежих слепков: выгрузите модели заново (запустите расписания выгрузки для коллизий).

Ежедневная копия — две строки в `crontab -e`:

```text
0 3 * * * cd /opt/toadflow/deploy && docker compose exec -T postgres pg_dump -U toadflow toadflow | gzip > /backup/toadflow-$(date +\%F).sql.gz
30 3 * * * docker run --rm -v toadflow_clash:/d -v /backup:/b alpine tar czf /b/clash-$(date +\%F).tgz --exclude=exports --exclude=uploads -C /d .
```

## HTTPS

Сервер отдаёт HTTP. Если интерфейс доступен за пределами доверенной сети, перед портом `WEB_PORT` ставят обратный прокси с TLS (nginx, Caddy, Traefik). Прокси должен:

- пропускать `/hubs/` с WebSocket (`Upgrade` / `Connection`) и держать соединение не меньше часа: через него подключены браузеры и агенты;
- принимать тело запроса до **16 МБ**: агенты загружают слепки моделей частями по 8 МБ. nginx по умолчанию принимает 1 МБ и отвечает на большее 413.

nginx на хосте:

```nginx
server {
    listen 443 ssl;
    server_name toadflow.example.ru;
    ssl_certificate     /etc/ssl/toadflow.crt;
    ssl_certificate_key /etc/ssl/toadflow.key;
    client_max_body_size 16m;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 3600s;
    }
}
```

Caddy:

```text
toadflow.example.ru {
    request_body {
        max_size 16MB
    }
    reverse_proxy 127.0.0.1:8080
}
```

Traefik по умолчанию размер тела не ограничивает. Если на маршруте есть middleware `buffering`, её `maxRequestBodyBytes` — не меньше `16777216`.

Агентов тогда подключают по адресу `https://toadflow.example.ru`.

## Логи и диагностика

```bash
docker compose logs -f server         # сервер: расписания, задания, подключения агентов
docker compose logs -f clash-engine   # движок коллизий: прогоны, ошибки ключа и связи с сервером
docker compose logs -f web            # nginx
docker compose restart server         # перезапуск сервера без потери данных
```

Если прогоны коллизий ждут в очереди, а движок к серверу не обращается, веб пишет «Движок не отвечает N мин» в строке прогона и на «Обзоре проверок». См. [Частые проблемы](Troubleshooting#коллизии).

## Удаление

```bash
cd toadflow/deploy
docker compose down        # остановить, данные сохранятся
docker compose down -v     # остановить и удалить базу, слепки и кеш движка
```
