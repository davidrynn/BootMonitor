# BootMonitor

A tiny Windows widget that tells you when your PC has **actually finished booting**, so you know it's safe to start heavy work like Unity, video editing or a game.

After you log in, Windows keeps loading startup apps, updaters and services for a while. BootMonitor watches CPU and disk activity. Once both stay calm for 10 seconds, it shows **"PC Fully Loaded & Ready!"**.

## Features

- Small always-on-top overlay in the bottom-right corner (drag it anywhere).
- Live CPU and disk usage, with a countdown to "ready".
- Lists your startup programs (from the registry Run keys and the Startup folders) and marks each one green when it's running.
- Shows the ready message for 5 minutes, then exits. It uses no CPU while it waits.
- Closes itself after 15 minutes if your PC never settles.
- Close it at any time with the **✕** button or **Esc**.
- **Remove from startup / Add to startup** button.
- Only one copy runs at a time.

## How "ready" is decided

| Setting | Default |
|---|---|
| CPU below | 20% |
| Disk busy below | 15% |
| Must stay calm for | 10 seconds |
| Gives up after | 15 minutes |

These are constants at the top of `MainWindow.xaml.cs`.

## Install

1. Download `BootMonitor.exe` from the [Releases](../../releases) page.
2. Put it somewhere permanent, e.g. `%LOCALAPPDATA%\BootMonitor\`.
3. Run it once.

> **Note:** On first run, BootMonitor adds itself to your user startup programs (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, entry `BootMonitorApp`) so it appears after every login. Click **Remove from startup** in the widget, or turn it off in Task Manager → Startup apps, to stop that. It remembers your choice.
>
> The exe isn't code-signed, so Windows SmartScreen may warn you the first time. Click **More info → Run anyway**.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
# Run from source
dotnet run

# Framework-dependent build (small; needs the .NET 10 Desktop Runtime)
dotnet publish BootMonitor.csproj -c Release -o "$env:LOCALAPPDATA\BootMonitor"

# Self-contained single exe (~62 MB, no runtime needed) - this is what goes on the Releases page
dotnet publish BootMonitor.csproj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

## Uninstall

1. Click **Remove from startup** in the widget (or delete the `BootMonitorApp` value from the Run key above).
2. Delete the folder you installed it in.
3. Optionally delete `HKCU\Software\BootMonitor`.

## License

[MIT](LICENSE)
