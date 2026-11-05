using Microsoft.AspNetCore.Mvc;

namespace GeneticOptimizer.Manager.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
}
