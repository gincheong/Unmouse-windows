# Unmouse for Windows

A Windows port of [Unmouse](https://github.com/gincheong/unmouse). Press a shortcut and every clickable element gets a label. Type a label to click that element.

Label input is matched by scan code, not by character. It works without switching the input language, even while a Korean IME is active.

## Features

- **Click mode** (`Ctrl+'`): shows labels on the focused window, its popups and menus, and the taskbar.
- **Scroll mode** (`Ctrl+Shift+J`): selects a scroll area and scrolls it with the keyboard.
- **Grid mode** (`Ctrl+Shift+'`): divides the screen into a grid and clicks any position, even in apps that expose no clickable elements.

Press the last label character with Shift to right-click, Alt to double-click, Ctrl to Ctrl-click, or Win to move the cursor without clicking.

## Portable

`Unmouse.exe` is a single self-contained file. It needs no installer and no .NET runtime. Settings are saved to `config.json` next to the exe, and only the keys that differ from the defaults are written.

"Launch at login" is the only feature that writes outside that folder. It adds a value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Build

Requires the .NET 10 SDK. The project also builds on macOS and Linux.

```sh
dotnet publish -c Release -o publish
```

The result is `publish/Unmouse.exe`.

## Diagnostics

```sh
Unmouse.exe --scan [--scroll]
```

Waits three seconds, then reports how many targets the focused window has and how long the scan took.

## Limitations

- Windows that run as administrator are not scanned and do not receive clicks unless Unmouse also runs as administrator.
- Apps that expose no UI Automation tree show no labels. Use grid mode there.

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
