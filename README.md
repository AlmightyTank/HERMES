# HERMES 0.1.0-alpha11.3.5

## Alpha11.3.5 — Insurance classification correction

- Weapon attachments no longer become Medical merely because the serialized template exposes a default `MedUseType` field.
- Medical classification now requires a real medical resource, non-empty damage-treatment effects, or meaningful stimulator buffs.
- Weapon and armor ancestry takes priority when categorizing installed attachments and plates.
- Insurability now follows the final item category instead of the unreliable raw `IsMedical` flag.
- Attached items inherit insured status from an insured parent weapon, armor, rig, backpack, or other parent assembly when SPT stores insurance on the parent instance.
- Retains Alpha11.3.4 Saving the Mole and localized Raid Planner objective support.

## Alpha11.3.4 — Saving the Mole in-raid key route

- Raid Planner now adds the TerraGroup science office key to **Saving the Mole** on Ground Zero.
- The requirement is labeled **Acquire route key in raid**, not Bring.
- The planner instructs the player to loot the key from the lab scientist's body and use it on office no. 4.
- In-raid acquisition requirements appear in the combined checklist but do not count as missing pre-raid gear or change a prepared plan to Missing Gear.
- When the key is currently carried, the checklist changes to Acquired.
- After the localized “Access the lab scientist's office” objective is complete, the key requirement is removed from the remaining route plan.

HERMES is a read-only SPT 4.0.13 personal operations assistant.

## Alpha11.3.3 — Localized Raid Planner objectives

Raid Planner now resolves objective text from SPT's loaded English locale database using each quest condition ID before it attempts to synthesize a description from the raw condition type.

This replaces generic lines such as:

```text
Complete 1 progress toward location conditions.
```

with the same player-facing objective text used by EFT/SPT, for example:

```text
Locate the machine gun on Ground Zero.
Find the wine bottle in the store.
Eliminate Scavs on Ground Zero.
```

### Locale lookup behavior

HERMES checks:

1. Explicit locale keys encoded on the condition
2. The exact condition ID used by vanilla and custom quest locales
3. Common condition-ID description/objective suffixes
4. Nested CounterCreator condition locales
5. Structured condition-data fallback only when no objective locale exists

All loaded English locale branches are merged, so VCQL and other custom quests can provide objective text through their own condition-ID locale entries.

HTML/color tags and line breaks are cleaned before objective text is displayed in the overlay.

## Alpha11.3 features retained

- Ground Zero mapping for `653e6760052C01c1c805532F`
- Ask HERMES for exact stash and equipped PMC items
- Loadout market replacement value
- Condition-adjusted trader liquidation
- At-risk and protected-slot value separation
- Exact item-instance insurance state
- High-value uninsured warnings
- Value & Insurance Loadout view

## Build and deploy

Open `HERMES.sln` and choose **Build → Build Solution**.

The build deploys to `C:\RealSPT` and creates:

```text
HERMES-0.1.0-alpha11.3.5.zip
```