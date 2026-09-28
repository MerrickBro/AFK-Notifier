# AFK Notifier

AFK Notifier is a Windows desktop utility that monitors audio from a selected application, repeatedly beeps through a configurable output device, and stops with a confirmation beep when it hears **AFK**.

## Install

1. Download or clone the repository.
2. Make sure the .NET 10 SDK is installed.
3. Double-click `Install AFK Notifier.vbs`.

The installer runs without a console window, publishes AFK Notifier into `%LOCALAPPDATA%\Programs\AFK Notifier`, creates a Windows Start Menu shortcut, installs the website-style font when available, and launches the app.

## Automatic updates

Installed copies check the public GitHub Releases feed on startup. When a newer release exists, AFK Notifier downloads the matching `win-x64` or `win-arm64` package in the background. The update is staged while the app is running and automatically replaces the installed files after AFK Notifier closes, then relaunches the updated version.

Every push to `main` is configured to build self-contained Windows packages and create a new GitHub Release with an automatically incremented `1.0.<run>` version. Anonymous update checks require this repository to be public.

## Visual design

The desktop UI follows the visual language of merrickbro.org: black monitor surfaces, light-blue `#b0e0ff` interface text and borders, green `#b0ffe0` accents, layered dark monitor-frame colors, and the same `3270` font when the installer can retrieve it from the website.

## What it does

- Selects a running Windows application by process.
- Captures only that process and its child-process audio through WASAPI process loopback.
- Does not use the microphone.
- Runs speech recognition locally through the Windows speech recognizer.
- Repeats a configurable idle beep through a selected playback device.
- Lets you configure idle-beep pitch, idle-beep volume, confirmation pitch, confirmation volume, and beep interval.
- Stops the alert and plays a two-tone confirmation when the trigger phrase is recognized.
- Saves preferences locally.
- Runs as a single instance; opening it again restores and brings the existing window to the front.

## Requirements

- Windows 10 version 2004 / build 19041 or newer.
- .NET 10 SDK for source installation/building.
- A Windows speech-recognition language installed for the trigger phrase.

## Manual development run

```powershell
dotnet restore
dotnet run --project src/AFKNotifier/AFKNotifier.csproj --configuration Release
```

## Current defaults

- Trigger phrase: `AFK`
- Idle tone: 750 Hz, 28% volume, 150 ms
- Alert interval: 1 second
- Confirmation: 1000 Hz followed by a second tone at 1.35x pitch, 28% volume

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
        +---- AFK not detected ----> idle beep repeats
        |
        +---- AFK detected --------> stop alert -> confirmation tones
```

The alert audio is produced by AFK Notifier itself, so it is not fed back into the selected application's process-loopback stream.
