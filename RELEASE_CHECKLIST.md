# HERMES 0.1.0 release checklist

Build and test against a clean SPT 4.0.13 installation before changing `Version.props` from `0.1.0-rc.1` to `0.1.0`.

## Build and package

- [ ] Clean the solution, then build `Hermes.Build` in **Release** with zero errors.
- [ ] Review all compiler warnings; no new HERMES warning is accepted without explanation.
- [ ] Confirm the package contains only the two DLLs plus README, LICENSE, and CHANGELOG.
- [ ] Confirm client and server report the exact same version.
- [ ] Confirm a package build does not deploy unless `DeployToTestEnvironment=true` is explicitly set.

## Clean-install startup

- [ ] Remove every older HERMES DLL and configuration file, then install the RC.
- [ ] Confirm SPT.Server starts without a HERMES exception.
- [ ] Confirm BepInEx enables every required HERMES patch.
- [ ] Confirm the first Character-screen load shows Gear as the only active native tab.
- [ ] Confirm HERMES is visible after Prestige in both inactive and selected states.
- [ ] Confirm no duplicate HERMES tab appears after reopening Character three times.

## Workspace behavior

- [ ] Open each workspace and switch repeatedly between HERMES workspaces.
- [ ] Leave HERMES for Gear, Health, Tasks, Achievements, and Prestige, then return.
- [ ] Confirm each transition rebuilds the client presentation without a full source invalidation.
- [ ] Confirm scroll state is restored and no old workspace blocks input.
- [ ] Confirm the top Refresh button performs a source recheck and completes.
- [ ] Confirm Items & Market search, stash-copy selection, and the sale-estimate card display correctly.

## Data and performance

- [ ] Capture one clean startup Player.log and SPT server log.
- [ ] Confirm only one initial Hideout/Crafts/Stash/Loadout preparation batch is started.
- [ ] Confirm Assistant preparation waits for all four valid source summaries.
- [ ] Confirm later unchanged workspace opens return from materialized caches.
- [ ] Confirm no continuous `/hermes/watch/` or full-workspace request loop appears.
- [ ] Record first-load and cached timings for Crafts, Stash, and Loadout.
- [ ] Confirm a timed-out client request can complete server-side without launching duplicate work.

## Profile and lifecycle safety

- [ ] Complete one PMC raid and confirm the post-raid preparation runs once.
- [ ] Open the in-raid inventory and confirm HERMES does not cover native tabs or raid UI.
- [ ] Switch PMC profile or create a test profile and confirm old prepared data is not reused.
- [ ] Change equipment, insurance, health, quest progress, hideout state, and stash contents; verify the correct domains refresh.

## Pre-raid readiness

- [ ] Select each available map and confirm quest warnings appear only for confidently matching objectives.
- [ ] Confirm unknown or unresolved maps show no guessed quest warning.
- [ ] Confirm `The Lab` and `Labs` normalize to the same map.
- [ ] Confirm Insurance Next interception, readiness confirmation, and Back navigation work.

## Context actions and notifications

- [ ] Use Ask HERMES on stash, equipped, trader-preview, Flea-preview, craft, and hideout items.
- [ ] Confirm selected instances include condition and installed equipment when available.
- [ ] Confirm Assistant alert cards are deduplicated and navigation targets open correctly.
- [ ] Confirm notices do not appear during raid unless explicitly enabled.

## Release decision

- [ ] No reproducible crash, profile mix-up, incorrect-map quest warning, tab-selection defect, or request loop remains.
- [ ] Any remaining slow first calculation is documented and completes within the configured long-request budget.
- [ ] Change `Version.props` to `0.1.0`, rebuild Release, and repeat the package-content/version checks.
