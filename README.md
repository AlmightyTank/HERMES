# HERMES 0.1.0-alpha11.2

HERMES is a read-only SPT 4.0.13 personal operations assistant.

## Alpha11.2 — Map-based Raid Planner

Alpha11.2 adds a **Raid Planner** view inside the Loadout tab. It groups active quest objectives by map and combines their explicitly encoded raid-item and equipment requirements into one preparation checklist.

### Raid-plan summary

For each map HERMES now reports:

- Active quest count
- Total and completed objective counts
- Prepared, Missing Gear, or Ready to Turn In status
- Combined raid-critical bring/equip checklist
- Required, carried, FIR-carried, and missing quantities
- Contributing quest names for each combined requirement
- Individual quest objective cards
- Multiple weapon/equipment restriction warnings
- Any-map objective guidance

### Requirement merging

Consumable raid tools and plant items are summed across quests. For example, two active quests requiring two and three MS2000 markers produce one five-marker checklist entry.

Equipment and weapon restrictions use the largest single requirement instead of being added together. HERMES warns when several distinct restrictions may not fit one raid loadout.

### Conservative behavior

- Only active quests are included.
- Completed conditions remain visible but do not reserve raid gear.
- Ordinary turn-in items remain informational unless the quest marks them as raid-counted or one-session-only.
- HERMES does not expose template IDs or internal objective-zone identifiers.
- Keys and tools are only listed when declared by quest data; undeclared route keys are not inferred yet.
- No inventory, quest, or loadout changes are performed.

### Loadout views

- **Overview**
- **Weapons & Ammo**
- **Armor**
- **Medical**
- **Quest Gear**
- **Raid Planner**

All Alpha10 and earlier stash, trader, flea, hideout, and crafting intelligence remains included.

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
HERMES-0.1.0-alpha11.2.zip
```
