# HERMES 0.1.0-alpha10.2.6

## Alpha10.2.6 — Available Crafts ingredient fix

The **Available Crafts** filter now excludes recipes with missing ingredients.

- **Available Crafts** requires an enabled station, sufficient station level, completed quest/unlock requirements, and all required ingredients currently owned.
- **Ready** requires everything above and also requires the station to be free with no matching production already active.

HERMES is a read-only SPT 4.0.13 personal operations assistant.

## Available Crafts filter

The Crafts tab now has a dedicated **Available Crafts** button in addition to **All**, **Ready**, **Profit**, and **Overnight**.

- **Available Crafts** shows recipes whose station level is met, whose quest/unlock requirements are complete, and whose required ingredients are currently owned. A busy station does not hide the recipe.
- **Ready** remains stricter and shows only available recipes that can be started immediately because the station is free and no matching production is active.
- Event-only stations are still filtered before either list is built, so Christmas Tree crafts remain hidden while the Christmas event is inactive.

The filter toolbar is split onto its own row so the new button does not crowd the recipe search and Refresh controls.

## Hideout station availability

HERMES now hides recipes when their station is not currently available to the active profile/server state. This applies to:

- Crafts tab recipe lists
- Craft detail requests
- Item **Produced By** entries
- Item **Used As an Ingredient** entries
- Hideout area summaries and details

The Christmas Tree station (`ChristmasIllumination`) is shown only while SPT reports the Christmas event active. HERMES also honors server mods that enable the Christmas hideout through the global event list. Recipes remain hidden when the event is inactive even though their static database definitions are still loaded.

Standard stations remain visible at level 0 so HERMES can still show future crafts and station requirements; the station only needs to exist in the player profile and be enabled in the hideout database.

## Quest-locked craft detection

HERMES now builds a reverse index from quest completion rewards to hideout production recipes. When a locked recipe is unlocked by a quest, the item usage and craft panels show the exact localized quest name:

```text
Locked by quest: "<quest name>"
```

The generic `Locked by progression` label is now reserved for locked recipes that have no identifiable quest unlock in the loaded database.

Completed quest unlocks are also accepted even if the profile's unlocked-recipe list has not refreshed yet.

## Unified market-price order

Every flea-price lookup now uses this exact fallback chain:

1. Active local cash flea offer
2. Converted active flea barter offer
3. SPT dynamic flea-market price
4. Handbook fallback only when no market price exists

This order is now used by:

- Main item flea analysis
- Trader barter requirement estimates
- Flea barter conversion
- Weapon attachment and armor-insert decomposition
- Exact stash flea references
- Stash sale comparisons
- Hideout material estimates
- Detailed craft acquisition and opportunity-value calculations

Explicit handbook statistics, such as the separate stash handbook-reference total and fast craft-list reference estimates, remain handbook-only by design. They are not flea-price lookups.

## Source and availability behavior

- Active cash offers are preferred over converted barter offers.
- Converted barter requirements recursively use the same four-step chain.
- SPT dynamic and handbook values are marked as reference estimates, not active purchase sources.
- A fallback reference is not used as a reliable flea-listing recommendation.
- Trader and craft breakdowns display the actual source used for each requirement.
- Installed components use market values before handbook fallback when calculating base-item-equivalent flea prices.

## Alpha10.2 stash intelligence

- Exact-instance flea listing estimates
- Trader-versus-flea sale destination
- Quest and hideout reservations
- Safe-to-sell and keep quantities
- Duplicate review
- Damaged and depleted item reporting
- Complete-stash trader, flea, and best-destination estimates

## Reliability

- Normal HERMES requests use a 12-second timeout.
- Full stash analysis uses a 30-second timeout.
- Shared market values use Alpha9 cache generation and refresh protection.
- Press **Refresh current data** after a trader restock or major flea change.

## Build and deploy

Open `HERMES.sln` in Visual Studio and choose:

```text
Build -> Build Solution
```

The build project deploys:

```text
C:\RealSPT\SPT\user\mods\HERMES\Hermes.Server.dll
C:\RealSPT\BepInEx\plugins\HERMES\Hermes.Client.dll
```

It also creates:

```text
HERMES-0.1.0-alpha10.2.6.zip
```

Change `SptRoot` in the client project if the development installation moves.
