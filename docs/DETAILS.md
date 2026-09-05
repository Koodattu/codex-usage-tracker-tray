# Codex Tray

A small Windows tray app for OpenAI Codex. C# / Windows Forms, no third-party packages, no installer, no embedded browser.

## Run

Run **`dist/CodexTray.exe`** after building, or copy that executable anywhere you want to keep it. No adjacent DLLs or configuration files are needed. Windows 10 1903+ and Windows 11 include the required .NET Framework 4.8 or newer. Codex must already be installed and signed in with a ChatGPT account under the same Windows user.

Launching the executable adds a tray icon without opening a window, showing weekly usage by default. Click the icon to open usage. Windows may initially place it under the notification area's **^** overflow; drag it into the visible tray if desired. Only one copy runs per Windows session.

- **Left click** the icon: open or close usage. Escape or clicking elsewhere closes the view.
- **Right click**, or click **⋯** in the popup: open the same menu for display choices, usage pools, pause, startup, app shortcuts, executable paths, and quit.
- **⚙ Settings** in the popup opens a dialog that covers the entire usage view, matching its size and position. It offers numbers/rings, one/two icons, switching interval, and Start with Windows. Changes apply when you select Save; Cancel leaves preferences unchanged.
- **Color**: green above 50% remaining, amber from 21–50%, red at 20% or below. Percentages round down. Gray with an amber dot indicates last-known data, a pending reset, or paused refresh. A dash or question mark means unavailable.
- **Usage view**: remaining quota, reset countdowns, locally recorded usage, banked resets with individual expiry dates in local time, update time, and next check. The reset list scrolls when needed.
- **Chart ranges**: select **24h**, **7d**, or **30d** for a rolling history window. The selected range is saved. These buttons use local history and make no network requests; they show quota remaining over time, not daily token totals.

### One or two tray icons

Choose **One icon · weekly**, **One icon · 5-hour**, **One icon · switch between limits**, or **Two icons · one for each limit**. Switching defaults to ten seconds and is adjustable from 5–300 seconds in Settings. Hover the icon to identify its current window. Switching uses already-fetched data and makes no network requests.

Unavailable windows are omitted from the tray, quota cards, and chart legend. If only one window is reported, all display choices use that available window in one icon, and the popup gives it a full-width card. If neither is available, one neutral icon remains so you can still reach the menu. Choices are based on returned data, not on assumptions about subscription plans. Existing explicitly saved display choices are preserved.

Numeric icons fit the entire value, including `58` and `100`, on one line. The popup uses native per-monitor Windows DPI rather than the legacy WinForms cached DPI. At 150% it renders at 660 × 954 physical pixels, and the tray image is 24 pixels; smaller work areas cap the popup size to fit. Menus and the settings dialog scale as well. No external runtime configuration file is required.

### Separate usage pools

Codex can return several quota pools. Select one from the popup or **Usage pool** in the tray menu. Names come from Codex, with a neutral fallback if it omits a name. The choice is saved. Five-hour and weekly windows always belong to the **same selected pool**.

Some accounts currently report only weekly usage for the main Codex pool and both windows for an additional pool. An absent five-hour window is hidden; the app never borrows it from another pool. Windows are classified by duration, not by the `primary`/`secondary` field name. Unexpected durations are not displayed as five-hour or weekly values.

## Refresh behavior

The app uses Codex's local `app-server` interface over redirected standard input/output:

1. Find an installed native Codex CLI and start a hidden process.
2. Complete `initialize` / `initialized`.
3. Read `account/read` without forcing a token refresh, then `account/rateLimits/read`.
4. Close the process. A private Windows job object also cleans up descendants if a read is cancelled or the tracker exits.

Automatic checks run about every **five minutes**, with up to 15 seconds of jitter plus timer scheduling. Successful manual checks are limited to one per minute. Failures back off for 5, 10, 20, then 30 minutes; manual refresh respects that backoff. Requests never overlap and time out after 25 seconds. Opening the popup and changing display modes do not make network requests. Startup at Windows sign-in waits 20 seconds before checking.

The UI updates countdowns every 15 seconds. After sleep, a due refresh runs when Windows resumes timer delivery. A passed reset timestamp is displayed as awaiting refresh; it does not manufacture a new 100% reading. Values older than ten minutes are marked as last known.

