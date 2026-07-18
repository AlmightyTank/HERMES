# HERMES

HERMES is a read-only in-game assistant for SPT 4.0.13. It adds a native **HERMES** tab to the Character and in-raid inventory screens and analyzes the active PMC profile without changing inventory, quests, traders, crafts, or hideout state.

This source is prepared as **0.1.0-rc.1**. The final `0.1.0` tag should be created only after the runtime checklist in `RELEASE_CHECKLIST.md` passes.

## Main features

- Local conversational Assistant with selected-item context and prepared alerts
- Trader purchase and sale comparison using the active profile
- Local Flea Market pricing, listing-fee estimates, and trader-versus-Flea recommendations
- Hideout upgrade requirements and active production status
- Craft readiness, acquisition cost, profit, and availability filtering
- Stash reservation, cleanup, duplicate, condition, and sale-destination analysis
- Loadout readiness, ammunition, armor, medical, insurance, and value analysis
- Raid Planner and map-specific pre-raid readiness
- **Ask HERMES** item context action
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

Normal tab changes rebuild the visible client presentation and read prepared server summaries. The top **Refresh** button performs the stronger source recheck.

## Building from source

The projects expect the SPT root at `C:\RealSPT` by default. Override it without editing project files by setting the `SPT_ROOT` environment variable or passing an MSBuild property:

```text
SptRoot=D:\Games\SPT
```

Build `HERMES.sln` or `Hermes.Build.csproj` in **Release**. The build project creates:

```text
HERMES-0.1.0-rc.1.zip
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
