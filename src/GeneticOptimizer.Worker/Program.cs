using System.Text.Json;
using System.Threading.Channels;
using GeneticOptimizer.Contracts;
using GeneticOptimizer.Worker.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TspComputation>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "GeneticOptimizer.Worker",
    problem = "TSP, вариант 1",
    endpoints = new[] { "GET /health", "POST /api/compute/tsp", "POST /api/compute/tsp/stream" }
}));

app.MapGet("/health", () => Results.Ok(new WorkerHealthResponse(
    "ok",
    "GeneticOptimizer.Worker",
    Environment.MachineName,
    DateTimeOffset.UtcNow)));

app.MapPost("/api/compute/tsp", (
    TspWorkerRequest request,
    TspComputation computation,
    CancellationToken cancellationToken) =>
{
    var errors = TspRequestValidator.Validate(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    return Results.Ok(computation.Optimize(request, null, cancellationToken));
});

app.MapPost("/api/compute/tsp/stream", async (
    TspWorkerRequest request,
    TspComputation computation,
    HttpContext context) =>
{
    var errors = TspRequestValidator.Validate(request);
    if (errors.Count > 0)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { errors }, context.RequestAborted);
        return;
    }

    context.Response.ContentType = "application/x-ndjson; charset=utf-8";
    var channel = Channel.CreateUnbounded<TspProgressMessage>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    var computationTask = Task.Run(() =>
    {
        try
        {
            var result = computation.Optimize(request, progress =>
            {
                channel.Writer.TryWrite(new TspProgressMessage(
                    "progress",
                    request.IslandIndex,
                    progress.Generation,
                    progress.Route,
                    progress.Length));
            }, context.RequestAborted);

            channel.Writer.TryWrite(new TspProgressMessage(
                "completed",
                request.IslandIndex,
                result.Generation,
                result.Route,
                result.Length,
                result.Cancelled,
                result.ElapsedMilliseconds));
        }
        catch (Exception exception)
        {
            channel.Writer.TryWrite(new TspProgressMessage(
                "error",
                request.IslandIndex,
                0,
                [],
                double.PositiveInfinity,
                Error: exception.Message));
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    });

    await foreach (var message in channel.Reader.ReadAllAsync(context.RequestAborted))
    {
        var json = JsonSerializer.Serialize(message, JsonSerializerOptions.Web);
        await context.Response.WriteAsync($"{json}\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    await computationTask;
});

app.Run();

namespace GeneticOptimizer.Worker
{
    public partial class Program;
}
