# HERMES 0.1.0 release checklist

Build and test against a clean SPT 4.0.13 installation before changing `Version.props` from `0.1.0-rc.2.2.1` to `0.1.0`.

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
- [ ] Click items from every Stash view and confirm Items & Market opens with the exact clicked copy already selected.

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

### Pre-raid and Hideout RC.2.1 checks

- [ ] Select two different maps and confirm readiness refreshes after each stable selection.
- [ ] Confirm quest warnings appear only for the confidently selected objective map.
- [ ] Confirm no surgery kit is a critical finding.
- [ ] Confirm IFAK, AFAK, Salewa, Car first aid kit, and Grizzly satisfy applicable bleed coverage.
- [ ] Enable **Require food and water** and verify missing hydration and energy provisions are reported.
- [ ] Put a water provision and an energy provision inside a rig, pockets, backpack, and secure container; confirm each carried location is recognized after selecting a map.
- [ ] Confirm an exhausted provision with zero remaining resource does not satisfy the requirement.
- [ ] Confirm at least one custom consumable with positive Hydration or Energy template effects is recognized.
- [ ] Use **Ask HERMES** from the Hideout and confirm Items & Market opens with the selected item already looked up.
- [ ] With Gear and Prestige selected, confirm inactive HERMES stays behind Prestige's right edge; select HERMES and confirm it comes forward.

## RC.2.2 additions

- [ ] Confirm all text-choice settings render as dropdowns in F12.
- [ ] Confirm Ask HERMES from stash, equipment, trader, Flea, Crafts, and Hideout always opens Items & Market with the selected item.
- [ ] Request `/hermes/quest-keys/status` and confirm the embedded catalog reports loaded with no error.
- [ ] Open an active quest that requires a key and confirm Raid Planner shows the key only on the correct map.
- [ ] Confirm completed key-access objectives no longer produce a missing-key requirement.

## Items & Market quest-key knowledge

- Search for a cataloged quest key in Items & Market.
- Confirm at least one `QUEST KEY` card appears with the quest name and map.
- Confirm active, completed, and future/locked quest statuses match the active PMC profile.
- Confirm a normal non-key item does not receive an unrelated quest-key card.
