namespace Tsp.Core;

public readonly record struct City(double X, double Y);

public sealed class TspProblem
{
    private readonly double[,] _distances;

    public IReadOnlyList<City> Cities { get; }
    public int Count => Cities.Count;
    public double Distance(int from, int to) => _distances[from, to];

    public TspProblem(IEnumerable<City> cities)
    {
        var points = cities.ToArray();
        if (points.Length is < 3 or > 500)
        {
            throw new ArgumentException("Нужно от 3 до 500 городов.");
        }

        var hasInvalidCoordinates = points.Any(point =>
            !double.IsFinite(point.X) ||
            !double.IsFinite(point.Y) ||
            Math.Abs(point.X) > 1e6 ||
            Math.Abs(point.Y) > 1e6);

        if (hasInvalidCoordinates)
        {
            throw new ArgumentException("Координаты должны быть конечными числами по модулю не более 1000000.");
        }

        if (points.Distinct().Count() != points.Length)
        {
            throw new ArgumentException("Координаты городов не должны совпадать.");
        }

        Cities = Array.AsReadOnly(points);
        _distances = new double[Count, Count];
        for (var i = 0; i < Count; i++)
        {
            for (var j = i + 1; j < Count; j++)
            {
                var dx = points[i].X - points[j].X;
                var dy = points[i].Y - points[j].Y;
                _distances[i, j] = _distances[j, i] = Math.Sqrt(dx * dx + dy * dy);
                if (_distances[i, j] == 0)
                {
                    throw new ArgumentException("Города расположены слишком близко.");
                }
            }
        }
    }

    public double Length(IReadOnlyList<int> route)
    {
        var total = 0.0;
        for (var i = 0; i < route.Count; i++)
        {
            total += _distances[route[i], route[(i + 1) % route.Count]];
        }

        return total;
    }

    public static TspProblem Generate(int count, int seed)
    {
        if (count is < 3 or > 500)
        {
            throw new ArgumentException("Нужно от 3 до 500 городов.");
        }

        var random = new Random(seed);
        var cities = new HashSet<City>();
        while (cities.Count < count)
        {
            cities.Add(new City(random.Next(1000), random.Next(1000)));
        }

        return new TspProblem(cities);
    }
}

public sealed record GeneticOptions(
    int Population = 400,
    int Generations = 1500,
    double Mutation = 0.2,
    int Elite = 4,
    int Threads = 0,
    int Seed = 42)
{
    public void Validate()
    {
        if (Population is < 4 or > 10_000 || Generations is < 1 or > 1_000_000 ||
            !double.IsFinite(Mutation) || Mutation is < 0 or > 1 ||
            Elite < 1 || Elite >= Population || Threads < 0)
        {
            throw new ArgumentException("Некорректные параметры генетического алгоритма.");
        }
    }
}

public sealed record SearchProgress(int Generation, int[] Route, double Length);
public sealed record SearchResult(int Generation, int[] Route, double Length, bool Cancelled);

public sealed class GeneticSolver
{
    private static Random RandomFor(int seed, int generation, int index) =>
        new(unchecked(seed * 73_856_093 ^ generation * 19_349_663 ^ index * 83_492_791));

    public SearchResult Solve(
        TspProblem problem,
        GeneticOptions options,
        Action<SearchProgress>? report = null,
        CancellationToken cancellation = default)
    {
        options.Validate();
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Threads == 0 ? Environment.ProcessorCount : options.Threads
        };
        var population = new int[options.Population][];
        var scores = new double[options.Population];
        var best = Enumerable.Range(0, problem.Count).ToArray();
        var bestLength = problem.Length(best);
        var completed = 0;

        try
        {
            parallel.CancellationToken = cancellation;
            Parallel.For(0, population.Length, parallel, i =>
            {
                var random = RandomFor(options.Seed, 0, i);
                var route = Enumerable.Range(0, problem.Count).ToArray();
                for (var j = route.Length - 1; j > 1; j--)
                {
                    var k = random.Next(1, j + 1);
                    (route[j], route[k]) = (route[k], route[j]);
                }
                population[i] = route;
                scores[i] = problem.Length(route);
            });

            for (var generation = 0; generation <= options.Generations; generation++)
            {
                cancellation.ThrowIfCancellationRequested();
                Array.Sort(scores, population);
                if (scores[0] < bestLength)
                {
                    bestLength = scores[0];
                    best = (int[])population[0].Clone();
                }
                completed = generation;
                report?.Invoke(new SearchProgress(generation, (int[])best.Clone(), bestLength));
                if (generation == options.Generations)
                {
                    break;
                }

                var next = new int[population.Length][];
                var nextScores = new double[population.Length];
                Parallel.For(0, next.Length, parallel, i =>
                {
                    if (i < options.Elite)
                    {
                        next[i] = population[i];
                        nextScores[i] = scores[i];
                        return;
                    }

                    var random = RandomFor(options.Seed, generation + 1, i);
                    int[] Tournament()
                    {
                        var winner = random.Next(population.Length);
                        for (var tournament = 0; tournament < 2; tournament++)
                        {
                            var candidate = random.Next(population.Length);
                            if (scores[candidate] < scores[winner])
                            {
                                winner = candidate;
                            }
                        }
                        return population[winner];
                    }

                    var child = Crossover(Tournament(), Tournament(), random);
                    if (random.NextDouble() < options.Mutation)
                    {
                        var first = random.Next(1, child.Length);
                        int second;
                        do
                        {
                            second = random.Next(1, child.Length);
                        }
                        while (first == second);

                        (child[first], child[second]) = (child[second], child[first]);
                    }
                    next[i] = child;
                    nextScores[i] = problem.Length(child);
                });
                population = next;
                scores = nextScores;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        return new SearchResult(completed, best, bestLength, cancellation.IsCancellationRequested);
    }

    private static int[] Crossover(int[] first, int[] second, Random random)
    {
        var length = first.Length;
        var left = random.Next(1, length);
        var right = random.Next(left, length);
        var child = Enumerable.Repeat(-1, length).ToArray();
        var used = new bool[length];
        child[0] = 0;
        used[0] = true;

        for (var i = left; i <= right; i++)
        {
            child[i] = first[i];
            used[first[i]] = true;
        }

        var position = right == length - 1 ? 1 : right + 1;
        for (var offset = 1; offset < length; offset++)
        {
            var city = second[1 + (right - 1 + offset) % (length - 1)];
            if (used[city])
            {
                continue;
            }

            child[position] = city;
            used[city] = true;
            position = position == length - 1 ? 1 : position + 1;
        }

        return child;
    }
}
