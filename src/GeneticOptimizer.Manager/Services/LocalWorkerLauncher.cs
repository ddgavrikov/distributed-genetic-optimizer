using System.Collections.Concurrent;
using System.Diagnostics;

namespace GeneticOptimizer.Manager.Services;

public sealed class LocalWorkerLauncher(
    IWebHostEnvironment environment,
    IHttpClientFactory httpClientFactory,
    ILogger<LocalWorkerLauncher> logger) : IDisposable
{
    private readonly ConcurrentDictionary<int, Process> _processes = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public async Task EnsureStartedAsync(IEnumerable<string> addresses, CancellationToken cancellationToken)
    {
        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                !uri.IsLoopback || uri.Scheme != Uri.UriSchemeHttp)
            {
                continue;
            }

            if (await IsAvailableAsync(address, cancellationToken))
            {
                continue;
            }

            await _startLock.WaitAsync(cancellationToken);
            try
            {
                if (!await IsAvailableAsync(address, cancellationToken))
                {
                    StartWorker(uri.Port);
                }
            }
            finally
            {
                _startLock.Release();
            }

            await WaitUntilReadyAsync(address, cancellationToken);
        }
    }

    private void StartWorker(int port)
    {
        if (_processes.TryGetValue(port, out var existing) && !existing.HasExited)
        {
            return;
        }

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var workerDirectory = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath, "..", "GeneticOptimizer.Worker"));
        var executableName = OperatingSystem.IsWindows()
            ? "GeneticOptimizer.Worker.exe"
            : "GeneticOptimizer.Worker";
        var executablePath = Path.Combine(
            workerDirectory, "bin", configuration, "net10.0", executableName);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "Не найдена сборка локального вычислительного сервиса. Соберите решение целиком.",
                executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add($"--urls=http://localhost:{port}");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить локальный вычислительный сервис.");
        _processes[port] = process;
        logger.LogInformation("Local worker started on port {Port}, PID {ProcessId}", port, process.Id);
    }

    private async Task WaitUntilReadyAsync(string address, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsAvailableAsync(address, cancellationToken))
            {
                return;
            }

            await Task.Delay(150, cancellationToken);
        }

        logger.LogWarning("Local worker {Address} did not become ready in time", address);
    }

    private async Task<bool> IsAvailableAsync(string address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            var client = httpClientFactory.CreateClient("workers");
            using var response = await client.GetAsync($"{address}/health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var process in _processes.Values)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.Dispose();
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
        }

        _startLock.Dispose();
    }
}
