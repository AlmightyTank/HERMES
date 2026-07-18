# HERMES

HERMES is a read-only in-game assistant for SPT 4.0.13. It adds a native **HERMES** tab to the Character and in-raid inventory screens and analyzes the active PMC profile without changing inventory, quests, traders, crafts, or hideout state.

This source is prepared as **0.1.0-rc.2.2.1**. The final `0.1.0` tag should be created only after the runtime checklist in `RELEASE_CHECKLIST.md` passes.

## Main features

- Local conversational Assistant with selected-item context and prepared alerts
- Trader purchase and sale comparison using the active profile
- Local Flea Market pricing, listing-fee estimates, and trader-versus-Flea recommendations
- Hideout upgrade requirements and active production status
- Craft readiness, acquisition cost, profit, and availability filtering
- Stash reservation, cleanup, duplicate, condition, and sale-destination analysis
- Loadout readiness, ammunition, armor, medical, insurance, and value analysis
- Raid Planner and map-specific pre-raid readiness that invalidates and rereads Loadout data after the selected map stabilizes
- Embedded quest-key knowledge catalog that links active quests to their required access keys and exact raid maps while using the installed SPT database for key IDs and objective progress
- Configurable pre-raid food-and-water requirement using carried item Hydration/Energy effects, including custom consumables
- **Ask HERMES** item context action that always opens Items & Market with the selected item already resolved
- Stash-row navigation resolves the exact session-scoped item instance and opens Items & Market with it selected
- Native EFT tab, controls, notifications, and Ragfair-derived UI assets

HERMES does not use an external AI service and does not perform game actions.

## Requirements

- SPT **4.0.13**
- .NET **9 SDK** for source builds
- Visual Studio 2022 or another MSBuild-compatible IDE
- A valid SPT installation containing the managed EFT and BepInEx assemblies

## Installation

Extract the release ZIP into the SPT root. The final layout is:

```text
BepInEx/plugins/HERMES/Hermes.Client.dll
SPT/user/mods/HERMES/Hermes.Server.dll
```

Remove older HERMES DLLs before installing a new version. Start the SPT server, then launch the game. Open the Character screen and select **HERMES**, or press **F8**.

## Configuration

HERMES creates its BepInEx configuration after the first launch. Open the in-game Configuration Manager with **F12** to change Assistant, market, hideout, craft, stash, loadout, Raid Planner, notification, and interface settings.

Normal tab changes rebuild the visible client presentation and read prepared server summaries. The top **Refresh** button performs the stronger source recheck. In **Pre-Raid Readiness**, **Require food and water** can require carried hydration and energy provisions.

## Building from source

The projects expect the SPT root at `C:\RealSPT` by default. Override it without editing project files by setting the `SPT_ROOT` environment variable or passing an MSBuild property:

```text
SptRoot=D:\Games\SPT
```

Build `HERMES.sln` or `Hermes.Build.csproj` in **Release**. The build project creates:

```text
HERMES-0.1.0-rc.2.2.1.zip
```

Automatic deployment is disabled by default for release safety. To copy the DLLs into a test installation during a build, set:

```text
DeployToTestEnvironment=true
TestSptRoot=C:\RealSPT
```

## Troubleshooting

- Confirm both HERMES DLLs are from the same version.
- Remove duplicate HERMES DLLs from other plugin or server-mod folders.
- Confirm the SPT server log reports the HERMES server mod and the BepInEx log reports the HERMES client.
- Enable **Detailed logging** or the diagnostics panel in F12 when collecting a report.
- Include `Player.log`, the BepInEx log, and the SPT server log when reporting a failure.

## License

MIT. See `LICENSE`.


### Ask HERMES item routing

Every item-facing **Ask HERMES** action opens **Items & Market**. Owned stash and equipped items resolve the exact item instance when possible; trader, Flea, craft, and Hideout previews resolve the selected template and show the full-condition base-item estimate. Hideout requirement and production items no longer redirect to Crafts.


### Carried food and drink

When **Require food and water** is enabled, HERMES examines every item carried through equipped rigs, pockets, backpacks, and the secure container. A provision counts only when its template provides a positive Hydration or Energy effect and its remaining consumable resource is usable. Template-name fallbacks cover common vanilla provisions while data-driven effect parsing supports custom food and drink.


### F12 dropdown settings

Settings that choose a workspace, filter, view, or sorting mode now use validated dropdowns instead of free-form text fields. Existing normalization remains in place for compatible values from older configuration files.

### Quest-key knowledge

HERMES embeds a versioned quest-key association catalog derived from the public TarkovForge key directory. The catalog supplies quest-to-key, map, access-purpose, and acquisition guidance without making web requests while SPT is running. HERMES resolves each entry against the installed SPT locale and template database, so keys or quests that do not exist in SPT 4.0.13 are ignored. Pre-raid warnings are emitted only when EFT provides a recognized selected map and the requirement map matches it.

Runtime catalog status is available from `/hermes/quest-keys/status`. The source data is embedded into `Hermes.Server.dll` from `Server/Hermes.Server/Data/quest_key_knowledge.json`.

When a cataloged key is selected in **Items & Market**, the detail panel displays a **QUEST KEY** card for each associated quest. Each card includes the quest name, map, what the key opens, acquisition guidance when available, and the active profile's current quest status.
