# Dynamic Island for Windows

An iPhone-style island for Windows that displays the date, time, and currently playing media.

## Features

- **Compact and expanded views:** The compact island shows the date, time, a play/pause button when media is active, and an audio visualizer. Click to expand for the full date and time (including seconds) and media details; click empty space or outside the island to collapse it.
- **Media information:** Displays the current track's title, artist, and album art for apps that provide media information through Windows, including Spotify and supported browsers.
- **Playback controls:** Previous, play/pause, and next controls are available in the expanded view. Click the album art to bring the media player's window forward.
- **Audio visualization and progress:** Shows a live output-level visualizer and a border that advances with track playback.
- **Volume controls:** Adjust system volume with the slider and percentage readout, or scroll over the expanded island for 2% increments.
- **Optional lyrics:** Shows the current and next synced lines, or plain lyrics when synced lyrics are unavailable. Lyrics are looked up online through [LRCLIB](https://lrclib.net) and can be turned off in Settings.
- **Appearance and layout:** Change the island's size, horizontal position, distance from the top, collapsed and expanded opacity, border thickness and color, island and text colors, font, clock format, and frosted-glass effect. Changes apply immediately; settings can be reset to defaults.
- **Convenient controls:** Right-click the island to open Settings or exit the app. The installer can optionally add desktop and start-with-Windows shortcuts.

## Privacy

Dynamic Island does not collect analytics, create an account, or send your settings or personal files anywhere. Media information and audio levels are read locally through Windows APIs; audio is not recorded. Your preferences are saved locally in `%AppData%\DynamicIsland\settings.json`.

Lyrics are optional and enabled by default. When enabled, the current track's title and artist are sent to LRCLIB to find lyrics. Turn off **Show lyrics** in Settings to prevent these requests. No player credentials are used or sent.

## Clone and run

Requirements: Windows 10 version 2004 (build 19041) or later, or Windows 11; and the .NET 8 SDK.

```powershell
git clone https://github.com/kevinWicaksono/DynamicIsland.git
cd DynamicIsland
dotnet run -c Release
```

## Build and install

Build the project:

```powershell
dotnet build -c Release
```

To create a portable, self-contained x64 app that does not require the .NET runtime on the target PC:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The published app is in `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\`.

To build the Windows installer:

1. Publish the self-contained app using the command above.
2. Install [Inno Setup](https://jrsoftware.org/isdl.php).
3. Open `DynamicIsland.iss` in Inno Setup and compile it, or run:

   ```powershell
   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" DynamicIsland.iss
   ```

The installer is created in `installer\`. Alternatively, download and run the latest installer from the [GitHub Releases](https://github.com/kevinWicaksono/DynamicIsland/releases/latest) page.
