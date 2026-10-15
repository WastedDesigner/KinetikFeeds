# Kinetik Feeds: RSS and Atom headlines widget for the Windows desktop

[![Latest release](https://img.shields.io/github/v/release/WastedDesigner/KinetikFeeds?label=download)](../../releases/latest)
[![License: GPL v3](https://img.shields.io/github/license/WastedDesigner/KinetikFeeds)](LICENSE)
![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)

**Kinetik Feeds** is a free, open-source **RSS and Atom news widget for Windows 11 and Windows 10**. It puts the latest headlines from the sites you follow on your desktop. Feeds are fetched straight from their public links: no account, no API keys.

It's a small portable app with no installer. It's a companion to [Kinetik](https://github.com/WastedDesigner/Kinetik), the taskbar system monitor, and [Kinetik Calendar](https://github.com/WastedDesigner/KinetikCalendar), and shares their look.

> **About this project:** I made this for my own desktop and decided to share it in case anyone else finds it useful. If you run into a bug or have an idea for a feature, please [open an issue](../../issues).

## Features

- **Any RSS or Atom feed**, each switchable on or off (starts with BBC News, The Verge and Hacker News)
- **One list of headlines** across your feeds, newest first; click one to open it in your browser
- **Updating** on a schedule you choose, with a limit on how many headlines and how old they can be
- **Themes** in Kinetik's style, custom accent, fonts, width, scale, opacity, rounding, shadow and border
- **Placement:** drag anywhere, snap to edges, lock position, run at startup
- **Settings backup** next to the exe, offered again on a fresh Windows

## Download and use

1. Download `KinetikFeeds.zip` from [Releases](../../releases/latest) and extract it anywhere.
2. Run `KinetikFeeds.exe`. Right-click the widget or the tray icon for Settings, where you add and remove feeds.

Settings are kept in `HKCU\Software\KinetikFeeds`. The only network requests are to the feed links you add.

## Requirements

Windows 10 or 11 (64-bit) with .NET Framework 4.8, which ships with both.

## Building from source

No SDK needed: `build.bat` compiles `KinetikFeeds.cs` with the .NET Framework compiler that ships with Windows. `build.bat package` also makes `KinetikFeeds.zip` for a release.

## License

[GPL-3.0-or-later](LICENSE). Copyright © 2026 Akila Sella Hennedige.
