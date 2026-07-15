# HERMES Alpha11.9 local release checklist

Run these checks against the same SPT 4.0.13 installation configured by `SptRoot`.

## Build and deployment

1. Close the game client and SPT server.
2. Open `HERMES.sln` in Visual Studio.
3. Select the intended configuration and run **Build → Rebuild Solution**.
4. Confirm the build project creates `HERMES-0.1.0-alpha11.9.zip` and deploys both DLLs when auto-deploy is enabled.
5. Start SPT.Server and confirm HERMES loads without dependency or router errors.
6. Start the client and confirm the log reports `HERMES 0.1.0-alpha11.9 loaded`.

## Interface smoke test

- Open and close HERMES with the configured shortcut.
- Open Item Search, Hideout, Crafts, Stash, Loadout, and Raid Planner.
- Refresh every tab once.
- Confirm error panels retain a usable Refresh path.
- Confirm the footer shows market/stash/loadout cache counts when diagnostics are enabled.
- Use **Copy diagnostics** and verify the report contains timings and cache statistics but no inventory contents.

## Request reliability test

- Enable detailed logging.
- Trigger two overlapping reads of the same tab or item.
- Confirm the log can report `Sharing in-flight HERMES request` and the diagnostics shared-request count increases.
- Temporarily lower the normal timeout and verify a timeout produces a visible message without freezing the window.
- Restore the timeout before gameplay testing.

## Data regression test

- Confirm Item Search excludes quest-only and handbook-less templates.
- Confirm Market uses cash flea, converted barter, dynamic flea, then handbook fallback.
- Confirm Available Crafts removes recipes with missing ingredients.
- Confirm event-only stations and recipes remain hidden when inactive.
- Confirm Stash protects configured quest and hideout reservations.
- Confirm insured attachments are classified as weapon/armor components and inherit insured assembly state.
- Confirm Saving the Mole shows the science-office key as acquired during the raid.
- Confirm localized quest objective text is shown instead of generic location-progress text.

## Safety

HERMES must not buy, sell, list, move, equip, insure, craft, upgrade, accept, or complete anything during this test.
