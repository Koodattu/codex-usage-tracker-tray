# Codex Tray

A small Windows tray app that shows your remaining OpenAI Codex allowance. One EXE, no installer, no third-party runtime packages. Built with C# and Windows Forms.

**[Download the latest release](https://github.com/Koodattu/codex-usage-tracker-tray/releases/latest)**

## Run

Download `CodexTray.exe` and run it. Requires Windows 10 1903+ or Windows 11 with .NET Framework 4.8, and Codex already installed and signed in with a ChatGPT account. No API key is needed.

The app starts quietly in the tray. If the icon is hidden, look under the taskbar's **^** menu.

- **Left click:** usage popup with reset countdowns, banked reset expiry times, a weekly daily budget, and **24h / 7d / 30d** history.
- **Right click** or **⋯:** menu, refresh controls, Start with Windows, Codex desktop, and the ChatGPT Microsoft Store page.
- **⚙:** Display, Notifications, and About tabs. Choose numbers or rings, one or two icons, and automatic switching between limits.

Icons change from green to amber to red as allowance runs low. Limits your account does not report are hidden. Separate Codex usage pools stay separate. The popup scales with Windows display settings.

Notifications are **off by default**. Enable low-allowance warnings (20% and 10%, adjustable), allowance-restored alerts, or a reminder when a banked reset expires within 24 hours. Allowance alerts follow the selected pool and do not repeat for the same threshold/reset window after restarting. Windows notification settings may hide alerts.

The daily budget divides remaining weekly allowance by the time until reset; it is an even-use budget, not a consumption prediction. Below one day, the popup shows the remaining allowance instead. Stale readings have no budget estimate.

To update, quit the tray app, replace the EXE, and run it again. Preferences and history are preserved. Releases are currently unsigned, so Windows may show a publisher warning.

**About → Check for updates** checks GitHub only when clicked. **Open Releases** opens the download page. There are no background update checks or automatic replacements.

## Data and refresh

Usage is read through the installed Codex CLI about every five minutes, with throttled manual refresh and error backoff. Opening the popup or switching chart ranges makes no requests. The tracker cannot redeem resets.

Local files are stored under `%LOCALAPPDATA%\CodexTray`:

- `settings.json` — preferences.
- `alerts.json` — notification deduplication and last observed allowance, created when alerts are enabled; account/pool keys are hashed.
- `history/<hashed-account>/YYYY-MM-DD.jsonl` — timestamps and remaining percentages, recorded for each usage pool. History survives restarts and covers the last 30 days.
- `logs/current.log`, `previous.log`, `older.log` — local diagnostics, capped at 256 KiB each. Open them from **About → Open logs** or the tray menu.

History starts when the app records usage; it cannot recover earlier activity. Daily files use UTC dates and retain the partial boundary day. Saved history loads after a successful account check.

No telemetry, API keys, credentials, conversations, or raw account responses are saved by the tracker. History is ordinary local text, not encrypted. Codex handles its own authentication.

If the app exits unexpectedly, keep the log files before restarting repeatedly. They record startup/shutdown, refresh stages, and exception types and stack frames, without exception messages or source file paths. Logging is automatic and local; nothing is uploaded. A forced termination or power loss may leave only the last completed operation.

## Build

On Windows, install .NET SDK 9 or newer and the .NET Framework 4.8 Developer Pack, then run:

```powershell
.\build.ps1
```

This builds with warnings as errors, runs the checks, and places the EXE, license, and SHA-256 checksum in `dist`. Quit a running copy from `dist` before rebuilding. No NuGet packages are downloaded.

## Publish

Push a new `<Version>` in `src/CodexTray/CodexTray.csproj` to `main`. In GitHub, select **Actions → Build and release → Run workflow → main**. The workflow builds and tests that revision, then publishes `v<Version>` with the downloadable EXE, license, and checksum. Existing tags and releases are never overwritten. Normal pushes and pull requests only build and test.

Publishing uses GitHub's built-in workflow token; no personal token or OpenAI secret needs to be configured.

[Technical details and troubleshooting](docs/DETAILS.md) · [Apache-2.0 license](LICENSE)

An independent community project, not affiliated with OpenAI. Compatibility depends on Codex's app-server interface.
