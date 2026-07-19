# HERMES

HERMES stands for **Hideout, Economy, Resource, Market, and Equipment System**.

It is a read-only in-game assistant for SPT **4.0.13**. HERMES adds a native **HERMES** tab to the Character screen and in-raid inventory screen, then analyzes the active PMC profile using local SPT data.

HERMES does not use an external AI service, does not send your profile anywhere, and does not perform game actions. It reads local profile, trader, item, quest, Hideout, craft, and Flea data, then presents the results inside the game.

## What HERMES Does

- Adds a native **HERMES** workspace inside EFT's Character and inventory UI
- Provides a local, rule-based Assistant with selected-item context
- Adds **Ask HERMES** item actions for stash, equipment, traders, Flea previews, crafts, and Hideout items
- Compares trader purchase and sale options for the active profile
- Estimates Flea Market values, listing fees, and trader-versus-Flea sale choices
- Reviews Hideout upgrade requirements and active production status
- Checks craft readiness, missing ingredients, acquisition costs, output value, profit, and availability
- Finds stash cleanup opportunities, duplicates, damaged gear, valuables, reserved items, and sale destinations
- Reviews loadout readiness, ammo, armor, medical coverage, insurance, provisions, and carried value
- Includes a Raid Planner with map-specific pre-raid readiness
- Connects active quests to required access keys and maps through embedded quest-key knowledge
- Supports a configurable pre-raid food-and-water requirement, including custom consumables when item data exposes hydration or energy effects

## Main Workspaces

### Assistant

Ask local, profile-aware questions about next steps, raids, items, stash, crafts, Hideout, and loadout. The Assistant is rule-based and runs locally.

### Items & Market

Look up an item and see owned-copy value, installed child-part value, trader prices, Flea estimates, quest usage, quest-key knowledge, Hideout needs, and craft associations. HERMES tries to resolve the exact owned item instance when opened from stash or equipment.

### Stash

Review cleanup ideas, sale candidates, duplicate items, condition warnings, valuable items, and reserved items you may want to keep.

### Loadout

Check raid readiness, including ammo, armor, medical coverage, insurance, value at risk, food, drink, and practical warnings.

### Crafts & Hideout

See craft readiness, missing ingredients, output value, estimated profit, active production, and Hideout upgrade requirements.

### Raid Planner

Select a map and review readiness, likely quest requirements, relevant quest keys, and warnings for that location.

## Requirements

For players:

- SPT **4.0.13**
- BepInEx, as included with SPT
- A normal SPT installation with both the server and client available

For source builds:

- .NET **9 SDK**
- Visual Studio 2022 or another MSBuild-compatible environment
- A local SPT **4.0.13** installation
- EFT, BepInEx, and SPT managed assemblies available under that SPT install

The client project resolves game references from your SPT folder, including:

- `EscapeFromTarkov_Data/Managed`
- `BepInEx/core`
- `BepInEx/plugins/spt`

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

## Recompiling From Source

Clone the repository, install the .NET 9 SDK, and point the build at a valid SPT 4.0.13 installation.

The build defaults to:

```text
C:\RealSPT
```

You can override that path with the `SPT_ROOT` environment variable:

```powershell
$env:SPT_ROOT = "D:\Games\SPT"
dotnet build .\Hermes.Build.csproj -c Release
```

Or pass the path directly as an MSBuild property:

```powershell
dotnet build .\Hermes.Build.csproj -c Release -p:SptRoot="D:\Games\SPT"
```

The build creates the release package at the repository root. The package name uses the value in `Version.props`; with the current source it is:

```text
HERMES-1.0.0.zip
```

It also stages the install payload under:

```text
dist/
```

By default, `Hermes.Build.csproj` also deploys the built DLLs and assets into the test SPT root so you can launch SPT and validate the mod immediately.

To build the ZIP without deploying into your SPT folder:

```powershell
dotnet build .\Hermes.Build.csproj -c Release -p:SptRoot="D:\Games\SPT" -p:DeployToTestEnvironment=false
```

To use one SPT path for references and another for deployment:

```powershell
dotnet build .\Hermes.Build.csproj -c Release -p:SptRoot="D:\Games\SPT-References" -p:TestSptRoot="D:\Games\SPT-Test"
```

You can also build the solution directly:

```powershell
dotnet build .\HERMES.sln -c Release -p:SptRoot="D:\Games\SPT"
```

Use `Version.props` to change the package/version number before building a release.

## Repository Layout

```text
Client/Hermes.Client/       BepInEx client plugin and native EFT UI
Server/Hermes.Server/       SPT server mod, profile analysis, and API routes
Server/Hermes.Server/Data/  Embedded quest-key knowledge
Hermes.Build.csproj         Packaging and optional test deployment project
Version.props               Shared HERMES version
dist/                       Generated install payload
```

## Configuration

HERMES creates its BepInEx configuration after first launch. Open the in-game Configuration Manager with **F12** to adjust Assistant, market, Hideout, craft, stash, loadout, Raid Planner, notification, and interface settings.

The top **Refresh** button performs a stronger source recheck when gear, stash contents, Hideout state, quest progress, or other profile data has changed and you want HERMES to reread it immediately.

## Important Notes

- HERMES is read-only. It will not buy, sell, move, craft, repair, insure, accept quests, complete quests, or edit your profile.
- HERMES uses the installed SPT database and active profile as the source of truth.
- Flea and trader values are estimates based on available local SPT data and may not match every economy setup or heavily customized mod list.
- Quest-key knowledge is embedded and resolved against installed SPT data. Entries that do not match SPT 4.0.13 are ignored safely.
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

HERMES is designed to be read-only and should avoid profile-writing conflicts with other mods. Mods that heavily alter item templates, trader offers, quests, Hideout data, or Flea pricing may affect the analysis HERMES displays.

## Credits

Created by **AMightyTank**.

Quest-key knowledge is derived from public TarkovForge key-reference data and embedded locally so HERMES does not need web access during play.

## License

MIT. See `LICENSE`.
