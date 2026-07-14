# HERMES 0.1.0-alpha11.3.1

## Alpha11.3.1 — Ground Zero and equipped-item Ask HERMES fix

- Raid Planner now resolves location id `653e6760052C01c1c805532F` as **Ground Zero**.
- Ask HERMES now supports exact PMC-owned items rooted in either the stash or equipped-character inventory.
- Items inside an equipped backpack, tactical rig, pockets, secure container, weapon assembly, armor, helmet, or other equipped tree can be analyzed by exact profile item id.
- The **Value & Insurance** view now includes an **Ask HERMES** button on every carried item row.
- The right-click context action recognizes inventory, equipment, character-gear, and weapon-modding item views while continuing to exclude raid/world loot menus.
- Exact trader analysis now accepts equipped and carried instances, not only stash-rooted instances.
- HERMES displays where the selected item is located, such as `Carried in backpack`, `Equipped on primary weapon`, or `PMC stash`.

HERMES is a read-only SPT 4.0.13 personal operations assistant.

## Alpha11.3 — Loadout Value and Insurance

Alpha11.3 adds a **Value & Insurance** view to the Loadout tab. HERMES values every carried inventory instance separately instead of hiding ammunition, magazines, attachments, armor plates, medical supplies, and provisions inside one assembly total.

### Value totals

HERMES now reports:

- Exact condition-adjusted trader liquidation value
- Market replacement value
- Cheapest supported replacement value per item
- Total carried replacement value
- At-risk raid replacement value
- Protected-slot replacement value
- Insured replacement value
- Uninsured insurable replacement value
- Estimated insurance cost when a configured insurer coefficient can be resolved

### Shared replacement-price order

Market replacement uses the same project-wide order:

1. Active local cash flea offer
2. Converted active flea barter offer
3. SPT dynamic flea-market price
4. Handbook fallback only when no market price exists

Available trader cash and barter purchases are also checked. The **best replacement** total chooses the cheaper supported trader or market source for each carried item.

### Value categories

The Value & Insurance view separates:

- Weapons and attachments
- Armor and plates
- Ammunition and magazines
- Medical supplies
- Provisions
- Other equipment

Each category shows trader liquidation, market replacement, best replacement, at-risk value, and uninsured value.

### Insurance and raid risk

Insurance is evaluated per exact profile item instance.

- Insured equipment is counted separately from uninsured equipment.
- High-value uninsured items generate Loadout warnings.
- Secure-container contents, melee/scabbard equipment, armbands, compass items, and special-slot items are shown as protected and excluded from estimated raid-loss value.
- Pocket, rig, backpack, weapon, armor, and other normal carried contents remain part of raid risk.
- Ammunition, medical supplies, and provisions are valued but marked non-insurable.

The default high-value uninsured threshold is ₽100,000 per item instance.

### Insurance-cost estimate

HERMES attempts to read the lowest enabled insurer price coefficient from the current server trader data. When available, it provides an estimated insurance budget for currently uninsured insurable equipment. The UI clearly labels this as an estimate because loyalty levels and server mods can change the final checkout cost.

When no supported insurer coefficient is found, insurance state and uninsured value still work; only the insurance-cost estimate is unavailable.

### Alpha11 features retained

- Exact equipped-loadout readiness
- Weapon, magazine, and ammunition compatibility
- Armor and insert checks
- Medical and sustenance coverage
- Active quest gear and carried raid-item checks
- Map-based raid planner
- Inferred route keys
- Multi-map quest stages
- QuestRaidItems support

## Build and deploy

Open `HERMES.sln` in Visual Studio and choose:

```text
Build -> Build Solution
```

The build deploys:

```text
C:\RealSPT\SPT\user\mods\HERMES\Hermes.Server.dll
C:\RealSPT\BepInEx\plugins\HERMES\Hermes.Client.dll
```

It also creates:

```text
HERMES-0.1.0-alpha11.3.1.zip
```
