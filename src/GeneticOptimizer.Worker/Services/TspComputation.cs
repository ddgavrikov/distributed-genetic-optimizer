using System.Diagnostics;
using GeneticOptimizer.Contracts;
using Tsp.Core;

namespace GeneticOptimizer.Worker.Services;

public sealed class TspComputation
{
    public TspWorkerResult Optimize(
        TspWorkerRequest request,
        Action<SearchProgress>? report,
        CancellationToken cancellationToken)
    {
        var cities = request.Cities.Select(city => new City(city.X, city.Y));
        var problem = new TspProblem(cities);
        var options = new GeneticOptions(
            request.PopulationSize,
            request.Generations,
            request.MutationProbability,
            request.EliteCount,
            request.Threads,
            request.Seed);
        var stopwatch = Stopwatch.StartNew();
        var result = new GeneticSolver().Solve(problem, options, report, cancellationToken);
        stopwatch.Stop();

        return new TspWorkerResult(
            request.IslandIndex,
            request.Seed,
            result.Generation,
            result.Route,
            result.Length,
            result.Cancelled,
            stopwatch.ElapsedMilliseconds);
    }
}
