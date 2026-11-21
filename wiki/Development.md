# Разработка

## Что нужно

- .NET SDK 10, Node.js 24.
- Для агента и аддина — Windows. Revit нужен только для живой проверки: тесты обходятся без него.

## Состав репозитория

| Путь | Что это |
|---|---|
| `src/ToadFlow.Server` | Ядро сервера: агенты, очередь заданий, расписания, история, пользователи, события |
| `src/ToadFlow.Server.Export` | Модуль «Выгрузка RVT/NWC»: пресеты, настройки расписания, задания агентам |
| `src/ToadFlow.Server.Loi` | Модуль «Проверка LOI»: профили ParamChecker, каталог категорий и параметров Revit, задания агентам |
| `src/ToadFlow.Server.Clash` | Модуль «Коллизии»: выгрузки моделей, части, черновики проверок и шаблоны, очередь прогонов движка и их результаты |
| `src/ToadFlow.Server.Families` | Модуль «Семейства»: карточки и версии семейств библиотеки, применение обходов, поиск, задания чтения ферме |
| `src/ToadFlow.Clash.Worker` | Работник движка коллизий — отдельный процесс (контейнер `clash-engine`), говорит с сервером по HTTP |
| `src/ToadFlow.Server.Host` | Приложение сервера: ядро и подключённые модули |
| `src/ToadFlow.Contracts` | Протокол сервер ↔ агент ↔ аддин |
| `src/ToadFlow.Agent.Core` | Агент: связь с сервером, экземпляры Revit, обнаружение зависаний, обзор источников |
| `src/ToadFlow.Agent` | Приложение агента: значок в области уведомлений, окно подключения |
| `src/ToadFlow.Revit.Addin` | Аддин Revit 2020–2027 (решение `ToadFlow.Revit.slnx`, конфигурации `Debug.R20`…`Release.R27`) |
| `web/` | Веб-интерфейс: React, Mantine, TanStack Query |
| `deploy/` | Docker-образы и docker compose |
| `install/` | Установщик агента (WixSharp) |
| `build/` | Сборка Windows-части и выпуск релиза (ModularPipelines) |
| `tests/` | Тесты сервера, агента и аддина |
| `wiki/` | Эта вики; публикуется в GitHub Wiki при изменении в `main` |

