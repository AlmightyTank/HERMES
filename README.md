# HERMES 0.1.0-alpha11.9

## Alpha11.9 — Stabilization, diagnostics, and release validation

Alpha11.9 is the final Alpha11 reliability pass before the conversational-assistant work begins. HERMES remains completely read-only.

### Request reliability

- Identical read-only requests that overlap now share one in-flight server request by default.
- Duplicate-request sharing can be disabled under **Reliability** in BepInEx/F12.
- Timeout handling still leaves the underlying SPT request observable so late failures are logged instead of becoming unobserved task exceptions.
- Server response envelopes are validated before deserialization. Explicit SPT error responses, missing data payloads, empty responses, and client/server model mismatches now produce actionable errors instead of silent fallback objects.
- The slow-request warning threshold is configurable from 1–30 seconds.
- Existing stale-response version guards remain active in Item Search, Hideout, Crafts, Stash, and Loadout.

### Client request diagnostics

The client tracks:

- Started, active, completed, and failed requests.
- Timeouts, transport failures, and invalid responses.
- Slow requests.
- Duplicate client requests satisfied by an existing in-flight request.
- Last route, last duration, and last failure.

The footer can show a compact health summary. **Copy diagnostics** places a plain-text report on the clipboard without exporting the PMC profile or inventory contents.

### Cache diagnostics

The cache-status route and footer now report independent statistics for:

- Shared market-price and market-summary caches.
- Profile-aware Stash analysis cache.
- Profile-aware Loadout analysis cache.

Entry counts, hits, misses, writes, and TTL values are visible. Global Refresh still clears all three cache groups.

### New BepInEx/F12 settings

Under **Reliability**:

- Share duplicate in-flight requests.
- Slow request warning seconds.
- Cache status refresh seconds.
- Show diagnostics footer.

### Release-safety audit

- Client/server cache-status model parity was expanded and checked.
- Stash and Loadout cache counters purge expired entries before reporting counts.
- Cache clears return a post-clear status covering all cache groups.
- Long Stash and Loadout analysis requests retain the minimum 30-second client timeout.
- Market fallback order, reservation policy, readiness logic, and every read-only safety restriction remain unchanged.

## Alpha11.8 — Loadout and Raid Planner polish

Alpha11.8 completes the final Loadout and Raid Planner interface/settings pass before the Alpha11.9 reliability audit. HERMES remains fully read-only.

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