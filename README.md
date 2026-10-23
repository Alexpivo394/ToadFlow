<p align="center">
    <h1 align="center">ToadFlow</h1>
    <p align="center">Сервис выгрузки моделей Revit в RVT и NWC по расписанию</p>
</p>

ToadFlow по расписанию выгружает модели Revit в **RVT** и **NWC** и складывает результаты в сетевую папку. Расписания, история и логи — в браузере; выгрузку делают Windows-машины с Revit, на которых стоит агент ToadFlow.

- Модель открывается в Revit **своей** версии (2020–2027) и **не обновляется**.
- До трёх Revit на машину, в том числе разных версий; зависший или упавший Revit агент убивает сам, модель выгружается повторно.
- Модели с Revit Server и из сетевых папок; пресеты выгрузки как у плагина ToadExporter.
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
msiexec /i ToadFlowAgent-1.0.0.msi SERVER=http://toadflow:8080 CODE=TF-XXXX-XXXX TRUSTADDIN=1
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
- [ToadTools](https://github.com/Alexpivo394/ToadTools) — плагины для Revit, в том числе ToadExporter для ручной пакетной выгрузки.
