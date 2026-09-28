@echo off
setlocal
cd /d "%~dp0"

echo AFK Notifier

echo Checking for .NET 10 SDK...
where dotnet >nul 2>nul
if errorlevel 1 goto missingDotnet

dotnet --list-sdks | findstr /b "10." >nul
if errorlevel 1 goto missingDotnet

echo Starting AFK Notifier...
dotnet run --project "src\AFKNotifier\AFKNotifier.csproj" --configuration Release
if errorlevel 1 goto failed

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
echo AFK Notifier could not start. Review the error above.
echo.
pause
exit /b 1
