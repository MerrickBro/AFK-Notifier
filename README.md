# AFK Notifier

AFK Notifier is a Windows desktop utility that monitors audio from a selected application, repeatedly beeps through a configurable output device, and stops with a confirmation beep when it hears **AFK**.

## Quick start

1. Download or clone the repository.
2. Make sure the .NET 10 SDK is installed.
3. Double-click `AFK Notifier.bat` in the repository root.

The launcher checks for the .NET 10 SDK, builds the application if needed, and starts AFK Notifier. If .NET 10 is missing, it opens the .NET 10 download page.

## What it does

- Selects a running Windows application by process.
- Captures only that process and its child-process audio through WASAPI process loopback.
- Does not use the microphone.
- Runs speech recognition locally through the Windows speech recognizer.
- Repeats an alert beep through a selected playback device.
- Stops the alert and plays a two-tone confirmation when `AFK` is recognized.
- Saves the selected app name, output device, trigger phrase, and beep interval locally.

## Requirements

- Windows 10 version 2004 / build 19041 or newer.
- .NET 10 SDK.
- A Windows speech-recognition language installed for the trigger phrase.

## Manual build and run

```powershell
dotnet restore
dotnet build --configuration Release
dotnet run --project src/AFKNotifier/AFKNotifier.csproj --configuration Release
```

## Current defaults

- Trigger phrase: `AFK`
- Alert tone: 750 Hz for 150 ms
- Alert interval: 1 second
- Confirmation: 1000 Hz followed by 1350 Hz

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
