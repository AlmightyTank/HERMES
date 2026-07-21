# Changelog

## 1.0.3

- Replaced the client's periodic `/hermes/assistant/alerts` HTTP polling with a dedicated HERMES WebSocket connection: the server now pushes a notification-update event with the current Assistant alert feed only when something actually changed, instead of the client asking on a fixed timer.
- Hideout, Crafts, Stash, and Loadout data is no longer refreshed in the background at all. Previously, any server-side change (including unrelated background activity like flea market price ticks) triggered the client to silently re-run the full workspace analysis and re-fetch all four domains even while HERMES was closed — this was the main source of the reported in-game slowdown. That data now refreshes only when you open HERMES or switch to a workspace tab.
- The server-side change check now runs once per interval per connected session, not once per client poll, and the (still comparatively expensive) Assistant alert recompute it can trigger now happens entirely server-side instead of round-tripping through the client.
- Added automatic WebSocket reconnect with backoff and a resync handshake, so the client catches up immediately after a reconnect (for example, across a raid) instead of waiting for the next scheduled poll.
- Live background refresh (F12 -> General -> Live background refresh) now controls only whether this WebSocket connection (and therefore background Assistant alerts) is opened at all; disabling it skips the connection and falls back to alerts refreshing only when Assistant is opened or Check is pressed. Workspace tab data is unaffected either way.
- Removed the now-unused live-refresh and Assistant alert check-interval settings, since their timers no longer exist.
- Fixed a second, unrelated slowdown: the Assistant native-alert raid check re-scanned every loaded assembly with reflection (`AppDomain.CurrentDomain.GetAssemblies()` plus type/member lookups) on every single frame, for the entire play session, any time the Assistant tab wasn't the one currently open. It now reuses the same cached raid-state check the workspace coordinator already uses, resolved once instead of every frame.
- The client now reports raid start/end over the same WebSocket connection, and the server pauses a session's background Assistant alert recompute entirely while a raid is active, catching up immediately the moment it ends. This has no visible effect with default settings, since alerts aren't shown during a raid by default anyway (**Show alerts during raid** is off); it only matters if that setting is enabled, in which case alerts stop updating mid-raid and refresh right after instead.
- Widened the server's background Assistant-alert check from 15s to 60s: it re-serializes and re-hashes the whole active profile on every tick, so a shorter interval meant more sustained background CPU cost on the same machine as the game for comparatively little freshness benefit.
- Removed a redundant HTTP round-trip: switching HERMES tabs (or pressing Refresh/Check) always called `/hermes/assistant/prepare/...` and then separately called `/hermes/assistant/alerts` for the feed that prepare call had just built. `/hermes/assistant/prepare/...` now returns the prepared alerts directly, so a manual refresh no longer needs the second request.
- Fixed the actual biggest cost: every tab switch was going through the same code path as pressing the top **Refresh** button — forcing `/hermes/recheck` (which bypasses the static-database and market-fingerprint throttles and forces them to rescan immediately), `/hermes/changes/...`, the tab's own summary, and `/hermes/assistant/prepare/...`, four requests deep, on every single click. A cheap, already-written, debounced "just read what the server already has prepared" path existed but was never wired up. Tab switching now uses that path — one lightweight request for the selected tab's data, no forced rescan, and rapid re-clicks on the same tab within 1.5s are now collapsed instead of re-fetched.
- Two moments still deliberately force a real recheck, the same as the explicit Refresh button: opening the Assistant tab (that's where actionable alerts live), and coming back to HERMES from elsewhere in EFT after being away. Switching between the other tabs while already inside HERMES stays on the cheap read-only path above.
- Fixed a visual seam glitch where the HERMES tab header didn't overlap cleanly with its right-hand neighbor (normally Achievements), unlike every other adjacent pair of native tabs. The correction that keeps HERMES's draw order fixed relative to that neighbor was being skipped entirely whenever EFT's tab strip uses a Unity layout group, relying only on a one-time placement that could go stale; it now runs continuously like it does for the other tab-strip layout mode.
- Updated client, server, and package version reporting to **1.0.3**.

## 1.0.2

- Added **Interface -> Font size percent** to the F12 Configuration Manager so the native HERMES workspace can be scaled from 80% to 130%.
- Added scalable button sizing with default sizes and maximum growth caps so larger font settings keep button labels readable without letting controls over-expand.
- Moved the native HERMES inventory tab to the slot immediately after Tasks, which better matches the mod's workflow.
- Kept tab placement collision-aware by opening one tab-width of space and preserving the relative order of later tabs, improving compatibility with mods such as WeekendDrops that add tabs after Prestige.
- Rechecked HERMES tab placement during the initial inventory-tab settle window so late-created external tabs are handled more reliably.
- Updated client, server, package, and documentation version references for **1.0.2**.

## 1.0.0

First full public release.

- Promoted the client plugin and shared package version to **1.0.0**.
- Added live background refresh so HERMES checks server revisions on a shared interval, refreshes only changed workspace summaries, and keeps Assistant alerts current even when the HERMES workspace is not selected.
- Added F12 controls for live background refresh and its interval.
- Added active-profile saving while HERMES is open, including a server `/hermes/profile/save` route and F12 controls for the save interval.
- Assistant alert polling now follows the live refresh cadence, retries quickly while the server feed is warming up or stale, and asks live sync to rematerialize the prepared feed automatically.
- Native HERMES notifications can now be right-clicked to dismiss without opening HERMES, and dismissals are remembered by the Assistant notice list.
- Opening an Assistant notice now consistently routes to the Assistant workspace.
- Suggested Assistant prompt buttons now submit immediately and stay disabled while an Assistant request is already loading.
- Loadout armor warnings now specifically require body armor or an armored rig and report missing torso armor as a critical readiness issue.
- Reworked the README into player-facing 1.0.0 documentation with clearer installation, source-build, configuration, compatibility, troubleshooting, and feature sections.
- Added a standalone mod-page draft for release distribution text.

## 0.1.0-rc.2.4.2

- Item search now values the selected owned copy as one assembled item: condition-adjusted root value plus every priced child item.
- Weapons, armor, containers, and other parent items now show child-item value rolled into the visible total.
- Preview and search selections now auto-use the matching owned copy so trader and flea sale estimates include installed child parts immediately.
- The selected search result and item overview update immediately when switching between the base item and a specific owned copy.
- Added a separate child-value metric and relabeled trader reference pricing as the base reference to keep the two valuation bases clear.

## 0.1.0-rc.2.4.1

- Added smart Items & Market section defaults so low-value or empty detail groups stay collapsed initially.
- Owned Copy Pricing is now collapsible and stays closed when the profile owns no matching copy.
- Traders stays closed when there is no supported sale or currently available purchase offer.
- Flea Market stays closed when no usable price, comparable offer, or net-sale estimate exists.
- Quest Requirements and Quest Key Knowledge stay closed when every known use is completed or no use exists.
- Hideout & Craft Uses stays closed when there is no remaining upgrade requirement and no recipe association.
- Compact headers still show the useful conclusion, and every section can still be expanded for completed or unavailable details.

## 0.1.0-rc.2.4

Final whole-mod polish and performance pass before the 0.1.0 release decision.

- Added one shared static-data snapshot for Hideout definitions, quests, locales, and trader names instead of repeatedly serializing and parsing immutable SPT tables.
- Pre-indexed trader, Hideout, quest, and quest-key references so item lookups avoid repeated full-database string scans.
- Added one-second reuse for immediately repeated read-only client requests while clearing that reuse cache before manual rechecks, workspace invalidations, or Assistant preparation.
- Removed the unused server-held `/hermes/watch/` route and wake-signal machinery.
- Reduced native screen discovery and workspace synchronization frequency once a valid InventoryScreen host is active.
- Bounded per-item collapsible-section state and made the shared row limit configurable with a release-safe default of 80.
- Collapsed detailed Items & Market sections, Flea details, and barter calculations by default while preserving the useful summary information.
- Increased prepared-profile sharing to two seconds so parallel workspace preparation reuses one parsed profile snapshot.
- Corrected provision classification so an MRE ration pack or other buff-bearing food is classified as **Provisions**, never as **Medical**.
- Updated F12 labels to distinguish reading prepared data from a strong source Refresh.

## 0.1.0-rc.2.3

- Added collapsible Items & Market sections for Traders, Flea Market, Quest Requirements, Quest Key Knowledge, and Hideout & Craft Uses.
- Kept the most useful summary visible while each section is collapsed and retained the full breakdown when expanded.
- Remembered section expansion independently for each selected item.

## 0.1.0-rc.2.2.1

- Added quest-key knowledge to the selected key inside **Items & Market**.
- Key matching now uses the installed item template id when available and normalized key-name aliases otherwise.
- The item detail response now lists every associated quest, exact map, access purpose, acquisition guidance, and current quest status.
- Active and completed quest-key associations are marked separately from future or locked quests.
- Added quest-key counts to the Items & Market usage summary and client-content fingerprint.

## 0.1.0-rc.2.2

- Changed every item-facing **Ask HERMES** action to open **Items & Market** and immediately look up the selected item, including native Hideout requirement and production items.
- Kept recipe navigation inside the Crafts workspace separate from the global Ask HERMES item action.
- Converted every text-choice F12 setting into a real dropdown with validated values and clearer player-facing labels.
- Added an embedded TarkovForge-derived quest-key knowledge catalog covering 63 key associations and 77 quest-name associations.
- Quest-key knowledge now enriches Loadout, Raid Planner, pre-raid readiness, and Assistant responses with the required key, exact map, access purpose, and acquisition guidance.
- Local SPT templates, active quest progress, completed objective state, and selected-map matching remain authoritative; unmatched catalog entries are ignored safely.
- Added `/hermes/quest-keys/status` for runtime catalog validation.

## 0.1.0-rc.2.1.2

- Fixed HERMES Stash rows opening Items & Market without resolving the clicked item.
- The inventory-selection endpoint now accepts both native EFT profile item ids and HERMES session-scoped instance keys.
- Clicking a Stash recommendation, cleanup row, valuable item, duplicate, or damaged item now selects the exact carried/stored copy and loads its trader, Flea, and usage details.
- Stash navigation carries the item name as a fallback so a profile change between summary creation and click still performs the visible lookup.

## 0.1.0-rc.2.1.1

- Fixed `TemplateInfo.Missing` passing 27 positional arguments to the 26-parameter `TemplateInfo` record constructor.
- Changed the missing-template factory to named constructor arguments so future template fields cannot silently shift the fallback values.

## 0.1.0-rc.2.1

- Fixed native Hideout item context menus that report `EItemViewType.Inventory` and incorrectly opened Items & Market.
- Native Hideout screen and context-owner detection now takes priority and routes the selected template to Crafts.
- Explicitly invalidates the prepared Loadout response after a stable map selection so readiness rereads recently moved equipment, medicine, food, and drink.
- Manual readiness refresh also clears the short-lived prepared profile snapshot before rebuilding Loadout.
- Reads positive Hydration and Energy effects from item-template data instead of relying on a narrow template-name list.
- Counts usable carried provisions inside rigs, pockets, backpacks, and the secure container, including custom consumables.
- Ignores an exhausted food or drink item when its profile resource is zero.
- Added a debug classification line for Hideout runtime verification.

## 0.1.0-rc.1

Release-candidate preparation for the first public HERMES release.

- Unified client requests through one broker with shared in-flight routes and timeout diagnostics
- Added server-prepared profile snapshots and materialized Hideout, Crafts, Stash, and Loadout summaries
- Added server-prepared Assistant alerts and collapsed the duplicate startup snapshot transfer
- Added lightweight craft-output valuation and workspace-open request coalescing
- Added server content revisions and lighter native client rebuilding
- Corrected native HERMES tab placement, exclusive selection, a full first-load settle window, and tab-transition client refresh
- Added strict pre-raid map matching and the explicit Comfort assembly reference
- Polished trader sale-estimate layout and native workspace row limits
- Centralized release versioning and removed stale Alpha version labels from runtime output
- Added release documentation, MIT license, package validation, and safer build/deployment defaults

## 0.1.0

Reserved for the build that passes `RELEASE_CHECKLIST.md` without a release-blocking issue.