## Data and privacy

- The tracker does **not** read `auth.json`, session conversations, or token logs. It never calls OpenAI's HTTP endpoints directly. Codex owns authentication and its upstream requests.
- It only reads account and quota information. It cannot spend or redeem reset credits, send messages, or start an AI turn.
- No telemetry or diagnostic payload logging. Backend error details are not shown or retained.
- Preferences are saved atomically to `%LOCALAPPDATA%\CodexTray\settings.json`: display mode, icon choice, switching interval, pool choice, chart range, and any manually selected executable paths. Start with Windows is stored separately in the Windows registry.
- Every successful usage check appends observations for **all returned Codex pools** to `%LOCALAPPDATA%\CodexTray\history\<hashed-account>\YYYY-MM-DD.jsonl`. These are plain UTF-8 JSON Lines files: one JSON object per pool per check, containing a Unix timestamp, pool ID, and five-hour/weekly remaining percentages (null when unavailable). No credentials, email addresses, conversations, or raw backend responses are saved. The account folder is a SHA-256 hash derived from the account identity and plan; it separates histories but is not encryption. Codex does not expose a workspace identity through this account response, so separate workspaces under the same login cannot be distinguished by this check.
- History survives restarts and switching pools. It is loaded after the first successful account/usage check so records from another login are not displayed. If account identity is unavailable, that run uses memory-only history. Changing account or plan selects a separate history folder.
- Charts retain a rolling **30 days**, capped at 43,201 samples per pool in memory. Daily files use UTC dates; older files are removed on a successful write, keeping up to 31 daily files per account to cover the partial boundary day. Interrupted or malformed rows are skipped; storage failures show a message while live usage continues to work.
- Chart gaps longer than 15 minutes stay gaps. The app does not reconstruct activity before it was running.
- Reset count is authoritative even when the service caps the details list. Available reset details appear individually, earliest expiry first. Missing details are explicitly labeled unavailable; a reported null expiry means no expiry. Reset details are refreshed from Codex and are not stored in chart history.

Example history row (synthetic):

```json
{"timestamp":1788600000,"pool":"codex","fiveHourRemaining":null,"weeklyRemaining":58}
```

Versions before 1.2 kept chart history only in memory. That older history cannot be recovered; persistent recording starts with version 1.2.

## Notifications, pacing, and updates

All three notification switches default to off, including for existing settings files. Low-allowance warnings use adjustable remaining-percentage thresholds (20% and 10% initially). Allowance-restored alerts require a fresh positive observation after an observed zero; reaching a reset timestamp alone never triggers one. Allowance alerts follow the selected pool and require an identified account and a future reset timestamp. Expiry reminders use reported available credits and fire on a successful refresh within 24 hours of expiry; matching expiry times are grouped. Pausing refresh also pauses these checks. No extra Codex requests are made for alerts.

`%LOCALAPPDATA%\CodexTray\alerts.json` stores hashed notification/account/pool keys, timestamps, and last observed remaining percentages. Alert records are retained for 30 days and saved before attempting to show a Windows notification, preventing repeated attempts after a restart. Windows may suppress delivery through its notification settings or Do not disturb. If alert state cannot be read or written, alerts are suppressed and the popup reports the problem. For a damaged alert file, quit the app and remove only `alerts.json` before restarting; this resets deduplication without removing history or settings.

The weekly daily budget divides remaining percentage by fractional days to the reported reset, rounded down to one decimal place. It describes an even-use budget rather than predicting how long future tasks will take. Within the final 24 hours it shows remaining allowance instead of extrapolating a large daily figure. It is unavailable on failed, paused, stale, missing-reset, or reset-pending readings.

About shows the installed assembly version. Check for updates makes one unauthenticated HTTPS GET to this repository's GitHub latest-release endpoint, with a ten-second timeout and a one-minute cache. GitHub rate-limit errors pause checks for an hour. Opening About does not check automatically. Draft, prerelease, or malformed version metadata is rejected. Open Releases always opens this repository's fixed HTTPS release page; response-provided download URLs are not executed. Nothing downloads or replaces the EXE in the background. Reset credits remain strictly read-only: there is no redemption action or API request.

## Windows integration

