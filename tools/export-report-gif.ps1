[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $ReportPath,

    [Parameter(Position = 1)]
    [string] $OutputPath,

    [string] $BrowserPath,

    [ValidateRange(8, 30)]
    [int] $FramesPerSecond = 15,

    [ValidateRange(8, 60)]
    [int] $FrameCount = 20
)

$ErrorActionPreference = 'Stop'
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$reportFullPath = [IO.Path]::GetFullPath($ReportPath)
if (-not (Test-Path -LiteralPath $reportFullPath -PathType Leaf)) {
    throw "Report not found: $reportFullPath"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = [IO.Path]::ChangeExtension($reportFullPath, '.gif')
}
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

if ([string]::IsNullOrWhiteSpace($BrowserPath)) {
    $browserCandidates = @(
        'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files\Google\Chrome\Application\chrome.exe',
        'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe'
    )
    $BrowserPath = $browserCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($BrowserPath) -or
    -not (Test-Path -LiteralPath $BrowserPath -PathType Leaf)) {
    throw 'Edge or Chrome was not found. Pass -BrowserPath explicitly.'
}

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($null -eq $ffmpeg) {
    throw 'ffmpeg was not found on PATH. GIF export is optional; benchmark and reports do not require it.'
}
$ffmpegPath = $ffmpeg.Source

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryDirectory = [IO.Path]::GetFullPath(
    (Join-Path $temporaryRoot ('hlslperf-gif-' + [Guid]::NewGuid().ToString('N'))))
if (-not $temporaryDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create a temporary directory outside the system temp root.'
}
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
    $reportUri = ([Uri] $reportFullPath).AbsoluteUri
    $browserProfile = Join-Path $temporaryDirectory 'browser-profile'
    for ($frame = 0; $frame -lt $FrameCount; ++$frame) {
        $progress = $frame / ($FrameCount - 1)
        $progressText = $progress.ToString('0.0000', [Globalization.CultureInfo]::InvariantCulture)
        $framePath = Join-Path $temporaryDirectory ('frame-{0:D3}.png' -f $frame)
        $browserArguments = @(
            '--headless',
            '--disable-gpu',
            '--disable-background-networking',
            '--disable-extensions',
            '--disable-sync',
            '--hide-scrollbars',
            '--no-first-run',
            '--run-all-compositor-stages-before-draw',
            '--window-size=1400,1000',
            "--user-data-dir=$browserProfile",
            "--screenshot=$framePath",
            "${reportUri}?capture=$progressText"
        )
        $browserOutput = & $BrowserPath $browserArguments *>&1
        for ($attempt = 0;
             $attempt -lt 100 -and -not (Test-Path -LiteralPath $framePath -PathType Leaf);
             ++$attempt) {
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath $framePath -PathType Leaf)) {
            throw "Browser frame capture failed at frame $frame. $browserOutput"
        }
    }

    $inputPattern = Join-Path $temporaryDirectory 'frame-%03d.png'
    $filter = 'tpad=stop_mode=clone:stop_duration=1,scale=1000:-1:flags=lanczos,' +
        'split[s0][s1];[s0]palettegen=max_colors=96:stats_mode=diff[p];' +
        '[s1][p]paletteuse=dither=bayer:bayer_scale=3'
    $ffmpegArguments = @(
        '-hide_banner',
        '-loglevel', 'error',
        '-y',
        '-framerate', $FramesPerSecond.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-i', $inputPattern,
        '-vf', $filter,
        '-loop', '0',
        $outputFullPath
    )
    $ffmpegOutput = & $ffmpegPath $ffmpegArguments *>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputFullPath -PathType Leaf)) {
        throw "ffmpeg GIF encoding failed. $ffmpegOutput"
    }
    Get-Item -LiteralPath $outputFullPath
}
finally {
    $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
    if ($resolvedTemporaryDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryDirectory)) {
        for ($cleanupAttempt = 0; $cleanupAttempt -lt 30; ++$cleanupAttempt) {
            try {
                Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($cleanupAttempt -eq 29) {
                    Write-Warning "GIF was exported, but the browser still holds temporary files at $resolvedTemporaryDirectory."
                    break
                }
                Start-Sleep -Milliseconds 200
            }
        }
    }
}
