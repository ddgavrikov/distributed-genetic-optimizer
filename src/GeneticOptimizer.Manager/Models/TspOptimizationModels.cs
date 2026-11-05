using GeneticOptimizer.Contracts;

namespace GeneticOptimizer.Manager.Models;

public sealed record TspOptimizationRequest
{
    public List<string> ServiceAddresses { get; init; } = [];
    public List<CityDto> Cities { get; init; } = [];
    public int PopulationSize { get; init; } = 400;
    public int Generations { get; init; } = 1500;
    public double MutationProbability { get; init; } = 0.2;
    public int EliteCount { get; init; } = 4;
    public int Threads { get; init; }
    public int Seed { get; init; } = 42;
}

public sealed record HealthCheckRequest(List<string> ServiceAddresses);
public sealed record GenerateCitiesRequest(int Count, int Seed);
public sealed record HealthCheckResult(string Address, bool Available, string Message, long ElapsedMilliseconds);
public sealed record TspHistoryPoint(int Generation, double Length);
public sealed record WorkerJobSnapshot(
    string Address,
    string Status,
    int Generation,
    double? BestLength,
    long ElapsedMilliseconds,
    string? Error);

public sealed record TspJobSnapshot(
    Guid Id,
    string Status,
    string Message,
    IReadOnlyList<CityDto> Cities,
    int[]? BestRoute,
    double? BestLength,
    int Generation,
    int TargetGenerations,
    long ElapsedMilliseconds,
    IReadOnlyList<TspHistoryPoint> History,
    IReadOnlyList<WorkerJobSnapshot> Workers);