CLI detection checks versioned app-managed runtimes (newest file first), the standalone installation, PATH, and common npm native-binary locations. **Setup → Choose Codex CLI executable…** handles custom installations; select the command-line binary, not the desktop GUI or an npm `.cmd` launcher.

Desktop launch checks known standalone locations, resolves a registered `OpenAI.Codex` application through Windows AppsFolder, then tries the registered `codex:` protocol. A custom desktop executable can also be selected. The ChatGPT Store shortcut targets the requested ChatGPT listing (`9NT1R1C2HH7J`), currently titled **ChatGPT Classic**, separately from the Codex desktop shortcut.

**Start with Windows** uses the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexTray` entry with a quoted absolute path. It is opt-in and needs no administrator rights. If you move the executable after enabling it, toggle the option off and on at the new location. To remove the app, turn off startup, quit, delete the executable, and optionally remove its preferences folder.

## Build and verify

Prerequisites: Windows, a .NET SDK that supports building .NET Framework projects, and the **.NET Framework 4.8 Developer Pack** (included with suitable Visual Studio installations). No packages are downloaded; `NuGet.Config` intentionally has no sources.

```powershell
.\build.ps1
```

The script builds Release with warnings as errors, runs the dependency-free verification executable, then copies the standalone app into `dist`. Development symbols and generated runtime configuration remain in the build directory; the shipped executable runs on the Windows-provided runtime without those files.

The verification suite covers quota selection, separate pools, missing values, overage, individual reset expiries, timestamp handling, backoff, 24h/7d/30d chart ranges, history bounds, persistence across restarts, account separation, interrupted writes, retention cleanup, storage failures, real JSON-RPC process transport, interleaved messages, cancellation and process cleanup, both icon modes at 16–64 pixels, numeric clipping, icon selection/fallback, rotation timing, popup menu/settings buttons, and rendering at 100%, 150%, and 200% sizes. Rendered samples in `.artifacts/preview-*.png` and `.artifacts/tray-numbers.png` use **synthetic test data**.

Optional read-only integration checks, run as the Windows user signed into Codex:

```powershell
.\tests\CodexTray.Tests\bin\Release\net48\CodexTray.Tests.exe --live
.\tests\CodexTray.Tests\bin\Release\net48\CodexTray.Tests.exe --inspect
```

`--live` reports which fields and pools are available without printing usage percentages or account identity. `--inspect` reports window durations and checks registered desktop discovery without launching it. These commands each make one usage request, so they are manual diagnostics, not polling commands.

`CodexTray.Tests.exe --ui-smoke` briefly shows sample-data popups on each connected monitor and verifies native DPI, popup bounds, and tray image size. It makes no usage requests. This was checked on the development machine's 144-DPI monitor and two 96-DPI monitors; the 144-DPI check reproduces the legacy WinForms `DeviceDpi = 96` condition that the native sizing corrects.

`CodexTray.Tests.exe --settings-smoke` verifies that the modal settings dialog appears above the always-on-top usage popup, closes through Cancel, and restores the popup's input. It opens temporary windows and closes them automatically without usage requests or preference changes.

`--settings-update-smoke` repeats the modal check while a simulated update request is pending. `--notification-smoke` shows one labeled test notification without changing preferences. `--update-smoke` makes one real read-only GitHub update check. The regular suite covers alert defaults, persistent deduplication, restoration, expiry grouping, storage failures, pacing, release metadata, cancellation, and all three settings tabs using synthetic data.

The app-server interface can evolve. Tested against locally installed Codex CLI 0.145.0 and app-managed 0.153.4. Store activation discovery and popup positioning across different monitor DPIs were checked on the development machine. Explorer restarts and the startup toggle should also be exercised on the target Windows setup before wider distribution. The executable is unsigned.

## Sources

- [Official Codex app-server protocol and rate-limit/reset-credit fields](https://learn.chatgpt.com/docs/app-server)
- [Microsoft: .NET Framework versions included with Windows](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server)
- [Microsoft: native window DPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow) and [DPI change notifications](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged)
- [ChatGPT Microsoft Store listing](https://apps.microsoft.com/detail/9nt1r1c2hh7j)

Original implementation informed by the supplied survey; no third-party tracker source was copied or bundled. See [LICENSE](../LICENSE).
