# HERMES 0.1.0-alpha12.0

## Alpha12.0 — Local conversational Assistant

Alpha12.0 adds the first dedicated HERMES Assistant tab. It is deterministic, local, and fully read-only. It does not require an API key and does not transmit profile, inventory, quest, market, or loadout data to an external service.

### Assistant interface

- Dedicated Assistant main tab.
- Single-line prompt composer with Enter-to-submit support.
- Local conversation history with configurable retention.
- Suggested prompt buttons.
- Copy button on every HERMES answer.
- Clear-conversation control.
- Visible selected-item context.
- Clickable navigation into Item Search, Hideout, Crafts, Stash, Loadout, and the Raid Planner view.
- Existing global Refresh can rerun the last Assistant question against a fresh server snapshot.

### Deterministic Alpha12.0 intents

The local intent engine can answer questions about:

- General HERMES capabilities.
- Current loadout readiness and configured warning thresholds.
- Weapon magazines, loaded ammunition, and compatible spare rounds.
- Medical treatment coverage and healing resource.
- Insurance state and at-risk replacement value.
- Best current Raid Planner map based on active quests, incomplete objectives, and missing pre-raid requirements.
- Stash safe-to-sell quantities, cleanup candidates, recoverable cells, and top sale recommendations.
- Ready, profitable, and overnight crafts using existing Craft settings.
- Hideout upgrade status, missing-material pressure, productions, generator, and fuel state.
- Selected-item trader value, flea estimate, replacement cost, quest use, hideout use, and crafting use.
- Item-name resolution for simple questions such as `What is Salewa worth?`.

All factual values come from existing HERMES services. The Assistant does not maintain a second market, quest, stash, craft, or loadout calculation path.

### New BepInEx/F12 settings

Under **Assistant**:

- Enable Assistant tab.
- Show suggested prompts.
- Include selected item context.
- Maximum conversation messages, from 10 to 200.

The global **Default opening tab** setting now accepts `Assistant`.

### Current Alpha12 boundary

Alpha12.0 recognizes supported intents per question. Persistent follow-up subject resolution is planned for a later Alpha12 build. For example, a selected item can be referenced as `this item`, but a completely new unnamed subject is not inferred from several messages earlier.

HERMES remains read-only. It does not buy, sell, list, move, equip, insure, craft, accept, or complete anything.

## Alpha11.9.1 — Unity text-enum compile correction

- Added an explicit client reference to `UnityEngine.TextRenderingModule.dll`.
- Added build-time validation for the Text Rendering module under the configured SPT managed directory.
- Fixes missing `TextAnchor` and `FontStyle` symbols used by the Loadout readiness progress bar.
- No runtime behavior, UI layout, cache logic, or analysis calculations changed.

## Alpha11.9 — Loadout and Raid Planner polish

Alpha11.9 completes the final Loadout and Raid Planner interface/settings pass before the Alpha11.9 reliability audit. HERMES remains fully read-only.

### Server-evaluated Loadout settings

The following BepInEx/F12 options now travel with every Loadout request and are included in the profile-aware cache signature:

- Minimum compatible spare rounds per equipped firearm.
- Require heavy-bleed treatment.
- Require light-bleed treatment.
- Require fracture treatment.
- Require pain treatment.
- Require a hydration provision.
- Require an energy provision.

Compatible spare rounds include ammunition loaded in compatible spare magazines plus loose compatible ammunition. Disabled treatment or sustainment requirements remain visible as optional coverage but do not reduce readiness.

### Loadout interface polish

- Configurable default Loadout sub-tab.
- Optional readiness score bar.
- Critical/advisory warning toggles.
- Weapon cards with durability, loaded ammunition, compatible spare magazines, and total spare-round metrics.
- Armor cards with condition and insert-fill bars.
- Medical readiness checklist that distinguishes required, optional, covered, and missing treatment.
- Optional empty-section suppression.
- Optional protected-slot value display.
- Optional insurance-cost estimate display.
- Interactive uninsured-only carried-item filter.

### Raid Planner polish

- Map and quest-name search.
- All, Prepared, Missing gear, and Incomplete status filters.
- Configurable sorting: Best prepared, Most active quests, Most incomplete objectives, Fewest missing requirements, or Alphabetical.
- Collapsible map cards.
- Grouped checklist sections for Equip before raid, Bring from stash, Route keys, and Acquire during raid.
- Configurable visibility for inferred route keys, acquire-in-raid items, handover objectives, and FIR handover objectives.
- Optional Medical, Sustainment, Weapons, Ammunition, and Insurance warning context.
- Localized objective text remains the primary objective label.
- Saving the Mole continues to show the TerraGroup science office key as an acquire-during-raid requirement.

