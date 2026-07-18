# Changelog

## 0.1.0-rc.2.2.1

- Added quest-key knowledge to the selected key inside **Items & Market**.
- Key matching now uses the installed item template id when available and normalized key-name aliases otherwise.
- The item detail response now lists every associated quest, exact map, access purpose, acquisition guidance, and current quest status.
- Active and completed quest-key associations are marked separately from future or locked quests.
- Added quest-key counts to the Items & Market usage summary and client-content fingerprint.

## 0.1.0-rc.2.2.1

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
