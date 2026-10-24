<p align="center">
    <h1 align="center">ToadFlow</h1>
    <p align="center">Сервис автоматизации работы с моделями Revit по расписанию</p>
</p>

ToadFlow по расписанию выполняет задачи над моделями Revit на Windows-машинах с Revit, на которых стоит агент ToadFlow. Расписания, история и логи — в браузере. Задачи подключаются модулями: **экспорт** моделей в **RVT** и **NWC** в сетевую папку и **проверка LOI** — заполнение параметров по профилям ParamChecker с отчётом Excel по каждой модели.

- Модель открывается в Revit **своей** версии (2020–2027) и **не обновляется**.
- До трёх Revit на машину, в том числе разных версий; зависший или упавший Revit агент убивает сам, модель выгружается повторно.
- Модели с Revit Server и из сетевых папок; пресеты выгрузки как у плагина ToadExporter, профили проверки — из плагина ParamChecker, с редактором правил в браузере.
- Пошаговый лог каждой модели; видно, кто создал и менял расписание, кто запустил выгрузку и кто убил Revit.
- Роли: наблюдатель, оператор, администратор.

```
Linux (docker compose)                      Windows-машины с Revit
┌─────────────────────────┐                ┌──────────────────────────────┐
│ web — интерфейс + nginx │                │ ToadFlow Agent               │
│ server — API, очередь   │◄── SignalR ────┤  ├─ Revit 2022 + аддин       │
│ postgres — данные       │   (исходящее   │  └─ Revit 2024 + аддин       │
└─────────────────────────┘    от агента)  └──────────────┬───────────────┘
                                                          ▼
                                             сетевая папка результатов
```

## Быстрый старт

**Сервер** (Linux с Docker):

```bash
git clone https://github.com/Alexpivo394/ToadFlow.git toadflow
cd toadflow/deploy
cp .env.example .env    # POSTGRES_PASSWORD, JWT_KEY, ADMIN_PASSWORD
docker compose up -d --build
```

Интерфейс — `http://<хост>:8080`, вход `ADMIN_LOGIN` / `ADMIN_PASSWORD` из `.env`.

**Агент** (Windows с Revit): в интерфейсе **Настройки → Агенты → Подключить агента**, затем установщик из [релизов](https://github.com/Alexpivo394/ToadFlow/releases):

```powershell
msiexec /i ToadFlowAgent-1.0.0.msi SERVER=http://toadflow:8080 CODE=TF-XXXX-XXXX
```

## Документация

Вся документация — в [вики](https://github.com/Alexpivo394/ToadFlow/wiki):

- [Установка сервера](https://github.com/Alexpivo394/ToadFlow/wiki/Server) — `.env`, обновление, резервные копии, HTTPS;
- [Установка агента](https://github.com/Alexpivo394/ToadFlow/wiki/Agent) — требования к машине, автовход, надстройка без подписи;
- [Первый запуск](https://github.com/Alexpivo394/ToadFlow/wiki/Getting-started) — пользователи, источники, пресеты, первое расписание;
- [Частые проблемы](https://github.com/Alexpivo394/ToadFlow/wiki/Troubleshooting);
- [Разработка](https://github.com/Alexpivo394/ToadFlow/wiki/Development) — сборка, тесты, выпуск версии, модули.

## Связанные проекты

- [ToadExport](https://github.com/Alexpivo394/ToadExport) — ядро выгрузки RVT/NWC в виде NuGet-пакетов.
- [ToadParamCheck](https://github.com/Alexpivo394/ToadParamCheck) — ядро проверки LOI в виде NuGet-пакетов.
- [ToadTools](https://github.com/Alexpivo394/ToadTools) — плагины для Revit, в том числе ToadExporter для ручной пакетной выгрузки и ParamChecker для ручной проверки LOI.
