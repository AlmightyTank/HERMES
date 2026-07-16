# HERMES 0.1.0-alpha12.6.2 local test checklist

## Build

- Open `HERMES.sln` and run **Build → Rebuild Solution**.
- Confirm the client log reports `HERMES 0.1.0-alpha12.6.2 loaded`.
- Confirm no client or server HERMES exceptions occur during startup.

## Simplified alerts

- Trigger two or more actionable conditions and confirm EFT displays only one HERMES notification at a time.
- Confirm each native notification is one line and begins with `HERMES •`.
- Confirm no long description appears under the title.
- Click a notification and confirm it opens the exact HERMES workspace.
- Dismiss a notification and confirm the same unchanged condition does not immediately return.
- Correct the underlying condition, refresh HERMES, and confirm the stale alert disappears.
- Confirm the next queued alert appears after the first is opened, dismissed, or cleared.
- Open Assistant and confirm the compact Alerts box does not use expandable cards or history.
- Test Check, Clear, Open, and ×.
- Confirm F12 contains the reduced **Assistant Alerts** options and no obsolete cooldown, auto-dismiss, maximum-visible, or change-only controls.

## Main Character screen

- Open Character and confirm the native HERMES tab remains after Prestige.
- Confirm Overall, Gear, Health, Skills, Map, Tasks, Achievements, and Prestige remain visible and usable.
- Confirm the EFT top tab strip and bottom task bar remain visible and clickable.
- Test every HERMES workspace and Back to Inventory.

## In-raid inventory

- Enter a raid and open inventory.
- Confirm HERMES appears after Prestige.
- Confirm F8 opens and returns from HERMES without closing inventory.
- Confirm alerts remain disabled during raids by default.
- Enable raid alerts in F12 and confirm only one compact notification can appear.

## Regression

- Verify Assistant, Items & Market, Hideout, Crafts, Stash, Loadout, and Raid Planner.
- Verify Ask HERMES from inventory, equipped items, traders, flea offers, crafts, and hideout requirements.
- Reopen Character at least five times and confirm one HERMES tab exists with no flicker or duplicate alert state.
