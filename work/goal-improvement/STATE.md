# Product improvement working record

## Starting state — 2026-10-03

- Revision: `e9bf8973dccfc5593b537f7d60bf6ba2c133c6e8`, branch `main`; no staged, unstaged, or untracked work.
- Remote: `origin`, `https://github.com/Koodattu/codex-usage-tracker-tray.git`; remote main matches the starting revision.
- Windows Forms / .NET Framework 4.8; SDK 9.0.304; no packages. English UI with system-local dates/numbers; no maintained translation files.
- `.\build.ps1`: build passed with zero warnings/errors; all 60 baseline checks passed. Packaging failed because the user's existing `dist/CodexTray.exe` is running. Preserve that process and verify the release in an isolated directory.
- GitHub credentials work. Network calls sometimes require sandbox escalation. Latest release is v1.3.3; starting main CI passed.
- Release: bump project version, push main (build/test only), dispatch the existing **Build and release** workflow on main. It builds/tests/packages and publishes a new immutable version and assets. No hosting, database, migrations, or deployment configuration changes needed.
- Recovery: no automatic installation; users can quit and replace the EXE with the previous release, retaining local preferences/history. Do not replace a running user copy. Never overwrite release tags.

## Product brief and baseline

Documented: a portable Windows tray companion for people using Codex with a signed-in ChatGPT account. It answers how much allowance remains, when limits reset, and how recorded allowance changed. Opens briefly during coding; history/settings support occasional deeper checks. No conversation access, credentials, telemetry, reset redemption, or automatic updates.

Assumption: quick quota decisions and confidence in the readings matter more than adding providers, spending forecasts, or a larger dashboard. No interviews or analytics were available.

Preserve: unobtrusive tray, fixed 440×636 logical footprint, native DPI, local-only history, pool/account separation, cached chart range changes, missing-window handling, opt-in alerts, error backoff, settings save/cancel, and existing chart hover.

Baseline evidence: synthetic render fixtures in `.artifacts/preview-*.png`; selected before/after captures retained beside this record. The original footer allocates only 203×33 logical pixels to connection/recovery messages. Chart readings require pointer hover; neither a native table nor export exists. A passed reset reads “Resets in / Now,” although allowance remains unconfirmed. Last-read timestamps omit the date even after a long interruption. Existing numerical history tests are healthy.

Journeys: first connection/error recovery; glance at remaining quotas and reset timing; switch pool/range; inspect a recorded reading; review gaps/missing values; retain/reuse recorded data; open/cancel settings; pause/resume and retry without bypassing throttles.

## Selected program and acceptance criteria

| Rank | User job / evidence | Outcome and acceptance | Cost / dependency |
| --- | --- | --- | --- |
| 1 | Trust status and recover; narrow footer and ambiguous pending reset observed in source/fixtures (high confidence) | Full-width actionable status; explicit last-known/pending-reset semantics; dated freshness when needed; disabled refresh explains cooldown, preserving policy; verify initial, busy, failed, paused, stale and restored states | Small UI/presentation change; no data migration |
| 2 | Inspect/reuse exact history; currently hover-only (high confidence); export value is a product hypothesis | Discoverable Readings action; native keyboard table for selected pool and 24h/7d/30d; explicit local timestamps, missing versus zero, recording gaps; CSV with unambiguous timestamps/numbers; cancel/failure keeps context; empty state and bounded large-history performance; no network or production file changes | One focused native view and export model; reuse history, theme and DPI |
| 3 | Broad redesign, forecast/pace, token costs, more providers | Deferred: compact overview is already effective; forecasts from incomplete sampled percentages could overpromise; token costs/providers change scope/privacy | Not justified for this release |

## Design and engineering decisions

- Primary discipline: Interface Design, native operational tool; end-user-ui-ux governs task/accessibility. Impeccable context ran once successfully; Operate/clarify/craft-floor guidance supports the existing visual system. Skill interviews/parallel assessments are replaced by documented agent decisions and sequential self-review per the user's explicit instruction. No independent review or user-approved concept is claimed.
- Compared structures: (a) replace overview with a table (loses quick glance); (b) grow the popup (hurts DPI/work-area fit); (c) focused readings view from the existing overview (selected). Follow the established owned-dialog pattern for protected keyboard inspection/export; return to the same popup and range.
- Intent: coding user checking allowance without losing their place. Hierarchy: remaining quota first, historical context second, recovery/action next. Palette: existing charcoal background, raised neutral surface, mint/five-hour, violet/weekly, amber warning, red depleted. Depth: existing subtle surface shifts. Typography: Segoe UI, 12–14px native text, stronger values. Spacing: existing 4px rhythm, 24px outer gutters; keep compact 30px desktop controls. No new motion.
- Data visualization router/strategy + dashboards specialist: one existing step chart for time change, table for exact lookup. Keep polling separate from navigation. Statistical/missingness guidance: never replace absent with zero, never infer usage across gaps or resets, no forecasts. Accessibility/testing specialists: native table as the non-hover path; test numbers, filtering, keyboard, real rendering and empty/error states separately.
- Architecture skills: existing history model already provides account/pool selection and rolling ranges. Keep it authoritative. Add one view-local snapshot/export module only if needed by the table and file output, not a new data layer. No broad refactor or dependency.
- TDD/diagnosing-bugs: observable UI and exported data are the seams; fixed synthetic readings with literal expected values. Reproduce status defects and new capability absence before implementation; no speculative mocks of internals.

## Research (accessed 2026-10-03)

First-party documentation, not installed competitor interactions:

