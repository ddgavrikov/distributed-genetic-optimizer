using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using GeneticOptimizer.Contracts;
using GeneticOptimizer.Manager.Models;
using Tsp.Core;

namespace GeneticOptimizer.Manager.Services;

public sealed class TspJobCoordinator(
    IHttpClientFactory httpClientFactory,
    LocalWorkerLauncher localWorkerLauncher,
    ILogger<TspJobCoordinator> logger)
{
    private const int MaximumWorkers = 20;
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();

    public Guid Start(TspOptimizationRequest request)
    {
        var addresses = NormalizeAddresses(request.ServiceAddresses);
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors));
        }

        var state = new JobState(request, addresses);
        if (!_jobs.TryAdd(state.Id, state))
        {
            throw new InvalidOperationException("Не удалось создать задачу.");
        }

        _ = Task.Run(() => RunAsync(state));
        return state.Id;
    }

    public TspJobSnapshot? Get(Guid id) => _jobs.TryGetValue(id, out var state) ? state.Snapshot() : null;

    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var state))
        {
            return false;
        }

        state.Cancellation.Cancel();
        return true;
    }

    public async Task<IReadOnlyList<HealthCheckResult>> CheckAsync(
        IEnumerable<string> rawAddresses,
        CancellationToken cancellationToken)
    {
        var addresses = NormalizeAddresses(rawAddresses);
        await localWorkerLauncher.EnsureStartedAsync(addresses, cancellationToken);
        return await Task.WhenAll(addresses.Select(address => CheckWorkerAsync(address, cancellationToken)));
    }

    public static IReadOnlyList<string> Validate(TspOptimizationRequest request)
    {
        var workerRequest = ToWorkerRequest(request, 0);
        return TspRequestValidator.Validate(workerRequest).SelectMany(pair => pair.Value).ToArray();
    }

    public static IReadOnlyList<CityDto> GenerateCities(int count, int seed)
    {
        var problem = TspProblem.Generate(count, seed);
        return problem.Cities.Select(city => new CityDto(city.X, city.Y)).ToArray();
    }

    private async Task RunAsync(JobState state)
    {
        try
        {
            await localWorkerLauncher.EnsureStartedAsync(
                state.Workers.Select(worker => worker.Address),
                state.Cancellation.Token);
            state.SetRunning();
            var tasks = state.Workers.Select((worker, index) => RunWorkerAsync(state, worker, index));
            await Task.WhenAll(tasks);
            state.Finish();
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            state.Finish();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to prepare workers");
            state.PreparationFailed($"Не удалось запустить локальные сервисы: {exception.Message}");
        }
    }

    private async Task RunWorkerAsync(JobState state, WorkerState worker, int islandIndex)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = httpClientFactory.CreateClient("workers");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{worker.Address}/api/compute/tsp/stream")
            {
                Content = JsonContent.Create(ToWorkerRequest(state.Request, islandIndex))
            };
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                state.Cancellation.Token);

            if (!response.IsSuccessStatusCode)
            {
                var details = await response.Content.ReadAsStringAsync(state.Cancellation.Token);
                state.WorkerFailed(
                    worker,
                    $"HTTP {(int)response.StatusCode}: {Trim(details)}",
                    stopwatch.ElapsedMilliseconds);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(state.Cancellation.Token);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(state.Cancellation.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = JsonSerializer.Deserialize<TspProgressMessage>(line, JsonSerializerOptions.Web);
                if (message is null)
                {
                    continue;
                }

                if (message.Type == "error")
                {
                    state.WorkerFailed(
                        worker,
                        message.Error ?? "Ошибка вычислительного сервиса.",
                        stopwatch.ElapsedMilliseconds);
                    return;
                }

                state.Report(worker, message);
            }
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            state.WorkerCancelled(worker, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            state.WorkerFailed(worker, "Превышено время ожидания ответа.", stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Worker {Address} is unavailable", worker.Address);
            state.WorkerFailed(worker, $"Сетевая ошибка: {exception.Message}", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Worker {Address} failed", worker.Address);
            state.WorkerFailed(worker, $"Ошибка ответа: {exception.Message}", stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task<HealthCheckResult> CheckWorkerAsync(string address, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = httpClientFactory.CreateClient("workers");
            using var response = await client.GetAsync($"{address}/health", cancellationToken);
            return response.IsSuccessStatusCode
                ? new HealthCheckResult(address, true, "Доступен", stopwatch.ElapsedMilliseconds)
                : new HealthCheckResult(
                    address,
                    false,
                    $"HTTP {(int)response.StatusCode}",
                    stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            var message = exception is TaskCanceledException ? "Тайм-аут" : "Нет соединения";
            return new HealthCheckResult(address, false, message, stopwatch.ElapsedMilliseconds);
        }
    }

    private static TspWorkerRequest ToWorkerRequest(TspOptimizationRequest request, int islandIndex) => new()
    {
        Cities = request.Cities,
        PopulationSize = request.PopulationSize,
        Generations = request.Generations,
        MutationProbability = request.MutationProbability,
        EliteCount = request.EliteCount,
        Threads = request.Threads,
        Seed = unchecked(request.Seed + islandIndex * 104_729),
        IslandIndex = islandIndex
    };

    private static string[] NormalizeAddresses(IEnumerable<string>? rawAddresses)
    {
        var items = (rawAddresses ?? [])
            .SelectMany(value => value.Split(
                ['\r', '\n', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Contains("://", StringComparison.Ordinal) ? value : $"http://{value}")
            .Select(value => value.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (items.Length == 0)
        {
            throw new ArgumentException("Укажите хотя бы один адрес вычислительного сервиса.");
        }

        if (items.Length > MaximumWorkers)
        {
            throw new ArgumentException($"Можно использовать не более {MaximumWorkers} сервисов.");
        }

        foreach (var item in items)
        {
            if (!Uri.TryCreate(item, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new ArgumentException($"Некорректный адрес сервиса: {item}");
            }
        }

        return items;
    }

    private static string Trim(string value) => value.Length <= 300 ? value : value[..300] + "…";

    private sealed class JobState
    {
        private readonly object _gate = new();
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly SortedDictionary<int, double> _history = [];
        private string _status = "queued";
        private string _message = "Задача поставлена в очередь.";
        private int[]? _bestRoute;
        private double _bestLength = double.PositiveInfinity;

        public Guid Id { get; } = Guid.NewGuid();
        public TspOptimizationRequest Request { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public WorkerState[] Workers { get; }

        public JobState(TspOptimizationRequest request, string[] addresses)
        {
            Request = request;
            Workers = addresses.Select(address => new WorkerState(address)).ToArray();
        }

        public void SetRunning()
        {
            lock (_gate)
            {
                _status = "running";
                _message = "Вычисления выполняются параллельно.";
            }
        }

        public void Report(WorkerState worker, TspProgressMessage message)
        {
            lock (_gate)
            {
                worker.Status = message.Type == "completed" ? "completed" : "running";
                worker.Generation = message.Generation;
                worker.BestLength = message.Length;
                worker.ElapsedMilliseconds = message.ElapsedMilliseconds;
                if (message.Length < _bestLength)
                {
                    _bestLength = message.Length;
                    _bestRoute = (int[])message.Route.Clone();
                }
                if (!_history.TryGetValue(message.Generation, out var existing) || message.Length < existing)
                {
                    _history[message.Generation] = message.Length;
                }
            }
        }

        public void WorkerFailed(WorkerState worker, string error, long elapsed)
        {
            lock (_gate)
            {
                worker.Status = "failed";
                worker.Error = error;
                worker.ElapsedMilliseconds = elapsed;
            }
        }

        public void WorkerCancelled(WorkerState worker, long elapsed)
        {
            lock (_gate)
            {
                worker.Status = "cancelled";
                worker.ElapsedMilliseconds = elapsed;
            }
        }

        public void Finish()
        {
            lock (_gate)
            {
                _stopwatch.Stop();
                if (Cancellation.IsCancellationRequested)
                {
                    _status = "cancelled";
                    _message = _bestRoute is null
                        ? "Поиск остановлен до получения первого поколения."
                        : "Поиск остановлен. Показано лучшее найденное решение.";
                }
                else if (_bestRoute is null)
                {
                    _status = "failed";
                    _message = "Ни один вычислительный сервис не вернул результат.";
                }
                else
                {
                    _status = "completed";
                    var failures = Workers.Count(worker => worker.Status == "failed");
                    _message = failures == 0
                        ? "Оптимизация завершена."
                        : $"Оптимизация завершена, недоступных сервисов: {failures}.";
                }
            }
        }

        public void PreparationFailed(string error)
        {
            lock (_gate)
            {
                _stopwatch.Stop();
                _status = "failed";
                _message = error;
            }
        }

        public TspJobSnapshot Snapshot()
        {
            lock (_gate)
            {
                var bestSoFar = double.PositiveInfinity;
                var history = _history
                    .Select(pair =>
                    {
                        bestSoFar = Math.Min(bestSoFar, pair.Value);
                        return new TspHistoryPoint(pair.Key, bestSoFar);
                    })
                    .TakeLast(100)
                    .ToArray();
                return new TspJobSnapshot(
                    Id,
                    _status,
                    _message,
                    Request.Cities,
                    _bestRoute is null ? null : (int[])_bestRoute.Clone(),
                    double.IsPositiveInfinity(_bestLength) ? null : _bestLength,
                    Workers.Max(worker => worker.Generation),
                    Request.Generations,
                    _stopwatch.ElapsedMilliseconds,
                    history,
                    Workers.Select(worker => worker.Snapshot()).ToArray());
            }
        }
    }

    private sealed class WorkerState(string address)
    {
        public string Address { get; } = address;
        public string Status { get; set; } = "queued";
        public int Generation { get; set; }
        public double? BestLength { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public string? Error { get; set; }

        public WorkerJobSnapshot Snapshot() =>
            new(Address, Status, Generation, BestLength, ElapsedMilliseconds, Error);
    }
}
