param(
    [Parameter(Mandatory=$true)][string]$Unity,
    [Parameter(Mandatory=$true)][string]$Project,
    [string]$Repository = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Project) { throw 'Use a fresh absolute project directory; existing projects are never overwritten.' }
if (-not [IO.Path]::IsPathRooted($Project)) { throw 'Project must be absolute.' }
New-Item -ItemType Directory -Force "$Project/Assets/Editor", "$Project/Packages", "$Project/ProjectSettings", "$Project/Build", "$Project/frames" | Out-Null
Copy-Item "$Repository/unity/LiveGpuDrivenCrowd" "$Project/Assets/LiveGpuDrivenCrowd" -Recurse
Copy-Item "$Repository/tools/portfolio-unity/PortfolioRecorder.cs" "$Project/Assets"
Copy-Item "$Repository/tools/portfolio-unity/Editor/PortfolioBuild.cs" "$Project/Assets/Editor"
Set-Content "$Project/Packages/manifest.json" '{"dependencies":{"com.unity.ugui":"2.0.0","com.unity.modules.imgui":"1.0.0","com.unity.modules.imageconversion":"1.0.0","com.unity.modules.screencapture":"1.0.0"}}'
Set-Content "$Project/ProjectSettings/ProjectVersion.txt" 'm_EditorVersion: 6000.3.13f1'
$env:HLSL_PORTFOLIO_PLAYER = "$Project/Build/Crowd.exe"
$env:HLSL_PORTFOLIO_FRAMES = "$Project/frames"
$build = Start-Process $Unity -ArgumentList "-batchmode -quit -projectPath `"$Project`" -executeMethod PortfolioBuild.Build -logFile `"$Project/build.log`"" -WindowStyle Hidden -PassThru
$build.WaitForExit()
if ($build.ExitCode -ne 0 -or -not (Test-Path $env:HLSL_PORTFOLIO_PLAYER)) { throw 'Unity build failed; inspect build.log.' }
$player = Start-Process $env:HLSL_PORTFOLIO_PLAYER -ArgumentList "-screen-width 960 -screen-height 540 -screen-fullscreen 0 -force-d3d11 -logFile `"$Project/player.log`"" -WindowStyle Hidden -PassThru
$player.WaitForExit()
if ($player.ExitCode -ne 0 -or -not (Test-Path "$Project/frames/complete.txt")) { throw 'Capture incomplete; inspect player.log.' }
Write-Output "Captured 288 real Unity frames in $Project/frames. Encode using docs/media/README.md."
