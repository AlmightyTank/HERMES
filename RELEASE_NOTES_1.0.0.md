# HERMES 1.0.0 Release Notes

HERMES 1.0.0 is the first full public release of the Hideout, Economy, Resource, Market, and Equipment System for SPT 4.0.13.

HERMES is a read-only in-game assistant that adds a native HERMES workspace to the Character screen and in-raid inventory screen. It analyzes your active PMC profile using local SPT data only, including profile, trader, item, quest, Hideout, craft, and Flea data. It does not contact an external AI service, send your profile anywhere, or perform gameplay actions.

## Highlights

- Native HERMES workspace inside EFT's Character and inventory UI.
- Local, rule-based Assistant with selected-item context.
- Ask HERMES item actions for stash, equipment, trader previews, Flea previews, crafts, and Hideout items.
- Items & Market analysis with owned-copy valuation, installed child-part value, trader prices, Flea estimates, quest usage, quest-key knowledge, Hideout needs, and craft associations.
- Stash review for cleanup opportunities, sale candidates, duplicates, damaged gear, valuables, reserved items, and suggested sale destinations.
- Loadout readiness checks for ammo, armor, medical coverage, insurance, provisions, carried value, and practical raid warnings.
- Crafts and Hideout views for upgrade requirements, active production, missing ingredients, output value, estimated profit, and availability.
- Raid Planner with map-specific readiness, quest requirements, and relevant access-key guidance.
- Embedded quest-key knowledge resolved against the installed SPT database and active profile state.
- Configurable pre-raid food-and-water requirements, including custom consumables when item data exposes hydration or energy effects.

## New In 1.0.0

This release promotes HERMES to version 1.0.0 and adds the final reliability and usability work needed for the first public build:

- Live background refresh now checks server revisions on a shared interval, refreshes only changed workspace summaries, and keeps Assistant alerts current even when the HERMES workspace is not selected.
- New F12 settings control live background refresh and its refresh interval.
- Active-profile saving is available while HERMES is open through the new `/hermes/profile/save` route, with configurable save timing in F12 settings.
- Assistant alert polling now follows the live refresh cadence, retries quickly while prepared data is warming up or stale, and can automatically ask live sync to rematerialize the prepared feed.
- Native HERMES notifications can be right-clicked to dismiss without opening HERMES, and dismissed notices are remembered by the Assistant notice list.
- Opening an Assistant notice now consistently routes to the Assistant workspace.
- Suggested Assistant prompt buttons now submit immediately and stay disabled while an Assistant response is already loading.
- Loadout armor warnings now specifically require body armor or an armored rig, and missing torso armor is reported as a critical readiness issue.
- Player-facing documentation has been rewritten with clearer installation, configuration, compatibility, troubleshooting, and source-build guidance.

## Requirements

- SPT 4.0.13
- BepInEx, as included with SPT
- A normal SPT installation with both the server and client available

Source builds require:

- .NET 9 SDK
- Visual Studio 2022 or another MSBuild-compatible environment
- A local SPT 4.0.13 installation with EFT, BepInEx, and SPT managed assemblies available

## Installation

1. Download `HERMES-1.0.0.zip`.
2. Remove older HERMES files if you installed a previous version.
3. Extract the ZIP into your SPT root folder.
4. Confirm the files are installed here:

```text
BepInEx/plugins/HERMES/Hermes.Client.dll
BepInEx/plugins/HERMES/ask_hermes.png
SPT/user/mods/HERMES/Hermes.Server.dll
```

5. Start the SPT server.
6. Launch the game.
7. Open the Character screen and select HERMES, or press F8.

## Important Notes

- HERMES is read-only. It will not buy, sell, move, craft, repair, insure, accept quests, complete quests, or edit your profile.
- HERMES uses the installed SPT database and active profile as the source of truth.
- Flea and trader values are estimates based on available local SPT data and may differ with heavily customized economy setups or mod lists.
- Quest-key knowledge is embedded locally. Entries that do not match SPT 4.0.13 are ignored safely.
- If you use multiple profiles, HERMES scopes prepared data to the active profile.

## Troubleshooting

- Make sure the client and server HERMES DLLs are from the same release.
- Remove duplicate HERMES DLLs from old install locations.
- Confirm the SPT server log reports the HERMES server mod.
- Confirm the BepInEx log reports the HERMES client plugin.
- Press Refresh in HERMES if profile, stash, Hideout, quest, or gear data looks stale.
- For bug reports, include `Player.log`, the BepInEx log, and the SPT server log.
