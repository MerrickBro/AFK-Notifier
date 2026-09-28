# AFK Notifier

AFK Notifier is a Windows desktop utility that monitors audio from a selected application, repeatedly beeps through a configurable output device, and stops with a confirmation beep when it hears **AFK**.

## Install

1. Download or clone the repository.
2. Make sure the .NET 10 SDK is installed.
3. Double-click `Install AFK Notifier.vbs`.

The installer runs without a command window, publishes a self-contained Windows build to `%LOCALAPPDATA%\Programs\AFK Notifier`, adds **AFK Notifier** to the Windows Start menu, and launches it when installation finishes.

After installation, use the Windows Start menu to open AFK Notifier. Opening it again while it is already running brings the existing window to the front instead of starting another copy.

`AFK Notifier.bat` remains available as a development/source-tree launcher, but is not needed for normal use after installation.

## What it does

- Selects a running Windows application by process.
- Captures only that process and its child-process audio through WASAPI process loopback.
- Does not use the microphone.
- Runs speech recognition locally through the Windows speech recognizer.
- Repeats an alert beep through a selected playback device.
- Stops the alert and plays a two-tone confirmation when `AFK` is recognized.
- Saves the selected app name, output device, trigger phrase, beep interval, beep volumes, and beep pitches locally.
- Runs as a single instance and brings the existing window forward when reopened.

## Requirements

- Windows 10 version 2004 / build 19041 or newer.
- .NET 10 SDK for installing or building from source.
- A Windows speech-recognition language installed for the trigger phrase.

## Manual build and run

```powershell
dotnet restore
dotnet build --configuration Release
dotnet run --project src/AFKNotifier/AFKNotifier.csproj --configuration Release
```

## Current defaults

- Trigger phrase: `AFK`
- Idle beep: 750 Hz, 28% volume, 150 ms
- Alert interval: 1 second
- Confirmation: 1000 Hz followed by a proportionally higher second tone, 28% volume

## Audio path

```text
Selected application
        |
        v
WASAPI process loopback
        |
        v
Windows speech recognition
        |
        +---- AFK not detected ----> alert beep repeats
        |
        +---- AFK detected --------> stop alert -> confirmation tones
```

The alert audio is produced by AFK Notifier itself, so it is not fed back into the selected application's process-loopback stream.
