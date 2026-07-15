# HERMES Alpha12.3 Release Checklist

## Build

- [ ] Rebuild `HERMES.sln` against SPT 4.0.13.
- [ ] Confirm `Hermes.Client.dll` and `Hermes.Server.dll` deploy to the configured SPT installation.
- [ ] Start the server and confirm version `0.1.0-alpha12.3`.
- [ ] Start the client and confirm the title shows `Follow-Up Context`.

## Follow-up context

- [ ] Ask `What do I need for Saving the Mole?`, then ask `What key?` and confirm the quest remains the subject.
- [ ] Ask about a named item, then ask `Where do I use it?` and confirm the same item is analyzed.
- [ ] Ask about a named craft, then ask `Why?` or `What is missing?` and confirm the same recipe is used.
- [ ] Ask about a hideout area, then ask `Is it ready?` and confirm the same area is used.
- [ ] Ask about a map, then ask `What quests are there?` and confirm the same map is used.
- [ ] Ask `What are we talking about?` and confirm the current and recent subjects are shown.
- [ ] Use the **Forget** button and confirm the next older subject becomes current.
- [ ] Use `Clear context` and confirm all remembered subjects are removed.
- [ ] Use **Clear chat** and confirm messages and subjects are cleared together.

## Ambiguity choices

- [ ] Trigger an item ambiguity and answer `the second one`.
- [ ] Trigger a quest or craft ambiguity and answer with an option number.
- [ ] Confirm the selected ambiguity choice becomes the current subject.

## Profile safety

- [ ] Confirm `/hermes/profile/context` returns a successful opaque token response.
- [ ] Switch SPT profiles and confirm the Assistant conversation/context is reset when the F12 setting is enabled.
- [ ] Disable profile-change reset and confirm HERMES does not automatically clear context.
- [ ] Confirm no raw session/profile identifier is displayed in the UI or diagnostics.

## F12 settings

- [ ] Disable follow-up context and confirm pronouns are no longer expanded from previous subjects.
- [ ] Disable context display and confirm the context box is hidden.
- [ ] Change maximum remembered subjects and confirm recent-subject retention is bounded.

## Regression

- [ ] Named item, quest, craft, station, map, and hideout-area recognition still works.
- [ ] Cross-system recommendations still work.
- [ ] Ask HERMES context-menu icon appears below Discard.
- [ ] Craft and Hideout item Ask HERMES buttons still open exact item context.
- [ ] Item Search, Hideout, Crafts, Stash, Loadout, and Raid Planner still load.
- [ ] No action modifies inventory, quests, hideout, insurance, flea, or traders.
