# HERMES 0.1.0-alpha13.0.10

## Map-selection prefetch for pre-raid readiness

HERMES starts the shared Loadout/readiness refresh when EFT opens the PMC map-selection screen.

- Map selection starts or joins one shared loadout request.
- HERMES, Raid Planner, and pre-raid readiness use the same result.
- Insurance Next does not start a duplicate full refresh when the prepared result is ready.
- Quest warnings are shown only when the selected raid map and objective map are both known and match.

## Crafts: best available trader or Flea profit

The four primary filters remain **Ready**, **Profitable**, **Active**, and **All**. **Available** remains an independent narrowing checkbox.

Exact profitable behavior:

- Before Flea Market unlock, **Profitable** is based only on the best trader sale available to the profile.
- After Flea Market unlock, HERMES compares the best trader sale against the estimated Flea net sale after listing fee.
- If Flea produces the higher net profit, the card recommends **SELL ON FLEA**.
- If a trader produces the higher profit, the card recommends **SELL TO <TRADER>**.
- Items that cannot be sold on Flea continue to use the best trader.
- A craft is profitable only when the best currently usable sale channel exceeds the economic value of its ingredients.

```text
trader profit = best trader sale value - economic input value
flea profit   = estimated Flea net sale after fee - economic input value
best profit   = max(trader profit, usable flea profit)
```

Filter combinations remain conjunctive:

- **Profitable + unchecked**: every craft with positive best trader/Flea profit.
- **Profitable + checked**: profitable crafts currently available to the profile.
- **Ready + checked**: ready crafts that are available.
- **Active + checked**: active, running, or collectable crafts.
- **All + checked**: available, active, or completed crafts.

Trader and Flea sale estimates are cached once per unique craft output during each Crafts request. If Flea is locked, HERMES detects that once and skips Flea pricing for the remaining outputs.

Expected startup log:

```text
HERMES pre-raid readiness map-selection preparation enabled.
HERMES 0.1.0-alpha13.0.10 map-prefetched readiness and best trader/flea craft profit loaded.
```

## Installation

Overlay the `Client` and `Server` folders onto the current HERMES source tree, delete the HERMES `bin` and `obj` folders, rebuild the solution in Visual Studio, and restart the SPT server and EFT client.