Ядро выгрузки — пакеты [ToadExport](https://github.com/Alexpivo394/ToadExport), ядро проверки LOI — пакеты [ToadParamCheck](https://github.com/Alexpivo394/ToadParamCheck), ядро коллизий — пакеты [ToadClash](https://github.com/Alexpivo394/ToadClash), ядро семейств — пакеты [ToadFamilyManager](https://github.com/Alexpivo394/ToadFamilyManager) (`Alexpivo394.Revit.Families.*`) на nuget.org. Чтобы собрать ToadFlow с пакетами, которых ещё нет на nuget.org, соберите их локально (`DOTNET_ENVIRONMENT=Development Publish__Version=X.Y.Z dotnet run -c Release -- pack` в `build/` репозитория пакета) и добавьте папку `output` как источник: `dotnet build -p:RestoreAdditionalProjectSources=<папка>`.

## Запуск локально

```powershell
dotnet run --project src/ToadFlow.Server.Host   # http://localhost:5000, вход admin / admin
cd web; npm install; npm run dev                # http://localhost:5173
```

В режиме Development сервер сам поднимает встроенный PostgreSQL (порт 54329, данные в `%LocalAppData%\ToadFlow\pgdata`) — ставить PostgreSQL не нужно.

Агент против локального сервера:

```powershell
dotnet run --project src/ToadFlow.Agent -- --register --server http://localhost:5000 --code TF-XXXX-XXXX
dotnet run --project src/ToadFlow.Agent
```

Аддин для отладки: `dotnet build src/ToadFlow.Revit.Addin -c Debug.R22` кладёт его в `%AppData%\Autodesk\Revit\Addins\2022`. Аддин работает только в Revit, запущенном агентом, — запускайте выгрузку через расписание.

Движок коллизий против локального сервера (ключ — любой не короче 32 символов, одинаковый у обоих):

```powershell
$env:Clash__EngineKey = 'dev-engine-key-0123456789abcdefghij'; dotnet run --project src/ToadFlow.Server.Host   # сервер с ключом движка
$env:Clash__EngineKey = 'dev-engine-key-0123456789abcdefghij'; dotnet run --project src/ToadFlow.Clash.Worker   # работник: http://localhost:5000
```

## Тесты

```powershell
dotnet test --project tests/ToadFlow.Server.Tests      # на встроенном PostgreSQL
dotnet test --project tests/ToadFlow.Agent.Tests       # агент против настоящего сервера и имитации Revit
dotnet test --project tests/ToadFlow.Revit.Addin.Tests # логика аддина без Revit
dotnet test --project tests/ToadFlow.Clash.Worker.Tests # работник движка коллизий против настоящего сервера
cd web; npm test; npm run lint; npx tsc -b
```

## Миграции базы

```powershell
dotnet tool restore
dotnet ef migrations add <Имя> --project src/ToadFlow.Server --startup-project src/ToadFlow.Server.Host --output-dir Data/Migrations
```

Сервер применяет миграции сам при старте.

## Сборка агента, аддина и установщика

Windows-часть собирает пайплайн в `build/`; версию задаёт GitVersion по истории git.

| Команда (из `build/`) | Что делает |
|---|---|
| `dotnet run -c Release` | Собирает аддин для всех версий Revit |
| `dotnet run -c Release -- test` | Тесты агента и аддина |
| `dotnet run -c Release -- pack` | Аддин, агент (self-contained) и `output/ToadFlowAgent-<версия>.msi` |
| `dotnet run -c Release -- pack publish` | То же и релиз на GitHub с установщиком |

## Выпуск версии

1. Запись `# X.Y.Z` с изменениями в начале `Changelog.md`.
2. Тег `X.Y.Z` или **Actions → Publish release → Run workflow** с номером версии.

Workflow собирает установщик агента и создаёт релиз на GitHub с текстом из `Changelog.md`. Сервер обновляется из исходников: [Установка сервера → Обновление](Server#обновление).

## Новый модуль

Модуль — новый тип задания (как выгрузка или проверка LOI): реализует `IServerModule` (`src/ToadFlow.Server/Modules`) — службы, таблицы, API, проверку настроек расписания и сборку задания для агента; если модулю нужен итог задания у себя (как каталог LOI), — `OnJobCompletedAsync`. Подключается ссылкой на проект в `ToadFlow.Server.Host` (и строкой `COPY` в `deploy/server.Dockerfile`) и одной строкой в `Program.cs`: `builder.AddServerModule<МойМодуль>()`.

Вид задания (`ModuleKinds`) агент передаёт аддину в `JobMessage.ModuleKind`, аддин выбирает по нему исполнителя в `JobExecutor`. Пустой вид — выгрузка: так шлют задания агенты до 1.1.0.

### Как модуль описывает себя серверу

| Член `IServerModule` | Зачем |
|---|---|
| `Name` | Имя в настройке `MODULES`: `export`, `loi`, `clash`. Строчными латинскими |
| `Title` | Название для людей: «Выгрузка», «Проверка LOI», «Коллизии» |
| `Kind` | Основной вид задания — его выбирают расписания модуля |
| `Kinds` | Все виды заданий модуля (у LOI — `loi` и `loi-catalog`, у выгрузки — `export` и пустой). Соответствие «модуль → виды» задаётся только здесь |
| `WebPath`, `HistoryPath` | Адреса пространства и истории модуля в вебе — туда ведут плитка и строка «модели с ошибкой» на «Обзоре» |

Сервер делит модули на **известные** (все, что подключены в `Program.cs`) и **включённые** (`ModuleSet`, по настройке `Modules`). `ConfigureModel` выполняется у всех известных — схема базы одна, миграции покрывают таблицы всех модулей. `AddServices`, маршруты, обработка итогов заданий и пробы `/api/health` — только у включённых. Поэтому всё, что модуль регистрирует в `AddServices` (фоновые службы, источники «Обзора», рабочие процессы, тома), у выключенного модуля само исчезает.

Модуль не должен зависеть от другого модуля: общее (справочник именования, источники, агенты) живёт в ядре.

**«Обзор».** В `AddServices` модуль может зарегистрировать:

- `IOverviewSource` (`src/ToadFlow.Server/Overview`) — плитка модуля (`OverviewTile`: главное число, подпись, изменение, до трёх строк, ссылка) и свои строки «Требует внимания» (`AttentionItem` с уровнем, ссылкой и ролью, с которой строка видна). Числа берите из расчётов, которые модуль уже делает. Исключение в источнике не ломает «Обзор»: плитка модуля покажет «нет данных»;
- `IWorkerProbe` — свой рабочий процесс вне сервера (как движок коллизий): когда он последний раз выходил на связь. Молчит дольше 5 минут — строка в «Требует внимания» и пометка в **Администрирование → Модули**;
- `IDataVolume` (например, `PathVolume`) — том с данными модуля: место на нём видно на странице «Сервер», а если места мало — строка в «Требует внимания».

Рабочий процесс модуля узнаёт, включён ли его модуль, по полю `modules` ответа `GET /api/health` (так делает `ModuleGate` движка коллизий) и при выключенном модуле завершается с кодом 0.

### Как модуль описывает себя вебу

Пространство модуля — один файл-манифест в `web/src/spaces` (по образцу `export.tsx`, `loi.tsx`, `clash.tsx`) и строка в `registry.ts`:

| Поле `Space` | Зачем |
|---|---|
| `id`, `module` | Адрес пространства (`/{id}`) и имя модуля сервера; пространство показывается, только если сервер назвал модуль включённым (`GET /api/modules`) |
| `label`, `short`, `icon` | Название, подпись в полосе и значок |
| `pages` | Страницы меню: адрес, подпись, раздел (`work` — сверху, `setup` — под разделителем), роль, с которой видна (`Operator`, `Admin`), видна ли проектировщику, условие скрытия |
| `routes` | `<Route>`-элементы пространства с путями относительно `/{id}`; у выключенного модуля маршрутов нет, его адреса ведут на «Обзор» |

Если у модуля есть задания, добавьте его в `web/src/spaces/modules.ts` (вид задания, адреса расписаний и истории): расписания и история модуля — это общие `SchedulesPage` и `HistoryPage` с `module` и `base`. Тексты — в `web/src/i18n/ru.ts`. Списки — на общей таблице `web/src/components/DataTable.tsx`.
