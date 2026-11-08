# Dynamic Island for Windows

An iPhone-style "Dynamic Island" for your Windows desktop: a small pill at the top of the screen that shows the date and time, and whatever media is currently playing. Click it to expand it into a full view with playback and volume controls.

Built with C# and WPF (.NET 8). It works offline except for optional lyric lookups, which are enabled by default and can be turned off in Settings.

## Download

Get the latest installer from the [Releases page](https://github.com/kevinWicaksono/dynamicIsland/releases/tag/v1.1.1).
No build tools needed. Run `DynamicIslandSetup.exe`.

## Features

- **Compact pill:** date and time, 12-hour or 24-hour. When media is active it also shows a circled play/pause button and a live audio visualizer. Its default opacity is 90%.
- **Expanded view (click the island):** full date, time with seconds, album art, song title, artist, audio visualizer, current and next lyric lines, previous / play-pause / next buttons, and a volume slider with a percentage readout. Its default opacity is 70%. Click empty space on it, or anywhere outside it, to collapse it. Click the album art to bring the player's window to the front.
- **Frosted glass:** an optional acrylic-style blurred backdrop, enabled by default and switchable in Settings.
- **Playback progress border:** while media plays, the island's border fills clockwise to show how far through the track you are.
- **Media lyrics (optional, enabled by default):** show the current and next synced lines, or plain lyrics when timing data is unavailable. Works with Spotify and browser media sessions (including YouTube) when Windows provides track metadata. Lyrics are looked up through [LRCLIB](https://lrclib.net); turn the feature off in Settings to avoid lyric lookup requests.
- **Volume control:** snaps in 2% steps. Scroll the mouse wheel over the expanded island for ±2%.
- **Settings (right-click → Settings…):** island size, horizontal position and distance from top, opacity when collapsed and when expanded, border thickness and color, island color, text color, font, clock format, optional lyrics, and frosted glass. Changes apply live.
- Right-click the island → **Exit Dynamic Island** to close it.

## Requirements

### To run

- Windows 10 version 2004 (build 19041) or newer, or Windows 11
- 64-bit (x64)
- Nothing else if you use the self-contained build or the installer. The .NET runtime is included.
- For media info, a player that uses the Windows media controls (Spotify, most browsers, Windows Media Player, VLC and others).
- An internet connection is needed for lyric lookups, which are enabled by default and can be turned off in Settings.

### To build from source

- Windows 10 / 11, x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Internet access for the first build so `dotnet` can download packages. At runtime, the app only makes network requests for lyric lookups when lyrics are enabled.
- *(Optional, for the installer)* [Inno Setup](https://jrsoftware.org/isdl.php)

Check your SDK with:

```powershell
dotnet --version
```

## Build and run

Clone the repository and open a terminal in the project folder (the one containing `DynamicIsland.csproj`):

```powershell
git clone https://github.com/kevinWicaksono/dynamicIsland
cd <repo-folder>
```

| Goal | Command |
|---|---|
| Run from source | `dotnet run -c Release` |
| Build only | `dotnet build -c Release` |
| Clean build output | `Remove-Item -Recurse -Force bin, obj` |

### Publish a portable app

Self-contained single file. It runs on any x64 Windows machine without .NET installed:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Smaller output. It needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed on the target PC:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

The published app is in:

```
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

### Build the installer

1. Publish the app as a self-contained single file (command above).
2. Install Inno Setup.
3. Compile the script, either by opening `DynamicIsland.iss` in Inno Setup and pressing **F9**, or from the terminal:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" DynamicIsland.iss
```

The installer is created at `installer\DynamicIslandSetup.exe`. It installs for the current user (no admin prompt) and offers optional desktop and start-with-Windows shortcuts. Before releasing, change `AppPublisher` and `AppVersion` in `DynamicIsland.iss`.

## Using it

| Action | What happens |
|---|---|
| Click the collapsed island | Expands it |
| Click empty space on the expanded island, or anywhere outside it | Collapses it |
| Click the album art (expanded) | Brings the player's window to the front |
| Click the circled ⏸/▶ in the compact pill | Plays or pauses without expanding |
| Scroll over the expanded island | Volume ±2% |
| Right-click the island | Settings… / Exit |

## Privacy and offline use

- No telemetry or update checks. The app only makes lyric lookup requests (enabled by default; disable in Settings). If enabled, the current media title and artist are sent to LRCLIB to find lyrics; no player credentials are used or sent.
- It never reads or scans your files. The font list in Settings is a fixed list, not a scan of your computer.
- **Media info:** the current track's title, artist and artwork come live from the Windows media session service. They are kept in memory only and never saved. Track title and artist are sent only to LRCLIB when lyrics are enabled.
- **Visualizer and volume:** the app reads the current output level (one number) and the master volume through Windows' audio API. No audio is recorded.
- **Click outside to collapse:** while the island is expanded, it checks whether a mouse button is pressed and whether the pointer is over the island. Nothing is recorded.
- **Album art click:** to find the player's window, the app briefly looks at the names of running processes and the titles of open windows. It uses them only to pick one window, keeps nothing, and does this only when you click the album art.
- **Saved settings:** `%AppData%\DynamicIsland\settings.json` holds your appearance preferences and whether lyrics are enabled. Delete that folder to reset everything. The installer's uninstaller removes it too.

## Project structure

| File | Purpose |
|---|---|
| `MainWindow.xaml` / `.xaml.cs` | The island: layout, animations, media, progress border, visualizer |
| `SettingsWindow.xaml` / `.xaml.cs` | The settings window |
| `AppSettings.cs` | Settings model and saving/loading |
| `LyricsService.cs` | Optional LRCLIB lyric lookup and synced-line parsing |
| `AudioVolume.cs` | System volume, mute and output-level access through Windows Core Audio |
| `App.xaml` / `App.xaml.cs` | Application entry point |
| `DynamicIsland.csproj` | Project file (.NET 8, WPF) |
| `DynamicIsland.iss` | Inno Setup installer script |

## Troubleshooting

- **Build errors about missing names or types** (for example `SettingsWindow` or `AudioVolume`): make sure all the files above are in the same folder as the `.csproj`, then clean and rebuild:
  ```powershell
  Remove-Item -Recurse -Force bin, obj
  dotnet build -c Release
  ```
- **Error `MC3000: Data at the root level is invalid` in a `.xaml` file:** the file has stray text before its first `<` (for example a Markdown code fence from copy-pasting) or was saved in the wrong encoding. Make sure it starts with `<Window` or `<Application` and is saved as UTF-8.
- **No media shown:** the player must support the Windows media controls. If the volume overlay in Windows doesn't show the track, the island can't either.
- **Island is hidden behind a full-screen app:** it is a normal always-on-top window, and some full-screen apps and games cover it.
- **Windows SmartScreen warns about an unknown publisher:** the app and installer are not code-signed. Choose *More info → Run anyway*.

## License

Add a license of your choice (for example MIT) as a `LICENSE` file.
