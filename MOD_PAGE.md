# HERMES

HERMES stands for **Hideout, Economy, Resource, Market, and Equipment System**.

It is a mostly read-only in-game assistant for SPT that helps you understand your active PMC profile, with alpha inventory tag edits available only through explicit confirmation.

It adds a native **HERMES** tab to the Character screen and in-raid inventory screen, then brings together item values, trader choices, Flea estimates, stash cleanup, crafts, Hideout upgrades, loadout readiness, quest-key knowledge, and pre-raid planning in one place.

HERMES does not use an external AI service and does not send your profile anywhere. Most of HERMES only reads local SPT data and presents analysis inside the game; Items & Market can apply, change, or reset inventory tags on selected owned copies after a confirmation popout.

## Requirements

- SPT **4.0.13**
- BepInEx, as included with SPT
- A normal SPT installation with both the client and server running

## What It Does

- Adds a native **HERMES** workspace inside EFT's Character and inventory UI
- Provides a local conversational Assistant with selected-item context
- Adds **Ask HERMES** item actions for stash, equipment, traders, Flea previews, crafts, and Hideout items
- Compares trader purchase and sale options using your active profile
- Estimates Flea Market value, listing fees, and trader-versus-Flea sale choices
- Applies, changes, or removes selected owned-copy inventory tags from Items & Market after explicit confirmation
- Reviews Hideout upgrade requirements and active production status
- Checks craft readiness, acquisition costs, profit, and availability
- Finds stash cleanup opportunities, duplicates, damaged gear, reserves, valuables, and sale destinations
- Reviews loadout readiness, ammo, armor, medical coverage, insurance, provisions, and carried value
- Includes a Raid Planner with map-specific pre-raid readiness
- Connects active quests to required access keys and maps with embedded quest-key knowledge
- Supports a configurable pre-raid food-and-water requirement, including custom consumables when their item data exposes hydration or energy effects

## Main Workspaces

### Assistant

Ask local, profile-aware questions about your next steps, raids, items, stash, crafts, Hideout, and loadout. The Assistant is rule-based and runs locally; it is not ChatGPT and does not contact an online service.

### Items & Market

Look up an item and see owned-copy value, installed child-part value, trader prices, Flea estimates, quest usage, quest-key knowledge, Hideout needs, and craft associations. HERMES tries to resolve the exact owned item instance when you open an item from your stash or equipment.

### Stash

Review your stash for cleanup ideas, sale candidates, duplicate items, condition warnings, valuable items, and reserved items you may want to keep.

### Loadout

Check readiness before a raid, including ammo, armor, medical coverage, insurance, value at risk, food, drink, and other practical concerns.

### Crafts & Hideout

See craft readiness, missing ingredients, output value, estimated profit, active production, and Hideout upgrade requirements.

### Raid Planner

Select a map and review readiness, likely quest requirements, relevant quest keys, and warnings that apply to that specific location.

## Installation

1. Download the HERMES release ZIP.
2. Remove older HERMES files if you installed a previous version.
3. Extract the ZIP into your SPT root folder.
4. Confirm the files end up here:

```text
BepInEx/plugins/HERMES/Hermes.Client.dll
BepInEx/plugins/HERMES/ask_hermes.png
SPT/user/mods/HERMES/Hermes.Server.dll
```

5. Start the SPT server.
6. Launch the game.
7. Open the Character screen and select **HERMES**, or press **F8**.

## Configuration

HERMES creates its BepInEx configuration after first launch. Open the in-game Configuration Manager with **F12** to adjust Assistant, market, Hideout, craft, stash, loadout, Raid Planner, notification, and interface settings, including **Interface -> Font size percent**.

The top **Refresh** button performs a stronger source recheck when you have changed gear, stash contents, Hideout state, quest progress, or other profile data and want HERMES to reread it immediately.

## Important Notes

- HERMES will not buy, sell, move, craft, repair, insure, accept quests, complete quests, or alter inventory structure.
- Inventory tag edits are limited to explicitly selected owned copies, show old and new tag values in a confirmation popout, and reject missing, moved, or stale items before writing.
- HERMES uses the installed SPT database and your active profile as the source of truth.
- Flea and trader values are estimates based on available local SPT data and may not match every economy setup or heavily customized mod list.
- Quest-key knowledge is embedded and resolved against your installed SPT data. Entries that do not match your SPT version are ignored safely.
- If you use multiple profiles, HERMES scopes prepared data to the active profile.

## Troubleshooting

- Make sure both the client and server HERMES DLLs are from the same release.
- Remove duplicate HERMES DLLs from old install locations.
- Confirm the SPT server log reports the HERMES server mod.
- Confirm the BepInEx log reports the HERMES client plugin.
- If something looks stale, press **Refresh** in HERMES.
- For bug reports, include `Player.log`, the BepInEx log, and the SPT server log.

## Compatibility

Built for SPT **4.0.13**.

HERMES is designed to keep profile writes narrow and confirmation-gated. Mods that heavily alter item templates, trader offers, quests, Hideout data, Flea pricing, or inventory item tag schemas may affect the analysis and alpha tag actions HERMES displays.

## Credits

Created by **AMightyTank**.

Quest-key knowledge is derived from public TarkovForge key-reference data and embedded locally so HERMES does not need web access during play.

## License

MIT.
