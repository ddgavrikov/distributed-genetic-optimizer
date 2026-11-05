namespace GeneticOptimizer.Contracts;

public sealed record CityDto(double X, double Y);

public sealed record TspWorkerRequest
{
    public List<CityDto> Cities { get; init; } = [];
    public int PopulationSize { get; init; } = 400;
    public int Generations { get; init; } = 1500;
    public double MutationProbability { get; init; } = 0.2;
    public int EliteCount { get; init; } = 4;
    public int Threads { get; init; }
    public int Seed { get; init; } = 42;
    public int IslandIndex { get; init; }
}

public sealed record TspProgressMessage(
    string Type,
    int IslandIndex,
    int Generation,
    int[] Route,
    double Length,
    bool Cancelled = false,
    long ElapsedMilliseconds = 0,
    string? Error = null);

public sealed record TspWorkerResult(
    int IslandIndex,
    int Seed,
    int Generation,
    int[] Route,
    double Length,
    bool Cancelled,
    long ElapsedMilliseconds);

public sealed record WorkerHealthResponse(
    string Status,
    string Service,
    string Machine,
    DateTimeOffset UtcTime);

public static class TspRequestValidator
{
    public static IReadOnlyDictionary<string, string[]> Validate(TspWorkerRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.Cities.Count is < 3 or > 500)
        {
            errors[nameof(request.Cities)] = ["Нужно от 3 до 500 городов."];
        }
        else
        {
            var hasInvalidCoordinates = request.Cities.Any(city =>
                !double.IsFinite(city.X)
                || !double.IsFinite(city.Y)
                || Math.Abs(city.X) > 1e6
                || Math.Abs(city.Y) > 1e6);

            if (hasInvalidCoordinates)
            {
                errors[nameof(request.Cities)] =
                    ["Координаты должны быть конечными числами по модулю не более 1000000."];
            }
            else if (request.Cities.Distinct().Count() != request.Cities.Count)
            {
                errors[nameof(request.Cities)] = ["Координаты городов не должны совпадать."];
            }
        }

        if (request.PopulationSize is < 4 or > 10_000)
        {
            errors[nameof(request.PopulationSize)] = ["Размер популяции должен быть от 4 до 10000."];
        }

        if (request.Generations is < 1 or > 1_000_000)
        {
            errors[nameof(request.Generations)] = ["Число поколений должно быть от 1 до 1000000."];
        }

        if (!double.IsFinite(request.MutationProbability) || request.MutationProbability is < 0 or > 1)
        {
            errors[nameof(request.MutationProbability)] = ["Вероятность мутации должна быть от 0 до 1."];
        }

        if (request.EliteCount < 1 || request.EliteCount >= request.PopulationSize)
        {
            errors[nameof(request.EliteCount)] = ["Элита должна быть не меньше 1 и меньше популяции."];
        }

        if (request.Threads is < 0 or > 256)
        {
            errors[nameof(request.Threads)] = ["Число потоков должно быть от 0 до 256; 0 — автоматически."];
        }

        return errors;
    }
}
