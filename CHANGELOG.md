# Changelog

## 0.2.0 - 2026-11-26

### Fixed

- Click mode adds labels to elements that appear after the first scan of a newly focused window. Labels that are already shown keep their characters or only get more characters appended.
- Scroll mode jumps to the top or bottom with `g` and `Shift+g` through the scroll area's UI Automation scroll pattern. Areas without the pattern still receive a wheel event.

## 0.1.0 - 2026-11-24

### Added

- Click mode, with labels on the focused window, its popups and menus, and the taskbar.
- Scroll mode.
- Grid mode, with finer division on the screen that has the focused window.
- Right-click, double-click, Ctrl-click, and cursor move with a modifier key on the last label character.
- Label input matched by scan code, so it works while a Korean IME is active.
- Label design settings: characters, font, weight, size, padding, colors, opacity, corner radius, uppercase, and pointer arrow.
- Grid design settings.
- English and Korean localization.
- Launch at login.
- Elevation status in the General tab, with a button to restart as administrator.
- Settings saved to `config.json` next to `Unmouse.exe`.
