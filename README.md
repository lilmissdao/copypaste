# CopyPaste

A tiny [Flycut](https://github.com/TermiT/Flycut)-style clipboard manager for Windows.
It sits in the system tray next to the clock and volume icons and remembers the
last **50** things you copied.

## Using it

| Action | How |
| --- | --- |
| See your history | Left-click the tray icon (popup opens bottom-right, above the clock) |
| Copy an old item again | Click it in the list |
| Paste an old item straight into the app you're in | Press **Ctrl+Shift+V**, pick with ↑/↓, press **Enter** |
| Search | Just start typing in the popup |
| Remove one item | Highlight it and press **Delete** |
| Close the popup | **Esc**, or click anywhere else |
| Pause, clear history, start with Windows, exit | Right-click the tray icon |

Re-copying something that's already in the list moves it back to the top
instead of duplicating it. History is saved to `%APPDATA%\CopyPaste\history.txt`,
so it survives restarts. Only text is recorded.

> Tip: Windows may tuck new tray icons into the `^` overflow. Drag the icon
> onto the taskbar next to the clock to keep it visible.

## Getting the .exe

**Option A – download a build:** open the repo's **Actions** tab, pick the latest
`build` run and download the `CopyPaste` artifact.

**Option B – build it yourself (no installs needed):** clone or download this repo
on your Windows machine and double-click `build.bat`. It uses the C# compiler that
ships with Windows (.NET Framework 4) and produces `CopyPaste.exe` next to it.

Then double-click `CopyPaste.exe`. To have it start automatically, right-click the
tray icon and tick **Start with Windows**.

Windows SmartScreen may warn about an unsigned app the first time; choose
**More info → Run anyway**.
