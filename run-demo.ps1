$ErrorActionPreference = 'Stop'
$solutionRoot = $PSScriptRoot
$managerProject = Join-Path $solutionRoot 'src\GeneticOptimizer.Manager\GeneticOptimizer.Manager.csproj'

Write-Host 'Building solution...'
dotnet build (Join-Path $solutionRoot 'DistributedGeneticOptimizer.slnx') --configuration Release
Write-Host 'Manager: http://localhost:5200'
Write-Host 'Workers for localhost addresses will start automatically.'
Write-Host 'Press Ctrl+C to stop the demo.'
Start-Process 'http://localhost:5200'
dotnet run `
    --project $managerProject `
    --configuration Release `
    --no-build `
    --no-launch-profile `
    --urls=http://localhost:5200
