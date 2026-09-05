---
target: src/CodexTray/PopupForm.cs
total_score: 28
p0_count: 0
p1_count: 0
timestamp: 2026-09-05T18-28-42Z
slug: src-codextray-popupform-cs
---
Method: dual-agent (A: /root/design_review · B: /root/evidence_review)

The layout is now solid. I would keep it and make a small clarity/accessibility pass. No blocking visual issue was found in these two screenshots; this was not a full screen-reader or interaction certification.

The quota cards are easy to scan, expiry columns align well, and the single-weekly layout uses its width effectively. The interface suits a tray utility: familiar system typography, restrained color, and no unnecessary decoration. It does not need a different aesthetic.

Priority issues:

1. **[P2] Two equal chart series look like one.** In the 100% screenshot, the purple weekly line covers the green 5-hour line. The source draws weekly last. A user could reasonably think the 5-hour history is missing. Give the series distinct line styles (solid/dashed) and retain both values in the hover tooltip; do not offset the actual values. Suggested command: `$impeccable polish`.
2. **[P2] The two weekly statistics need a little explanation.** `~0% · partial history` can be read as zero usage across the entire day, while the budget looks like an official daily allowance. Keep the compact numbers, but explain recorded coverage and that the budget divides remaining weekly allowance by time until reset. Add `% remaining` to the chart's unit labeling. Suggested command: `$impeccable clarify`.
3. **[P2] Keyboard and accessibility support stops short of chart details.** Buttons have names and tab order, but the active range is conveyed by color, historical points require mouse hover, and the painted summary omits quota reset countdowns and the banked-reset headline count. Expose selected state, include those values in the accessible summary, and provide a keyboard route to recorded readings. Reset list rows already have accessible text. Suggested command: `$impeccable harden`.
4. **[P2] Refresh cooldown is easy to misread.** The disabled button does not explain its manual-refresh cooldown; the footer's next-check time refers to automatic polling. Provide a concise reason/countdown while preserving the existing throttling. Existing connection errors already offer useful recovery guidance, so a broad error-flow rewrite is unnecessary. Suggested command: `$impeccable clarify`.
5. **[P3] Finish the control and label polish.** The large mint Refresh button attracts more attention than a periodically updating monitor needs; make it a secondary action. Normalize the oversized gear relative to the ellipsis and close icon. Rename the reset column `In` to `Time left`, and clarify that the dropdown selects a usage pool, not the model Codex will run. Keep the popup menu access the user requested. Suggested command: `$impeccable polish`.

Design health: **28/40 — good**, using Impeccable's subjective 0–4 heuristic scale.

| Heuristic | Score | Main observation |
|---|---:|---|
| System status | 3 | Freshness and next check visible; manual cooldown unclear |
| Familiar language | 3 | Partial history and budget need context |
| Control and freedom | 3 | Escape, close, pause, and Cancel available |
| Consistency | 3 | Coherent layout; icon treatment varies |
| Error prevention | 3 | Read-only credits and conservative polling |
| Recognition | 3 | Main information visible; pool purpose needs a cue |
| Efficiency | 3 | Fast tray glance; historical inspection is mouse-only |
| Minimalism | 3 | Clear grouping; Refresh is overemphasized |
| Error recovery | 2 | Useful messages, but recovery actions require menu navigation |
| Help | 2 | Basic tooltips/docs; chart concepts lack contextual help |

Cognitive load is low-to-moderate. Controls are grouped into small choices; having eight controls on the screen is not itself an eight-way decision. The remaining load comes from interpreting the weekly statistics. The experience starts well with an immediate quota answer; uncertainty appears when interpreting incomplete history or waiting for a disabled refresh.

Persona checks: a power user can check remaining quota quickly, but cannot inspect history entirely by keyboard. A screen-reader user has named controls and an aggregate description, but lacks selected-range state and some painted data. A first-time user may mistake the pool selector for a model switch or the budget for a separate allowance.

Measured contrast is strong: primary text is **16.26:1** on the background and **14.53:1** on cards; muted text is **6.67:1** and **5.96:1** respectively. No text-contrast failure was found among the reviewed theme pairs. Small text size is a separate readability consideration. The HTML/CSS detector returned zero findings, but cannot meaningfully validate native WinForms, so its result is inconclusive. The screenshots and source are the relevant evidence. No browser overlay applies to this native UI.

Minor observations: standardize `5h` versus `5-HOUR` where useful and add a close-button tooltip. Keep the honest gaps in the chart and preserve strict read-only reset tracking. A useful design question for the next pass is whether the default glance answers quota remaining before asking users to interpret history.
