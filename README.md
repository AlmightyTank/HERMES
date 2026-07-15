# HERMES 0.1.0-alpha12.4.2

## Native EFT notification integration

Alpha12.4.2 removes the custom HERMES notification-card overlay. Proactive notices are now submitted to EFT's real `NotificationManagerClass` queue and rendered by EFT's native `BaseNotificationView` prefab.

- Uses `NotificationManagerClass.DisplayMessageNotification(...)`.
- Uses `ENotificationDurationType.Infinite`, so a notice remains until the player clicks it or dismisses it from the HERMES inbox.
- Uses EFT's native notification icons: Alert, Quest, Hideout, or Note depending on notice type.
- Critical and warning notices use the native notification text-color option.
- Clicking a HERMES-owned native notification opens HERMES directly to its related tab and sub-view.
- The click patch only handles descriptions registered by HERMES; every normal EFT notification is left untouched.
- If EFT's notification manager is not active yet during startup, the notice remains pending and is submitted later.

The embedded winged icon remains used by the **Ask HERMES** item context-menu action. EFT's native notification queue uses its own icon set and prefab.

## Proactive notice types

HERMES can surface configured notices for:

- Critical loadout-readiness problems
- High-value uninsured equipment
- Completed hideout production
- Hideout areas ready to upgrade
- Ready profitable crafts
- Optional stash cleanup or safe-to-sell opportunities

Stash notices remain disabled by default because full Stash analysis is the heaviest proactive check.

## Native click routing

EFT's `BaseNotificationView.OnPointerClick(...)` already closes native notifications. HERMES adds a narrow prefix that:

1. Confirms the click is a left click.
2. Reads the native notification object attached to that view.
3. Checks whether the exact notification description belongs to HERMES.
4. Opens the related HERMES tab.
5. Allows EFT's original click method to continue and close the notification normally.

Inbox actions can also close the matching native EFT notification view.

## Quiet notification behavior

- Automatic checks pause during raids by default.
- **Only notify on changes** prevents unresolved conditions from repeating.
- When repeats are allowed, a configurable cooldown is enforced.
- Native notices remain available in the Assistant notice history after being shown.
- Profile changes clear HERMES notice state and close HERMES-owned native notifications.
- A failed optional data source does not prevent other notice categories from being evaluated.

## Assistant notice inbox

The Assistant tab retains:

- Check now
- Open related HERMES tab
- Dismiss individual notice
- Dismiss all
- Clear notice history
- Current check status

Opening or dismissing an inbox entry also closes its corresponding native EFT notification.

## BepInEx/F12 settings

Under **Assistant Notices**:

- Enable proactive notices
- Check interval minutes
- Repeat cooldown minutes
- Overlay auto-dismiss seconds — retained only for configuration compatibility and ignored
- Maximum visible/native-active notices
- Show notices while HERMES is closed
- Show notices during raid
- Only notify on changes
- Per-category notice toggles and thresholds

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
