param(
    [Parameter(Mandatory = $true)][string]$SerializationScript,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$dynamicRepository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $dynamicRepository 'artifacts/dynamic-smoke' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $SerializationScript -PathType Leaf)) { throw 'Shared validation lock script is required.' }
& $SerializationScript -Action {
    Push-Location $dynamicRepository
    try {
        dotnet build tests/HlslPerf.DynamicSmoke/HlslPerf.DynamicSmoke.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Dynamic native harness build failed.' }
        dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Core correctness tests failed.' }
        dotnet run --no-build --project tests/HlslPerf.DynamicSmoke/HlslPerf.DynamicSmoke.csproj -c Release -- $dynamicRepository $OutputDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Dynamic native correctness matrix failed.' }
    }
    finally { Pop-Location }
}
