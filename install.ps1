$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

Add-Type -AssemblyName PresentationFramework

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot 'src\AFKNotifier\AFKNotifier.csproj'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\AFK Notifier'
$publishDir = Join-Path $env:TEMP ('AFKNotifier_' + [guid]::NewGuid().ToString('N'))
$startMenuDir = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
$shortcutPath = Join-Path $startMenuDir 'AFK Notifier.lnk'

function ShowMessage($text, $title, $icon) {
    [System.Windows.MessageBox]::Show($text, $title, 'OK', $icon) | Out-Null
}

try {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Start-Process 'https://dotnet.microsoft.com/download/dotnet/10.0'
        ShowMessage '.NET 10 SDK is required to install AFK Notifier. The download page has been opened.' 'AFK Notifier' 'Information'
        exit 1
    }

    $sdks = & dotnet --list-sdks 2>$null
    if (-not ($sdks -match '^10\.')) {
        Start-Process 'https://dotnet.microsoft.com/download/dotnet/10.0'
        ShowMessage '.NET 10 SDK is required to install AFK Notifier. The download page has been opened.' 'AFK Notifier' 'Information'
        exit 1
    }

    $runtime = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'Arm64') {
        'win-arm64'
    } else {
        'win-x64'
    }

    $publishOutput = & dotnet publish $projectPath --configuration Release --runtime $runtime --self-contained true --output $publishDir --nologo --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        $details = ($publishOutput | Select-Object -Last 12) -join [Environment]::NewLine
        throw "Build failed.$([Environment]::NewLine)$details"
    }

    Get-Process AFKNotifier -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if (Test-Path $installDir) {
        Remove-Item $installDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Copy-Item (Join-Path $publishDir '*') $installDir -Recurse -Force

    $exePath = Join-Path $installDir 'AFKNotifier.exe'
    if (-not (Test-Path $exePath)) {
        throw 'AFKNotifier.exe was not produced by the publish step.'
    }

    New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Description = 'AFK Notifier'
    $shortcut.IconLocation = "$exePath,0"
    $shortcut.Save()

    Start-Process $exePath
    ShowMessage 'AFK Notifier is installed. You can now launch it from the Windows Start menu.' 'AFK Notifier' 'Information'
    exit 0
}
catch {
    ShowMessage ("Installation failed:`n`n" + $_.Exception.Message) 'AFK Notifier' 'Error'
    exit 1
}
finally {
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
