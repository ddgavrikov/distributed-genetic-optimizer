using GeneticOptimizer.Manager.Models;
using GeneticOptimizer.Manager.Services;
using Microsoft.AspNetCore.Mvc;

namespace GeneticOptimizer.Manager.Controllers;

[ApiController]
[Route("api/optimization")]
public sealed class OptimizationController(TspJobCoordinator coordinator) : ControllerBase
{
    [HttpPost("start")]
    public IActionResult Start(TspOptimizationRequest request)
    {
        try
        {
            return Accepted(new { jobId = coordinator.Start(request) });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpGet("jobs/{id:guid}")]
    public IActionResult Get(Guid id)
    {
        var job = coordinator.Get(id);
        return job is null ? NotFound(new { error = "Задача не найдена." }) : Ok(job);
    }

    [HttpPost("jobs/{id:guid}/cancel")]
    public IActionResult Cancel(Guid id)
    {
        return coordinator.Cancel(id)
            ? Accepted()
            : NotFound(new { error = "Задача не найдена." });
    }

    [HttpPost("generate")]
    public IActionResult Generate(GenerateCitiesRequest request)
    {
        try
        {
            return Ok(TspJobCoordinator.GenerateCities(request.Count, request.Seed));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPost("health")]
    public async Task<IActionResult> Health(HealthCheckRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await coordinator.CheckAsync(request.ServiceAddresses, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }
}