### Fixed safety and data rules

- Market-price fallback order remains unchanged.
- Quest requirements and inventory facts are never altered by client filters.
- Hidden rows still participate in server totals and readiness calculations.
- Acquire-in-raid requirements do not count as missing pre-raid gear.
- HERMES does not buy, sell, move, equip, insure, craft, or complete anything.

## Alpha11.7.1 — Stash hidden-row counter compile fix

- Moved all six `HermesUi.LimitRows(...)` calls out of `foreach` collection expressions.
- Keeps `hiddenDestinations`, `hiddenValuable`, `hiddenRows`, `hiddenCleanup`, `hiddenDuplicates`, and `hiddenDamaged` in scope for their hidden-row notices.
- No Stash analysis, filtering, sorting, reservation, valuation, or cache behavior changed.

Alpha11.7 is the Stash Intelligence polish and configuration pass for SPT 4.0.13. It builds on Alpha11.6 and keeps HERMES fully read-only.

## Stash filtering and sorting

The Stash tab now provides one consistent filter toolbar for recommendation and cleanup rows:

- Free-text search across item names, short names, exact instance labels, categories, recommendations, destinations, and reservation reasons.
- Category filter: Currency, Weapons, Ammunition, Magazines, Armor, Medical, Keys, Provisions, Containers, and Other when present.
- Best-destination filter.
- Found-in-raid filter: all, FIR only, or exclude FIR.
- Sorting by recommendation, name, sell value, sellable quantity, value per cell, occupied cells, condition, destination, or reserved quantity.
- Clear-filter control and clipboard summary export.

The Overview, Safe to Sell, Cleanup, Keep, Review, Duplicates, and Damaged views remain available.

## Exact-item recommendation cards

Each recommendation row now shows:

- Owned quantity, protected keep quantity, and exact sellable quantity.
- Category and found-in-raid state.
- Partial-stack, filled-container, installed-assembly, and protected-currency badges.
- Best destination plus separate trader and flea alternatives.
- Sellable value and value per occupied cell.
- Active quest, future quest, next hideout, and future hideout reservation quantities.
- Exact quest/hideout reasons when enabled.
- An **Ask HERMES** button for the exact stash instance.

Filled containers and assembled items remain manual-review recommendations. HERMES does not move or sell inventory.

## Pinned metrics

The top of every Stash view now keeps the main metrics visible:

- Sellable quantity and best estimated value.
- Recoverable cells and removable exact instances.
- Reserved quantity.
- Manual-review and condition-warning counts.

## New BepInEx/F12 settings

### Stash

- Default stash view.
- Default stash sorting.

### Stash Reservations

- Include active quest reservations.
- Include future quest reservations.
- Include next hideout upgrade.
- Include future hideout upgrades.
- Prefer found-in-raid copies when several copies can satisfy a reservation.

FIR-required objectives always protect FIR copies even when the general preference is disabled.

### Stash Recommendations

- Duplicate baseline reserve.

### Stash Condition

- Weapon durability warning percentage.
- Armor/generic durability warning percentage.
- Low resource warning percentage.
- Key uses warning threshold.

### Stash Cleanup

- Minimum cleanup sale value.
- Minimum value per recovered cell.

### Stash Display

- Maximum recommendation rows returned by the server.
- Show or hide protected currencies in item lists.
- Show or hide the unsupported-item count.
- Show or hide detailed reservation reasons.

## Server analysis behavior

Stash settings are sent with each request and affect the server analysis itself. The 10-second profile-aware cache now separates entries by the complete Stash settings signature, so two different reservation or cleanup configurations cannot reuse each other's result.

Totals still include all supported exact items. The server row limit affects only returned recommendation details.

## Fixed policies

The following safety rules remain non-configurable:

- Active quest items remain protected when active quest reservations are enabled.
- Quest-only and handbook-less items are excluded from sale recommendations.
- Filled containers and installed assemblies remain manual review.
- Flea recommendations require a reliable market estimate.
- The shared price order remains active cash flea offer, converted flea barter, SPT dynamic flea price, then handbook fallback.
- HERMES never sells, moves, deletes, equips, or lists an item.

## Build and deployment

Open `HERMES.sln` and build the solution. The build project uses the configured `C:\RealSPT` installation and automatically deploys the client and server modules when the expected SPT 4.0.13 references are present.

A local build against the user's SPT installation is still required.