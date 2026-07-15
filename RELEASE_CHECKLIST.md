# HERMES 0.1.0-alpha12.4.1 Release Checklist

## Build

- [ ] Open `HERMES.sln` in Visual Studio.
- [ ] Confirm the configured SPT root points to `C:\RealSPT`.
- [ ] Run **Build → Rebuild Solution**.
- [ ] Confirm client and server modules auto-deploy without errors.
- [ ] Start SPT.Server and confirm HERMES reports `0.1.0-alpha12.4.1`.

## Notice behavior

- [ ] Enable proactive notices and use **Check now** in the Assistant tab.
- [ ] Confirm critical Loadout warnings create at most one Loadout notice.
- [ ] Confirm a high-value uninsured item creates a Value & Insurance notice.
- [ ] Complete a hideout production and confirm the completion notice.
- [ ] Confirm a ready hideout upgrade is reported when enabled.
- [ ] Confirm a ready profitable craft respects the minimum-profit threshold.
- [ ] Enable Stash notices and confirm the minimum-value threshold is respected.
- [ ] Confirm **Only notify on changes** prevents unchanged conditions from repeating.
- [ ] Disable change-only mode and confirm the repeat cooldown is respected.
- [ ] Confirm overlay cards remain visible until opened or dismissed and remain in the Assistant inbox history.
- [ ] Confirm clicking anywhere on a notice card navigates to the correct HERMES tab and sub-view.
- [ ] Confirm automatic checks pause during a raid when raid notices are disabled.
- [ ] Confirm profile switching clears prior notice state.

## Safety

- [ ] Confirm no notice action buys, sells, lists, equips, moves, insures, crafts, collects, accepts, or completes anything.

## Persistent notice test

- [ ] Generate a proactive notice while HERMES is closed.
- [ ] Confirm the card appears at the lower-right in EFT style.
- [ ] Confirm it remains visible for more than 20 seconds.
- [ ] Click the card body and confirm HERMES opens to the intended tab/sub-view.
- [ ] Generate another notice and use × to dismiss it without opening HERMES.
- [ ] Confirm cards remain clickable above the open HERMES window.
