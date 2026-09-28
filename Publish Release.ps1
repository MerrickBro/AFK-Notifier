$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$repo = 'MerrickBro/AFK-Notifier'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $repoRoot 'src\AFKNotifier\AFKNotifier.csproj'
$updaterProject = Join-Path $repoRoot 'src\AFKNotifier.Updater\AFKNotifier.Updater.csproj'
$workerProject = Join-Path $repoRoot 'src\AFKNotifier.RecognizerWorker\AFKNotifier.RecognizerWorker.csproj'
$fontUrl = 'https://merrickbro.org/fonts/3720/3270-Regular.ttf'
$workRoot = Join-Path $env:TEMP ('AFKNotifierRelease_' + [guid]::NewGuid().ToString('N'))

function Require-Command($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "$name is required but was not found in PATH."
    }
}

function Get-NextVersion {
    $latestTag = [string](& gh release list --repo $repo --limit 1 --json tagName --jq '.[0].tagName' 2>$null)
    $latestTag = $latestTag.Trim()
    if ([string]::IsNullOrWhiteSpace($latestTag)) {
        return [version]'1.0.1'
    }

    $normalized = $latestTag.TrimStart('v', 'V')
    $current = [version]$normalized
    $patch = if ($current.Build -ge 0) { $current.Build + 1 } else { 1 }
    return [version]::new($current.Major, $current.Minor, $patch)
}

function Assert-Package($zipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @(
            'AFKNotifier.exe',
            'AFKNotifier.Updater.exe',
            'RecognizerWorker/AFKNotifier.RecognizerWorker.exe',
            'Fonts/3270-Regular.ttf'
        )) {
            if ($entries -notcontains $required) {
                throw "Package $zipPath is missing $required."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

try {
    Require-Command 'dotnet'
    Require-Command 'gh'

    & gh auth status | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub CLI is not authenticated. Run gh auth login first.'
    }

    $version = Get-NextVersion
    $tag = "v$version"
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

    $workerOutput = Join-Path $workRoot 'recognizer-worker'
    & dotnet publish $workerProject --configuration Release --runtime win-x64 --self-contained true --output $workerOutput --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw 'Recognizer worker publish failed.'
    }

    if (-not (Test-Path (Join-Path $workerOutput 'AFKNotifier.RecognizerWorker.exe'))) {
        throw 'Recognizer worker executable was not produced.'
    }

    $packages = @()
    foreach ($runtime in @('win-x64', 'win-arm64')) {
        $runtimeRoot = Join-Path $workRoot $runtime
        $appOutput = Join-Path $runtimeRoot 'app'
        $updaterOutput = Join-Path $runtimeRoot 'updater'
        $zipPath = Join-Path $workRoot "AFK-Notifier-$runtime.zip"

        & dotnet publish $appProject --configuration Release --runtime $runtime --self-contained true --output $appOutput --nologo --verbosity minimal "/p:Version=$version"
        if ($LASTEXITCODE -ne 0) {
            throw "AFK Notifier publish failed for $runtime."
        }

        & dotnet publish $updaterProject --configuration Release --runtime $runtime --self-contained true --output $updaterOutput --nologo --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            throw "Updater publish failed for $runtime."
        }

        Copy-Item (Join-Path $updaterOutput 'AFKNotifier.Updater.exe') $appOutput -Force

        $workerInstallDirectory = Join-Path $appOutput 'RecognizerWorker'
        New-Item -ItemType Directory -Path $workerInstallDirectory -Force | Out-Null
        Copy-Item (Join-Path $workerOutput '*') $workerInstallDirectory -Recurse -Force

        $fontDirectory = Join-Path $appOutput 'Fonts'
        $fontPath = Join-Path $fontDirectory '3270-Regular.ttf'
        New-Item -ItemType Directory -Path $fontDirectory -Force | Out-Null
        Invoke-WebRequest -Uri $fontUrl -OutFile $fontPath -UseBasicParsing

        if (-not (Test-Path $fontPath) -or (Get-Item $fontPath).Length -lt 10000) {
            throw "The IBM 3270 font download failed for $runtime."
        }

        foreach ($requiredPath in @(
            (Join-Path $appOutput 'AFKNotifier.exe'),
            (Join-Path $appOutput 'AFKNotifier.Updater.exe'),
            (Join-Path $workerInstallDirectory 'AFKNotifier.RecognizerWorker.exe'),
            $fontPath
        )) {
            if (-not (Test-Path $requiredPath)) {
                throw "Required release file missing: $requiredPath"
            }
        }

        Compress-Archive -Path (Join-Path $appOutput '*') -DestinationPath $zipPath -Force
        Assert-Package $zipPath
        $packages += $zipPath
    }

    & gh release create $tag @packages --repo $repo --target main --title "AFK Notifier $tag" --generate-notes
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub release creation failed for $tag."
    }

    Write-Host "Published $tag successfully."
    Write-Host 'Installed copies will detect it the next time they start or when the version label is clicked.'
}
finally {
    if (Test-Path $workRoot) {
        Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
