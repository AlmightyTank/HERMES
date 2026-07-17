# HERMES 0.1.0-alpha12.7.4.6

## True-change refresh, actionable alert previews, and hideout requirements

This is a drop-in update for **Alpha12.7.4.5**. Install both the Client and Server folders.

## Changes

### Assistant action buttons

Assistant action buttons now size themselves from their label. `OPEN LOADOUT`, `OPEN STASH`,
`OPEN CRAFTS`, and similar actions display their complete text instead of truncating with an
ellipsis.

### Actionable alert previews

Assistant alerts no longer perform their own periodic profile requests. They are generated from
the profile-scoped summaries already owned by the server revision coordinator.

The Alerts panel now shows:

- the highest-priority actionable condition in the status preview;
- the number of additional actionable conditions;
- a card for each retained alert with its actual title, message, category, severity, Open action,
  and Dismiss action.

Dismissed conditions stay dismissed while the same condition remains active. If that condition
resolves and later returns, it can alert again.

### Hideout upgrade item lists

Every hideout station row now includes the complete item list for its next upgrade level:

- item name;
- owned quantity;
- required quantity;
- missing quantity;
- found-in-raid requirement when applicable;
- completion marker when the requirement is already met.

The existing selected-station detail panel remains available for the full acquisition and
progression breakdown.

### True-change server refresh

Profile workspaces now use a dedicated `/hermes/recheck` route. Pressing Refresh asks the server to
re-evaluate the active PMC sources without invalidating every domain and without immediately
replacing the current page model.

The server ignores continuously changing hideout timer and counter values when calculating normal
source revisions. It separately tracks completion milestones, so construction and production
completion still produce one real update when the threshold is crossed.

The client also compares semantic response fingerprints before replacing a workspace model. Even if
a server revision is conservative, an identical Hideout, Crafts, Stash, Loadout, or Raid Planner
response does not rebuild the page and does not re-request its selected detail.

Assistant notices no longer issue independent `/hermes/hideout/summary`, `/hermes/crafts/summary`,
`/hermes/stash/summary`, or `/hermes/loadout/summary` requests.

## Expected request log

A held watch still reconnects after its normal timeout:

```text
/hermes/watch/<revision>/open
```

That watch request is the quiet server connection and is expected every 30 seconds while HERMES is
open or every 60 seconds while closed.

With no true source change, the watch should return with an empty changed-domain list and the client
should reconnect without requesting workspace summaries. You should **not** see this sequence after
every timeout:

```text
/hermes/hideout/summary
/hermes/crafts/summary
/hermes/crafts/detail/<craft>
```

When inventory, quests, station state, production completion, loadout, or another semantic source
really changes, only its affected summaries are downloaded. A selected Hideout/Craft detail is
reloaded only when its parent summary also changed semantically.

Pressing Refresh in a profile workspace sends:

```text
/hermes/recheck
```

It wakes the held watch and performs a server source comparison. It does not force page replacement.
Items & Market remains demand-loaded and may clear its short-lived pricing caches when its own
Refresh button is used.

## Installation

1. Copy `Client/Hermes.Client` over the matching client project folder.
2. Copy `Server/Hermes.Server` over the matching server project folder.
3. Delete the HERMES client and server `bin` and `obj` folders.
4. Clean and rebuild `HERMES.sln` in Visual Studio.
5. Restart both SPT.Server and EscapeFromTarkov.

Both sides must be installed together because this update adds `/hermes/recheck` and expands the
hideout summary model.

## Runtime checks

1. Open Assistant and confirm `OPEN LOADOUT` is fully visible.
2. Create a loadout warning or another configured condition and press Check. Confirm the Alerts
   status identifies the condition and its card displays the actual message.
3. Open Hideout and confirm each station lists all item requirements for its next level.
4. Leave HERMES unchanged for several watch cycles. Only `/hermes/watch/...` should repeat.
5. Move an inventory item. Only the affected profile workspaces should update.
6. Start a short hideout production. Countdown changes should not refresh the page every watch;
   production completion should trigger one update.
7. Press Refresh without changing profile data. `/hermes/recheck` should run, but the current page
   should remain intact.

## Validation limitation

The source was syntax-parsed and the package was integrity-tested in this environment. Visual Studio
compilation and in-game execution against SPT 4.0.13 were not available here.
