@echo off
setlocal
cd /d "%~dp0"

set DOTNET_NOLOGO=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

where dotnet >nul 2>nul
if errorlevel 1 goto missingDotnet

dotnet --list-sdks | findstr /b "10." >nul
if errorlevel 1 goto missingDotnet

echo Building AFK Notifier...
dotnet build "src\AFKNotifier\AFKNotifier.csproj" --configuration Release --nologo --verbosity quiet
if errorlevel 1 goto failed

echo Starting AFK Notifier...
start "" "src\AFKNotifier\bin\Release\net10.0-windows10.0.19041.0\AFKNotifier.exe"
exit /b 0

:missingDotnet
echo.
echo AFK Notifier requires the .NET 10 SDK.
echo Opening the .NET 10 download page...
start "" "https://dotnet.microsoft.com/download/dotnet/10.0"
echo.
pause
exit /b 1

:failed
echo.
echo AFK Notifier could not build. Review the error above.
echo.
pause
exit /b 1
