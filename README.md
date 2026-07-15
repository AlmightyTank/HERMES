# HERMES 0.1.0-alpha12.4.1

## Persistent EFT-style clickable notices

- Replaces the temporary top-right IMGUI window with compact lower-right EFT-style notification cards.
- Notice cards remain visible until the player clicks them or dismisses them with the close button.
- Clicking anywhere on a card opens HERMES directly to its related tab and sub-view.
- Uses the embedded winged HERMES icon and severity accent bars.
- Cards are rendered after the main HERMES window so they remain visible and clickable above it.
- The previous auto-dismiss setting remains bound for configuration compatibility but is no longer used.

Alpha12.4 adds the final planned Alpha12 feature phase: optional proactive Assistant notices and a quiet, change-aware notice inbox. All checks use the same local read-only HERMES services as the existing tabs. No external AI service is used and no profile data leaves the game.

## Proactive notice types

HERMES can surface configured notices for:

- Critical loadout-readiness problems
- High-value uninsured equipment
- Completed hideout production
- Hideout areas ready to upgrade
- Ready profitable crafts
- Optional stash cleanup or safe-to-sell opportunities

Stash notices are disabled by default because full Stash analysis is the heaviest proactive check.

## Quiet notification behavior

- Automatic checks pause during raids by default.
- **Only notify on changes** prevents the same unresolved condition from repeating.
- When repeat notifications are allowed, a configurable cooldown is enforced.
- On-screen cards remain visible until opened or dismissed and also remain available in the Assistant notice inbox history.
- Profile changes clear notice state so recommendations from one PMC are not shown for another.
- A failed optional data source does not prevent the remaining notice categories from being evaluated.

## Assistant notice inbox

The Assistant tab now contains a **Proactive Notices** section with:

- Check now
- Open related HERMES tab
- Dismiss individual notice
- Dismiss all
- Clear notice history
- Current check status

Opening a notice navigates directly to Loadout, Value & Insurance, Hideout, Crafts, or Stash as appropriate.

## New BepInEx/F12 settings

Under **Assistant Notices**:

- Enable proactive notices
- Check interval minutes
- Repeat cooldown minutes
- Overlay auto-dismiss seconds (legacy compatibility; ignored)
- Maximum visible notices
- Show notices while HERMES is closed
- Show notices during raid
- Only notify on changes
- Loadout readiness notices
- High-value uninsured notices
- Completed production notices
- Ready hideout upgrade notices
- Ready profitable craft notices
- Minimum craft notice profit
- Stash opportunity notices
- Minimum stash notice value

## Existing Alpha12 features retained

- Local deterministic Assistant
- Entity recognition and ambiguity handling
- Cross-system reasoning and ranked next steps
- Follow-up conversation context
- Ask HERMES item context actions and icon
- Ask HERMES actions in Crafts, Hideout, Stash, and Loadout

## Safety boundary

HERMES remains read-only. It does not buy, sell, list, move, equip, insure, craft, collect production, accept quests, or complete quests.

## Build

Open `HERMES.sln` and run **Build → Rebuild Solution** against SPT 4.0.13. The normal post-build deployment copies the generated modules into `C:\RealSPT`.
