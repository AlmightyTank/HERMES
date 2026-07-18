# HERMES 0.1.0 release audit

## Decision

The project is ready to enter **0.1.0-rc.2.2.1 runtime validation**. It is not yet approved for the final `0.1.0` label because the latest performance, initial-tab settling, and tab-transition presentation changes have not been rebuilt and exercised in a clean SPT 4.0.13 runtime.

## Static audit result

- 84 release-source files
- 71 C# files
- 44,746 C# lines
- 29 server routes
- 61 shared client/server response models
- 49 automated static checks passed
- 0 automated static checks failed

The audit checked project XML, source delimiter structure, unfinished markers, stale version labels, shared versioning, request-pipeline ownership, route registration and ordering, client/server model parity, server DI cycles, client assembly references, configuration bindings, first-load tab settling, map-normalization compilation safety, build/deployment defaults, required documentation, and the embedded icon.

## Release blockers corrected

- Replaced scattered Alpha versions with one `Version.props` value.
- Made server status, server metadata, client logs, workspace logs, and diagnostics resolve the assembly informational version.
- Removed generated `bin`, `obj`, and `dist` content from the release source.
- Removed the duplicate TextMeshPro project-reference setup.
- Added explicit validation for every client assembly reference.
- Added the missing README, changelog, MIT license, release checklist, validation summary, and `.gitignore`.
- Changed automatic installation into `C:\RealSPT` from default-on to explicit opt-in.
- Added `SPT_ROOT` and `SptRoot` path overrides.
- Removed the obsolete client watch entry point and disabled legacy continuous Loadout polling.
- Excluded the unused Ragfair UI dump implementation from Release builds.
- Replaced target-typed string construction in readiness normalization with explicit `new string(...)` construction.
- Extended the first-load HERMES inactive-state repair through the full settling window while yielding immediately to a genuine HERMES selection.

## RC.2.1.1 compile correction

- Confirmed `TemplateInfo` has 26 positional constructor parameters.
- Confirmed `TemplateInfo.Missing` now supplies those same 26 values.
- Converted the fallback factory to named arguments so consumable-resource and food/water fields cannot be shifted by a future positional edit.

## RC.2.1.2 stash-selection correction

- Confirmed native EFT context actions continue to resolve raw profile item ids.
- Confirmed HERMES Stash rows can resolve their session-scoped public instance keys back to the exact active-profile item.
- Kept raw profile item ids out of materialized Stash workspace responses.

## Runtime gates still required

1. Clean and build `Hermes.Build` in Release with zero errors.
2. Review compiler warnings and verify the generated package contents.
3. Test a clean install with no old HERMES DLL or configuration.
4. Verify Gear is the only selected tab on the first Character-screen load and HERMES remains after Prestige.
5. Verify all HERMES and native tab transitions refresh only the client presentation.
6. Capture startup and post-raid client/server logs to confirm one preparation batch, complete Assistant preparation, cache hits on later opens, and no request loop.
7. Exercise pre-raid map matching, context actions, notifications, profile switching, and a full PMC raid lifecycle.
8. Change `Version.props` from `0.1.0-rc.2.2.1` to `0.1.0` only after `RELEASE_CHECKLIST.md` passes.

## Environment limitation

This environment does not contain the .NET SDK or the local SPT 4.0.13 managed assemblies. The audit therefore validates source structure and release wiring but cannot replace the local Visual Studio build or in-game test.


## RC.2.1 targeted review

- Confirmed map selection is debounced before resetting and rebuilding the prepared pre-raid snapshot.
- Confirmed unknown maps still fail closed for quest warnings.
- Confirmed surgery-kit absence is critical and the named general medkits satisfy both bleed capabilities.
- Confirmed the food/water toggle is bound exactly once.
- Confirmed stable map selection explicitly invalidates the prepared Loadout response before rebuilding readiness.
- Confirmed carried provision detection walks the complete equipped inventory tree and reads positive Hydration/Energy effects from vanilla or custom templates.
- Confirmed exhausted FoodDrink resources do not satisfy a provision requirement.
- Confirmed Hideout Ask actions navigate to Crafts using exact template/item usage data.
- Confirmed HERMES stays anchored after Prestige while inactive sibling order remains behind Prestige until selection.

## RC.2.2 scope

- Global Ask HERMES item routing is unified on Items & Market.
- All string-based F12 choices use validated dropdown value lists.
- Embedded quest-key knowledge is resolved against the installed SPT database and active profile state.

## RC.2.2.1 correction

Quest-key knowledge is now included in the Items & Market item-usage response and rendered by both the native and legacy detail views. The server matches the selected key by template id or normalized key alias, resolves associated quest names against the installed SPT quest database, and reports profile-aware quest status.