- [CodexBar UI](https://github.com/steipete/CodexBar/blob/main/docs/ui.md): documented compact quota/reset presentation, explicit stale styling and partial-history labels. Relevant lesson: keep quota, reset and freshness together; do not copy its much larger provider/settings surface.
- [CodexBar refresh loop](https://github.com/steipete/CodexBar/blob/main/docs/refresh-loop.md): documents retained stale readings with status. Apply clearer local status while preserving this app's conservative five-minute policy.
- [ccusage blocks](https://ccusage.com/guide/blocks-reports): reports separate observed blocks, active state and estimates. Apply explicit recorded-data scope; avoid promoting this app's sparse local history into confident forecasts.
- [Claude usage guidance](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work): explains usage windows shared across product surfaces. Adjacent lesson: label quota windows and keep their meaning distinct from chart lookback or token counts.
- Manual workaround: hover points repeatedly or open local JSONL files. A keyboard table and local CSV remove that friction without new data collection.

## Completed batches and verification

### Trustworthy overview and recovery

- Reproduced first-use recovery clipping with `--status-checks` before changing production code; the test failed against the original layout. Full-width status now fits at 100/150/200%.
- Explicit last-known header, pending-reset labels, dated older readings, and throttle countdowns; preserved quota numbers and existing refresh/backoff policy. Retained the established button fill: native WinForms disabled painting ignores the requested foreground and made the proposed darker disabled surface illegible. Avoided adding custom button painting for a cosmetic change.
- End-user-ui-ux and Impeccable clarify identified recovery text getting lower priority than timestamps. Interface Design retained the existing compact layout and moved timestamps beneath the actions. No new animation.

### Inspect and reuse recorded history

- Readings entry next to the chart; native virtual table and stable selected-pool snapshot; current row details, gap caveat, empty state, 24h/7d/30d selection, and return to the same popup/range without network requests.
- Explicit CSV save, selected pool/range only, chronological UTC rows, invariant full-precision numbers, empty cells for missing data, quoting, atomic replacement and recoverable file failures.
- Added tests first for the new data/view interfaces. Reproduced and fixed a DPI row-height defect; selection detail initially followed `SelectionChanged` before `CurrentCell` updated, reproduced by a literal timestamp assertion and fixed at `CurrentCellChanged`.
- Architecture guidance kept snapshot/export behavior together and reused the popup button factory through Theme. No new data store or package. Data/accessibility specialists drove the native table alternative, missingness caveat, UTC export, stable selection and local time offset.
- Full retained-history workload: 43,201 synthetic rows plus construction and three 100/150/200% renders took 94–119 ms across normal verification runs on this machine. This is a local responsiveness check, not a claimed performance improvement or production benchmark.

### Checks and limitations

- `dotnet build tests/CodexTray.Tests/CodexTray.Tests.csproj -c Release --configfile NuGet.Config --nologo`: zero warnings/errors.
- `CodexTray.Tests.exe`: **68 checks passed** after integration. Targeted `--status-checks` and `--history-checks` were also run during the failing-test/fix cycles. Full clean committed build remains the final release gate.
- Visually inspected actual WinForms renders: original/new first-use and stale popup, dense table at 150/200%, and empty readings. Other fixture renders cover both/single windows, chart ranges/hover, settings, and reset scrolling. Final explicit correction: restored the established button fill for readable native disabled text.
- Native desktop preview exposed its accessibility tree, but screenshot capture failed (`FrameArrived timed out` / `window capture timed out`) and an element click failed (`coordinate input geometry is unavailable`). Computer Use was then stopped by the physical Escape key. No further Computer Use calls were made; owned preview processes were closed. **No completed manual mouse/keyboard or screen-reader pass is claimed.** Automated offscreen WinForms modal and control tests verify the affected journey; save-dialog interaction itself remains a manual follow-up.
- `impeccable detect --json` on the changed C# surfaces returned `[]`. This web-oriented detector provides limited evidence for native rendering; it does not replace the render/control checks. Windows-only product; mobile/browser layouts are not shipped surfaces.
- Export usefulness remains a product-value hypothesis awaiting real-user feedback. No forecast or additional data collection added.

## Final self-review

⚠️ DEGRADED: single-context (user explicitly required one agent). Questions skipped: routine direction and review decisions delegated by the user.

- Standards axis (`code-review`): reviewed tracked diff and all new files against supplied AGENTS guidance; no unrelated user changes, sensitive payloads, dependency additions, broad refactor, or external-contract changes. Removed obsolete footer drawing/state. Existing build/release setup unchanged.
- Spec axis: the two evidence-backed priorities are implemented across UI, data and file output, with regression coverage and before/after artifacts. Remaining manual desktop accessibility and native save-dialog checks are disclosed, not represented as passed. No required repository check has been bypassed.
- Design critique: the overview already has useful task-specific hierarchy; retain it. The original weaknesses were recoverability (clipped text), uncertainty (reset “Now”), and pointer-only historical inspection. Improved without growing the popup or adding a dashboard. Native table selection/focus and existing colors preserve recognition; disabled refresh explains its wait in text.

## Release plan / next action

Version selected: **1.4.0**. Main remains at the starting revision and is not protected; v1.4.0 does not yet exist. Commit the reviewed sources, tests, docs and compact synthetic evidence. Build that exact commit with the unchanged `build.ps1` in `.artifacts/release-check` so the user's running `dist` EXE stays untouched. Push normally, follow push CI, dispatch `build-release.yml` on main, verify published tag/revision/assets/checksum, and run synthetic smoke checks against the downloaded binary. Record the final release receipt here after verification.

## Local review

Build with `.\build.ps1` after quitting a copy running from `dist`, or use the isolated `.artifacts/release-check` output from this run. Run `tests\CodexTray.Tests\bin\Release\net48\CodexTray.Tests.exe --preview` for synthetic overview/history; add `empty`, `error`, or `reset` for those states. Screenshots in `evidence/` are synthetic, not account data. No local preview or release command requires a database.
