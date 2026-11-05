# Распределённый генетический алгоритм

Учебное распределённое приложение для решения задачи коммивояжёра генетическим алгоритмом. Решение состоит из управляющего ASP.NET Core MVC-приложения и одного или нескольких одинаковых вычислительных ASP.NET Core Web API-сервисов.

## Требования

- .NET 10 SDK;
- Microsoft Visual Studio с компонентом «ASP.NET и веб-разработка» либо Visual Studio Code с расширением C# Dev Kit;
- Windows PowerShell для запуска готового сценария.

## Запуск в Visual Studio

1. Открыть `DistributedGeneticOptimizer.slnx`.
2. Выбрать профиль `Manager (auto workers)`.
3. Запустить решение без отладки или с отладкой.
4. Открыть `http://localhost:5200`, если браузер не открылся автоматически.

Manager запускается на порту `5200`. Локальные Worker-сервисы создаются автоматически для адресов из формы, например `localhost:5101` и `localhost:5102`.

## Быстрый запуск из PowerShell

В корне решения выполнить:

```powershell
powershell -ExecutionPolicy Bypass -File .\run-demo.ps1
```

Сценарий собирает решение, запускает Manager и открывает приложение в браузере. Для завершения нужно нажать `Ctrl+C` в терминале.

## Ручной запуск

Manager:

```powershell
dotnet run --project src\GeneticOptimizer.Manager --urls=http://localhost:5200
```

Локальные Worker-сервисы Manager запустит самостоятельно. Для запуска Worker на другом компьютере используется команда:

```powershell
dotnet run --project src\GeneticOptimizer.Worker --urls=http://0.0.0.0:5101
```

После этого в форме Manager нужно указать IP-адрес компьютера и порт, например `192.168.1.25:5101`.

## Использование

1. Указать адреса вычислительных сервисов, по одному адресу в строке.
2. Нажать «Запустить и проверить» либо сразу перейти к запуску задачи.
3. Задать города и параметры генетического алгоритма.
4. Нажать «Начать поиск».
5. Дождаться завершения или остановить вычисления кнопкой «Остановить».

Каждый Worker независимо выполняет генетический поиск с отдельным `seed`. Manager запускает обращения к сервисам параллельно и выбирает самый короткий найденный маршрут.

## Структура решения

- `src/Tsp.Core` — модель задачи коммивояжёра и генетический алгоритм;
- `src/GeneticOptimizer.Contracts` — DTO запросов, ответов и сообщений прогресса;
- `src/GeneticOptimizer.Worker` — вычислительный ASP.NET Core Web API-сервис;
- `src/GeneticOptimizer.Manager` — управляющее MVC-приложение;
- `tests/GeneticOptimizer.Worker.Tests` — сценарные тесты Worker API.

## API вычислительного сервиса

- `GET /health` — проверка готовности;
- `POST /api/compute/tsp` — выполнение задачи с единым ответом;
- `POST /api/compute/tsp/stream` — выполнение задачи с передачей прогресса в формате NDJSON.

## Тесты

Запуск всех тестов:

```powershell
dotnet test DistributedGeneticOptimizer.slnx --configuration Release
```

Запуск только сценарных тестов Worker:

```powershell
dotnet test tests\GeneticOptimizer.Worker.Tests\GeneticOptimizer.Worker.Tests.csproj --configuration Release
```

Сценарные тесты построены на `WebApplicationFactory<Program>` и `IClassFixture`. Они проверяют готовность сервиса, успешное решение, валидацию входных данных, воспроизводимость по `seed` и потоковую передачу прогресса.
