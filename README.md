<p align="center">
    <h1 align="center">ToadFlow</h1>
    <p align="center">Сервис автоматизации работы с моделями Revit по расписанию</p>
</p>

ToadFlow по расписанию выполняет задачи над моделями Revit на Windows-машинах, где стоит агент ToadFlow. Расписания, история, логи и коллизии — в браузере.

| Модуль | Что делает |
|---|---|
| **Выгрузка RVT / NWC** | Сохраняет модели в RVT и NWC в сетевую папку; пресеты — как у плагина ToadExporter |
| **Проверка LOI** | Проверяет заполнение параметров по профилям ParamChecker, пишет отчёт Excel по каждой модели и ведёт аналитику |
| **Коллизии** | Ищет пересечения между моделями разделов без Navisworks: проверки с матрицей допусков, база коллизий со статусами и историей, отчёты Excel и HTML, рабочие места проектировщиков, панель в Revit через [ToadTools](https://github.com/Alexpivo394/ToadTools) |

- Модель открывается в Revit **своей** версии (2020–2027) и **не обновляется**.
- Несколько Revit на машину, в том числе разных версий; зависший или упавший Revit агент убивает сам, и модель обрабатывается повторно.
- Модели с Revit Server и из сетевых папок.
- Пошаговый лог каждой модели; видно, кто создал и менял расписание, кто запустил выгрузку и кто убил Revit.
- Роли: проектировщик, наблюдатель, координатор, администратор.

```
Linux (docker compose)                       Windows-машины с Revit
┌──────────────────────────────┐            ┌──────────────────────────────┐
│ web — интерфейс + nginx      │            │ ToadFlow Agent               │
│ server — API, очередь        │◄─ SignalR ─┤  ├─ Revit 2022 + аддин       │
│ clash-engine — поиск коллизий│  (исходящее│  └─ Revit 2024 + аддин       │
│ postgres — данные            │  от агента)└──────────────┬───────────────┘
└──────────────────────────────┘                           ▼
                                              сетевая папка результатов
```

## Быстрый старт

**Сервер** — Linux с Docker, 4 ядра и 8 ГБ памяти:

```bash
git clone https://github.com/Alexpivo394/ToadFlow.git toadflow
cd toadflow/deploy
cp .env.example .env    # POSTGRES_PASSWORD, JWT_KEY, CLASH_ENGINE_KEY, ADMIN_PASSWORD
docker compose up -d --build
```

Интерфейс — `http://<хост>:8080`, вход — `ADMIN_LOGIN` и `ADMIN_PASSWORD` из `.env`.

**Агент** — Windows с Revit. В интерфейсе **Настройки → Агенты → Подключить агента**, затем установщик из [релизов](https://github.com/Alexpivo394/ToadFlow/releases):

```powershell
msiexec /i ToadFlowAgent-2.0.0.msi SERVER=http://toadflow:8080 CODE=TF-XXXX-XXXX
```

Обновление с 1.7.1 на 2.0.0 требует копии базы и новой переменной `CLASH_ENGINE_KEY` — порядок в вики: [С 1.7.1 на 2.0.0](https://github.com/Alexpivo394/ToadFlow/wiki/Server#с-171-на-200).

## Документация

Вся документация — в [вики](https://github.com/Alexpivo394/ToadFlow/wiki):

| Страница | Что там |
|---|---|
| [Установка сервера](https://github.com/Alexpivo394/ToadFlow/wiki/Server) | `.env`, обновление и откат, резервные копии, HTTPS |
| [Установка агента](https://github.com/Alexpivo394/ToadFlow/wiki/Agent) | Требования к машине, автовход, обновление |
| [Первый запуск](https://github.com/Alexpivo394/ToadFlow/wiki/Getting-started) | Пользователи, источники, пресеты, первое расписание |
| [Коллизии](https://github.com/Alexpivo394/ToadFlow/wiki/Clash) | Выгрузка, проверки, статусы, отчёты |
| [Проектировщику](https://github.com/Alexpivo394/ToadFlow/wiki/Designer) | Работа с коллизиями в браузере и в Revit |
| [Частые проблемы](https://github.com/Alexpivo394/ToadFlow/wiki/Troubleshooting) | Агент, Revit, модели, LOI, коллизии |
| [Разработка](https://github.com/Alexpivo394/ToadFlow/wiki/Development) | Сборка, тесты, выпуск версии, модули |

Изменения по версиям — в [Changelog.md](Changelog.md).

## Связанные проекты

- [ToadExport](https://github.com/Alexpivo394/ToadExport) — ядро выгрузки RVT и NWC.
- [ToadParamCheck](https://github.com/Alexpivo394/ToadParamCheck) — ядро проверки LOI.
- [ToadClash](https://github.com/Alexpivo394/ToadClash) — ядро поиска коллизий.
- [ToadTools](https://github.com/Alexpivo394/ToadTools) — плагины для Revit: ToadExporter, ParamChecker, панель «Коллизии» и другие.
