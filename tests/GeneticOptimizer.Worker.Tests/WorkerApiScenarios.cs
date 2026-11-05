using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeneticOptimizer.Contracts;

namespace GeneticOptimizer.Worker.Tests;

public sealed class WorkerApiScenarios(WorkerApiFixture factory) : IClassFixture<WorkerApiFixture>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact(DisplayName = "Сервис сообщает о готовности")]
    public async Task Health_ReturnsOkAndServiceIdentity()
    {
        var response = await _client.GetAsync("/health");
        var health = await response.Content.ReadFromJsonAsync<WorkerHealthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal("GeneticOptimizer.Worker", health.Service);
    }

    [Fact(DisplayName = "Корневой маршрут описывает TSP API")]
    public async Task Root_ReturnsTspApiDescription()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("TSP", body);
        Assert.Contains("/api/compute/tsp", body);
    }

    [Fact(DisplayName = "Корректное задание возвращает замкнутый маршрут")]
    public async Task Optimize_ValidSquare_ReturnsOptimalRoute()
    {
        var request = ValidRequest();

        var response = await _client.PostAsJsonAsync("/api/compute/tsp", request);
        var result = await response.Content.ReadFromJsonAsync<TspWorkerResult>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal([0, 1, 2, 3], result.Route.Order().ToArray());
        Assert.Equal(0, result.Route[0]);
        Assert.Equal(4.0, result.Length, 8);
        Assert.Equal(request.Generations, result.Generation);
    }

    [Fact(DisplayName = "Некорректные города возвращают 400 с описанием")]
    public async Task Optimize_DuplicateCities_ReturnsValidationProblem()
    {
        var request = ValidRequest() with
        {
            Cities = [new(0, 0), new(0, 0), new(1, 1)]
        };

        var response = await _client.PostAsJsonAsync("/api/compute/tsp", request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Cities", body);
        Assert.Contains("совпадать", body);
    }

    [Fact(DisplayName = "Одинаковый seed даёт воспроизводимый маршрут")]
    public async Task Optimize_SameSeed_ReturnsSameResult()
    {
        var request = ValidRequest();
        var firstResponse = await _client.PostAsJsonAsync("/api/compute/tsp", request);
        var secondResponse = await _client.PostAsJsonAsync("/api/compute/tsp", request);
        var first = await firstResponse.Content.ReadFromJsonAsync<TspWorkerResult>();
        var second = await secondResponse.Content.ReadFromJsonAsync<TspWorkerResult>();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Length, second.Length);
        Assert.Equal(first.Route, second.Route);
    }

    [Fact(DisplayName = "Потоковый маршрут передаёт прогресс и завершение")]
    public async Task Stream_ReturnsProgressAndCompletionMessages()
    {
        using var response = await _client.PostAsJsonAsync("/api/compute/tsp/stream", ValidRequest());
        var lines = (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var messages = lines
            .Select(line => JsonSerializer.Deserialize<TspProgressMessage>(line, JsonSerializerOptions.Web))
            .OfType<TspProgressMessage>()
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(messages, message => message.Type == "progress" && message.Generation == 0);
        Assert.Contains(messages, message =>
            message.Type == "completed" &&
            message.Generation == ValidRequest().Generations);
        Assert.All(messages.Where(message => message.Route.Length > 0), message =>
            Assert.Equal([0, 1, 2, 3], message.Route.Order().ToArray()));
    }

    private static TspWorkerRequest ValidRequest() => new()
    {
        Cities = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
        PopulationSize = 50,
        Generations = 30,
        MutationProbability = 0.2,
        EliteCount = 2,
        Threads = 2,
        Seed = 2026,
        IslandIndex = 0
    };
}
