# HERMES 0.1.0-alpha12.3

Alpha12.3 adds deterministic multi-turn conversation context to the local HERMES Assistant. It remembers the player-facing subject resolved by the previous answer and uses that subject for follow-up questions without sending profile or conversation data outside the game.

## Follow-up questions

HERMES can now continue a conversation such as:

```text
You: What do I need for Saving the Mole?
HERMES: ...

You: What key?
HERMES: TerraGroup science office key ...

You: Where do I get it?
HERMES: The key is acquired during the Ground Zero raid from the lab scientist's body.
```

Other supported follow-up forms include:

- `Why?`
- `What is missing?`
- `Do I have it?`
- `Where do I use it?`
- `Is it ready?`
- `Is it profitable?`
- `What about that map?`
- `What are we talking about?`
- `Forget that`
- `Clear context`

## Remembered subject types

The current local conversation can remember:

- Item
- Quest
- Map
- Craft output
- Crafting station
- Hideout area

The current subject is shown above the Assistant conversation when enabled. The selected Item Search or Ask HERMES item remains visible separately and is only used when the wording clearly refers to the selected item.

## Ambiguity follow-ups

When Alpha12.1 entity recognition returns several possible matches, Alpha12.3 remembers the displayed choices. The player can answer with:

- `The first one`
- `The second one`
- `Option 3`
- The exact displayed name

The chosen entity becomes the active conversation subject.

## Profile safety

A new read-only `/hermes/profile/context` route returns an opaque hash of the active SPT session/profile identifier. HERMES never displays or stores the raw identifier.

When **Reset context when PMC profile changes** is enabled, a changed token clears the previous local Assistant conversation and remembered subjects. This prevents a follow-up from accidentally using another PMC profile's context.

## New BepInEx/F12 settings

Under **Assistant**:

- Enable follow-up conversation context
- Show conversation context
- Maximum remembered subjects (1–12)
- Reset context when PMC profile changes

The existing maximum conversation-message setting remains independent from subject memory.

## Context controls

- **Forget** above the conversation removes the current remembered subject.
- **Clear chat** clears both conversation messages and remembered subjects.
- `Forget that` removes the current subject while keeping older recent subjects available.
- `Clear context` removes all remembered subjects without requiring the full HERMES window to close.

## Existing Alpha12 features retained

- Local deterministic Assistant
- Item, quest, map, craft, station, and hideout-area recognition
- Ambiguity handling
- Cross-system next-step reasoning
- Raid ranking and map preparation
- Craft-versus-raid comparison
- Ask HERMES context-menu icon
- Ask HERMES buttons in Crafts, Hideout, Stash, and Loadout

## Safety boundary

HERMES remains local and read-only. It does not buy, sell, list, insure, equip, move, craft, accept, complete, or modify anything.

## Build

Open `HERMES.sln` and run **Build → Rebuild Solution** against the SPT 4.0.13 installation configured by the project files. The normal post-build deployment copies the generated modules into `C:\RealSPT`.

## Additional source correction

Alpha12.3 corrects the Item Search result-button label to use `string.Join("\n", lines)`. The Alpha12.2 source package contained a literal line break inside that string expression, which could prevent the client project from compiling.
