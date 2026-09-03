# RxBarcodeListener

A Windows tray app for pharmacy POS workstations. It listens for barcode-scanner
keystrokes system-wide and, when an Rx or Will Call bag barcode is detected, checks
NimbleRx / PioneerRx / Will Call and pops up an on-screen alert (or auto-injects a
service-fee line) so the cashier never has to leave the POS software.

## What it does

- **NimbleRx alert** — warns the cashier not to charge if a paid PO/DO order already exists.
- **California Medicaid alert** — warns not to charge a service fee for that pay method.
- **Auto SOC fee** — when a fill was underpaid (acquisition cost > amount collected) beyond
  a configurable threshold, injects a fee-line UPC directly into the POS sale.
- **Self-updating** — checks an internal server for new versions and installs them automatically.
- **Diagnostics window** — lets non-technical staff view recent logs, check for updates,
  restart the app, or send a report to the developer, all without needing SSH/dev tools.

## Setup

1. Copy `Config.example.cs` → `Config.cs` and fill in real API keys/secrets (gitignored).
2. Copy `.env.example` → `.env` and fill in real values (gitignored).
3. `dotnet build -c Release`

## Where the exe ends up

Build output is redirected outside the repo (see `Directory.Build.props`) so Google
Drive sync cannot lock files during incremental builds.

**After `dotnet build -c Release`**

```
%LOCALAPPDATA%\RxBarcodeListener-build\bin\Release\net8.0-windows\RxBarcodeListener.exe
```

Examples on POS workstations:

- `C:\Users\Install\AppData\Local\RxBarcodeListener-build\bin\Release\net8.0-windows\RxBarcodeListener.exe`
- `C:\Users\Pioneer\AppData\Local\RxBarcodeListener-build\bin\Release\net8.0-windows\RxBarcodeListener.exe`

**After first-run install** (when the exe is launched from Downloads or elsewhere)

The app copies itself to:

```
%LOCALAPPDATA%\RxBarcodeListener\RxBarcodeListener.exe
```

Examples:

- `C:\Users\Install\AppData\Local\RxBarcodeListener\RxBarcodeListener.exe`
- `C:\Users\Pioneer\AppData\Local\RxBarcodeListener\RxBarcodeListener.exe`

## Notes

- Secrets never leave your machine — `Config.cs`, `.env`, and this repo's build/deploy
  scripts (`*.ps1`) and internal docs (`*.md` other than this file) are intentionally
  kept out of git.
- Requires Windows + .NET 8 (WinForms).
