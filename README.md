# WinDock

<img src="icon.png" width="96" alt="WinDock icon">

A Mac-style dock for Windows 10 and 11. It replaces the taskbar with a floating, rounded dock that magnifies under your mouse, and stays out of the way until you touch the bottom edge of the screen.

## Features

- **Hover magnification** with name labels, and a bounce when an app launches
- **Pinned and running apps** with a dot under anything that is open
- **Click** to launch, switch to, cycle through the windows of, or minimize an app
- **Drag icons** to rearrange them
- **Launchpad**: a full-screen, searchable grid of every installed app
- **Windows Search** and **Recycle Bin** icons
- **System controls** beside the dock: network, sound (scroll to change volume), battery, clock, and a power menu with Lock, Sleep, Sign Out, Restart and Shut Down
- **Desktop icons button**: a small floating button that hides and shows your desktop icons with one click
- **Use either or both**: the dock and the desktop icons button can each be switched on or off
- **A dock on every monitor**
- **Stays out of games**: it won't pop up over fullscreen apps, or where a window covers it
- **Three modes**: auto-hide, always visible (windows stop above it), or behind windows on the desktop
- **A settings window** for size, magnification, opacity, roundness, light/dark, and everything above, applied live

## Install

1. Download `WinDock-Setup.exe` from the [latest release](https://github.com/AverageBENNAL/WinDock/releases/latest).
2. Double-click it and choose **Yes**.

That's it. WinDock installs for your Windows account only, so it doesn't need administrator rights, and nothing else has to be installed first. A welcome window explains what it is about to do before it changes anything.

Windows SmartScreen may warn that the app is unrecognised, because it isn't code-signed. Choose **More info > Run anyway** if you trust it.

### Portable version

Prefer not to install? Download `WinDock-win-x64.zip` instead, extract it somewhere it can stay (such as `Documents\WinDock`), and double-click `WinDock.exe`. If you move the folder later, run `WinDock.exe` once from its new place so the Start menu shortcut follows it.

## Using it

- **Bring the dock up** by touching the bottom edge of the screen.
- **Right-click the dock** for Dock Settings, Show/Hide Windows Taskbar, and Turn Off WinDock.
- **Right-click an app** to pin or unpin it, open a new window, quit it, or end it if it is frozen.
- **Pin more apps** from Launchpad (right-click > Pin to Dock) or from the dock while they are running.
- **Open WinDock from the Start menu** while it is running to bring up its settings.

### The Windows taskbar

By default WinDock hides the Windows taskbar while it runs, which also hides the clock and system tray. To get them back, right-click the dock and choose **Show Windows Taskbar**; the dock then sits just above it.

While it is hiding the taskbar, WinDock also switches off Windows' **Show flashing on taskbar apps** setting. Otherwise Windows pops the hidden taskbar up whenever an app wants attention and holds it there, which makes snapped windows stop short of the screen edge. The dock bounces that app's icon instead.

Turning WinDock off restores the taskbar, and that setting, exactly as they were. If WinDock is ever force-closed and the taskbar stays hidden, start WinDock again and turn it off normally.

## Uninstall

Open **Settings > Apps > Installed apps**, find WinDock, and choose **Uninstall**. This stops WinDock, puts the Windows taskbar back, and removes the app, its shortcut and its start-at-sign-in entry. It asks whether to delete your settings and pinned apps too; say No to keep them for a reinstall.

Installing a newer `WinDock-Setup.exe` over an existing install is an update: your settings and pins are kept.

For the portable version, run this from the folder you extracted, then delete the folder:

```powershell
.\WinDock.exe --uninstall
```

## Known limits

- The bar is translucent rather than blurred.
- On a monitor that uses different display scaling from the main one, the dock can look slightly soft.
- With "Stay hidden under windows" on, a maximized window keeps the dock from appearing on that monitor.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer.

```powershell
dotnet run -c Release
```

`tools\publish.ps1` builds the installer and the portable zip (the installer is the same exe under a "Setup" name), and `tools\make-icon.ps1` regenerates the icon.

## License

[MIT](LICENSE)
