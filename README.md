# HERMES 0.1.0-alpha10.2.1

HERMES is a read-only SPT 4.0.13 personal operations assistant.

Alpha10.2.1 fixes trader-barter requirement valuation so missing live offers use SPT's dynamic flea-market price before handbook fallback. It retains the Alpha10.2 stash intelligence features.

## Alpha10.2.1 fix

- Trader barter requirements now use this valuation order:
  1. Active local flea cash or converted barter offer
  2. SPT dynamic flea price from `templates/prices`
  3. Handbook value only when neither market source is available
- The per-requirement market calculation identifies dynamic values as `SPT dynamic flea price`.
- The handbook fallback note appears only when both live and dynamic market pricing are unavailable.
- The same corrected market valuation flows into converted flea barters, hideout costs, craft costs, and stash sale analysis.

## Alpha10.2 additions

- Exact-instance flea listing estimates for stash items
  - Current local flea comparisons
  - Converted barter offers
  - Stack and resource condition adjustment
  - Installed attachment and armor-insert market values
  - Estimated listing fee and net proceeds
- Conservative best sale destination
  - Flea is selected automatically only with at least three comparable offers
  - Otherwise HERMES retains the best supported trader or marks the item for review
- Complete-stash estimates
  - Best trader liquidation
  - Estimated flea net
  - Best reliable destination selected per item
- Sellable-quantity estimates
  - Trader alternative
  - Flea net alternative
  - Best-destination value after quest/hideout reservations
- Duplicate review
  - Exact-template groups
  - Owned quantity and instance count
  - Explicit quest/hideout reserve
  - One-instance advisory baseline when no reserve exists
  - Potential excess quantity and sale value
- Damaged/depleted report
  - Weapons below 70% durability
  - Armor and generic repairables below 50%
  - Medical, consumable, fuel/resource, and repair-kit resources below 20%
  - Keys with one use remaining
- New Stash subviews
  - Overview
  - Safe to Sell
  - Keep
  - Review
  - Duplicates
  - Damaged

## Reliability

- Normal HERMES requests retain the 12-second timeout.
- The full stash-analysis request uses a 30-second timeout because the first scan may warm many unique market-price entries.
- Market and stash snapshots continue to use Alpha9 cache generation and stale-response protections.

## Important behavior

HERMES remains read-only. It does not sell items, create flea listings, move inventory, repair equipment, or alter the active profile.

Flea estimates are advisory. Listing fees are scaled from SPT's fee calculation for the base item, and built or filled items remain manual-review items even when a market value can be estimated.

Duplicate reporting does not create a new hard reservation. Quest and hideout reservations remain authoritative; the one-instance baseline is shown only inside the advisory duplicate view.

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
HERMES-0.1.0-alpha10.2.1.zip
```

Change `SptRoot` in the client project if the development installation moves.
