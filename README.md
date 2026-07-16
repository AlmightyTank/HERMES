# HERMES 0.1.0-alpha12.6.2

Alpha12.6.2 keeps the inventory-only HERMES workspace from Alpha12.6.1 and simplifies the alert system.

## Simplified alerts

- EFT shows only one HERMES notification at a time.
- Native notifications use one short line, such as `HERMES • Loadout: 1 critical issue`.
- Long descriptions and custom notification text colors were removed.
- Clicking the notification opens the exact HERMES workspace directly.
- An alert is removed automatically when its underlying condition clears.
- Dismissing an alert suppresses the same unchanged condition until it clears and occurs again.
- The Assistant workspace now uses a compact Alerts box instead of expandable notice cards and history.
- The Alerts box contains only Check, Clear, Open, and dismiss controls.
- F12 no longer exposes repeat cooldown, overlay auto-dismiss, maximum-visible, or change-only settings.

## Inventory workspace layout

- Keeps EFT's native Character tabs visible above HERMES.
- Keeps EFT's bottom task bar visible below HERMES.
- Uses a compact fixed header with Reset, Refresh, and Back controls.
- Uses a left workspace rail on wide layouts and two compact navigation rows on narrower layouts.
- Gives the selected workspace the remaining clipped content area.
- Back restores the previously selected EFT inventory tab instead of closing the inventory screen.
- HERMES remains a separate tab after Prestige; Achievements and Prestige are not replaced.

## Build and deployment

Open `HERMES.sln` and use **Build → Rebuild Solution**. The project retains the existing automatic deployment configuration for `C:\RealSPT`.

This package has not been compiled in this environment because .NET SDK/MSBuild is unavailable. Source structure, project XML, settings bindings, version metadata, and ZIP integrity were validated.
