# HERMES 0.1.0-alpha7.1

HERMES is a read-only in-game personal operations assistant for **SPT 4.0.13**.

Alpha7 adds local flea-market intelligence to the existing Alpha6 trader analysis. It reads the currently active SPT ragfair offer pool, compares normalized cash prices, estimates a listing fee, and recommends whether a player should use a vanilla trader or the local flea market.

## Handbook visibility rule

HERMES lists an item only when its handbook/reference value is greater than zero. Trader buy support does not override this rule. Quest-only items remain excluded as before.

## Alpha7 features

### Player-facing item search

- Open HERMES with `F8`.
- Search by localized item name or short name.
- Selectable and ranked results.
- No template IDs or internal database names appear in the popup or response models.
- Dedicated quest-only templates with `_props.QuestItem = true` are excluded.
- Items are excluded when they have neither a positive handbook value nor acceptance by a supported vanilla trader.

### Vanilla-trader intelligence

- Best estimated trader sale price.
- Click the best-sale header to expand the full vanilla-trader sale comparison.
- Current trader purchase offers.
- Cash and barter requirements.
- Player and required loyalty levels.
- Current stock and unlimited-stock state.
- Personal purchase limit and remaining quantity.
- Trader restock countdown.
- Exact localized quest name and required state for quest-locked offers.

### Local flea-market intelligence

- Click the **Local flea market** header to expand or collapse the flea analysis.
- Reads active local SPT flea offers for the selected item.
- Ignores trader duplicates so trader offers are not counted twice.
- Ignores barter-only, expired, locked, zero-quantity, and malformed offers.
- Uses rouble price per individual item, including stack and pack normalization.
- Calculates lowest, median, average, and highest reasonable cash price.
- Filters offers below 80% condition when comparable-condition offers exist.
- Falls back to used-condition offers when no 80%+ offers exist and labels that fallback.
- Filters extreme high-price outliers above three times the initial median.
- Shows up to eight lowest comparable offers with quantity, condition, and remaining time.
- Shows the player’s flea unlock level and item listing eligibility.
- Suggests a listing price one rouble below the lowest comparable offer.
- Estimates the flea listing fee using SPT’s server-side tax service.
- Estimates net flea proceeds.
- Compares the estimated flea net against the best supported trader sale value.
- Compares the lowest flea purchase price against the cheapest currently available cash trader offer.
- Requires at least three comparable flea offers before issuing a strong market recommendation.

Alpha7 remains fully read-only. It does not buy items, sell items, create listings, alter the stash, or modify the profile.

## Supported scope

- SPT 4.0.13
- Vanilla traders only
- Current local SPT flea economy
- English player-facing locale
- No Tony/YATM-specific support
- No external market API
- No historical price tracking

## Visual Studio build and automatic test deployment

1. Close the SPT server and Escape from Tarkov before rebuilding so Windows does not lock the installed DLLs.
2. Open `HERMES.sln` in Visual Studio 2022.
3. Select `Debug` or `Release`.
4. Use **Build > Build Solution**.

The `Hermes.Build` project automatically:

1. Builds `Hermes.Server.dll` and `Hermes.Client.dll`.
2. Creates the `dist` installation layout.
3. Creates `HERMES-0.1.0-alpha7.1.zip`.
4. Copies the updated DLLs into the testing installation at `C:\RealSPT`.

Automatic deployment locations:

```text
C:\RealSPT\SPT\user\mods\HERMES\Hermes.Server.dll
C:\RealSPT\BepInEx\plugins\HERMES\Hermes.Client.dll
```

No PowerShell script or generated `.props` file is used.

## Build properties

The defaults are defined in `Hermes.Build.csproj`:

```xml
<SptRoot>C:\RealSPT</SptRoot>
<DeployToTestEnvironment>true</DeployToTestEnvironment>
<TestSptRoot>$(SptRoot)</TestSptRoot>
```

To package without copying into the test installation:

```xml
<DeployToTestEnvironment>false</DeployToTestEnvironment>
```

## Build output

```text
dist\SPT\user\mods\HERMES\Hermes.Server.dll
dist\BepInEx\plugins\HERMES\Hermes.Client.dll
HERMES-0.1.0-alpha7.1.zip
```

## Current limitations

- Trader sale prices are estimates for a full-condition base item, not a selected stash instance.
- Flea condition is calculated from each offer’s root item; weapon attachments and armor inserts are not separately valued yet.
- Barter flea offers are counted as ignored and are not converted into a market price.
- Listing fees are estimates and may differ slightly from the final client listing screen.
- Trader barter estimates use handbook reference prices.
- Quest and hideout usage is still reference detection only.
