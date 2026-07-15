# HERMES 0.1.0-alpha12.4.2 Release Checklist

## Build

- [ ] Open `HERMES.sln` in Visual Studio.
- [ ] Confirm the configured SPT root points to `C:\RealSPT`.
- [ ] Run **Build → Rebuild Solution**.
- [ ] Confirm client and server modules auto-deploy without errors.
- [ ] Start SPT.Server and confirm HERMES reports `0.1.0-alpha12.4.2`.
- [ ] Confirm the BepInEx log reports `HERMES native EFT notification click routing enabled.`

## Native EFT notification behavior

- [ ] Enable proactive notices and use **Check now** in the Assistant tab.
- [ ] Confirm the notice is displayed by EFT's normal notification stack, not a separate HERMES IMGUI card.
- [ ] Confirm the notice uses an EFT Alert, Quest, Hideout, or Note icon.
- [ ] Confirm the notice remains visible for more than 20 seconds.
- [ ] Left-click the native notification and confirm HERMES opens to the intended tab/sub-view.
- [ ] Confirm EFT closes the notification normally after the click.
- [ ] Confirm ordinary EFT notifications still behave normally and do not open HERMES.
- [ ] Generate a notice while the notification manager is not ready during startup and confirm it appears after the main menu becomes available.
- [ ] Open the Assistant inbox and dismiss a visible notice; confirm the matching native notification closes.
- [ ] Use **Dismiss all** and confirm all HERMES-owned native notices close.
- [ ] Switch profiles and confirm old HERMES native notifications close and notice state resets.

## Notice categories

- [ ] Confirm critical Loadout warnings create at most one Loadout notice.
- [ ] Confirm a high-value uninsured item creates a Value & Insurance notice.
- [ ] Complete a hideout production and confirm the completion notice.
- [ ] Confirm a ready hideout upgrade is reported when enabled.
- [ ] Confirm a ready profitable craft respects the minimum-profit threshold.
- [ ] Enable Stash notices and confirm the minimum-value threshold is respected.
- [ ] Confirm **Only notify on changes** prevents unchanged conditions from repeating.
- [ ] Disable change-only mode and confirm the repeat cooldown is respected.
- [ ] Confirm automatic checks pause during a raid when raid notices are disabled.

## Safety

- [ ] Confirm no notice action buys, sells, lists, equips, moves, insures, crafts, collects, accepts, or completes anything.
