# HERMES

HERMES is a read-only in-game assistant for SPT 4.0.13. It adds a native **HERMES** tab to the Character and in-raid inventory screens and analyzes the active PMC profile without changing inventory, quests, traders, crafts, or hideout state.

This source is prepared as **0.1.0-rc.2.4.1**. The final `0.1.0` tag should be created only after the runtime checklist in `RELEASE_CHECKLIST.md` passes.

## Main features

- Local conversational Assistant with selected-item context and prepared alerts
- Categorized in-game question guide with editable examples for next steps, raids, loadouts, items, stash, crafts, hideout, and contextual follow-ups
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


### Final polish and performance pass

RC.2.4 keeps the current feature set and removes avoidable work before the final release decision. Immutable SPT quest, Hideout, locale, and trader data is materialized once; item-usage and quest-key associations are indexed; immediate duplicate read requests are reused; native Unity discovery and synchronization are throttled; and large lists use a release-safe row cap. Manual Refresh still clears the short client reuse window before reading changed server data.

Food and drink classification now takes precedence over generic buff detection. An MRE ration pack and other buff-bearing provisions count toward carried energy or hydration but are never counted as medical items or bleed/surgery coverage.

Items & Market now uses smart section defaults. Sections with no owned copy, no current trader or Flea value, no remaining quest/key requirement, and no current Hideout or craft use stay collapsed initially while their compact header still explains the result. Players can expand any section to inspect completed or unavailable details.

## Requirements

- SPT **4.0.13**
- .NET **9 SDK** for source builds
- Visual Studio 2022 or another MSBuild-compatible IDE
- A valid SPT installation containing the managed EFT and BepInEx assemblies

## Installation

The build stages the install payload under `dist` and creates the release ZIP from that folder. Extract the release ZIP into the SPT root. The final layout is:

```text
BepInEx/plugins/HERMES/Hermes.Client.dll
BepInEx/plugins/HERMES/ask_hermes.png
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
HERMES-0.1.0-rc.2.4.1.zip
```

By default, building `Hermes.Build.csproj` also installs the generated server DLL, client DLL, and client assets into the test SPT root for local validation. The default test root is `C:\RealSPT`; override it with:

```text
TestSptRoot=C:\RealSPT
```

To package without installing into the test SPT root, set:

```text
DeployToTestEnvironment=false
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
