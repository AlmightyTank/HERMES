# HERMES 1.1.0 Release Notes

1.1.0 introduces the first real confirmed action: inventory tags.

## New In 1.1.0

- Apply a tag to explicitly selected owned inventory items.
- Change an existing tag on explicitly selected owned inventory items.
- Remove an existing tag from explicitly selected owned inventory items.
- Apply the same tag to selected matching item copies from Items & Market.
- Confirmation previews open as a popout before writing.
- Confirmation previews show old and new tag values for every selected item.
- Confirmations reject missing items, moved items, and stale tag state before writing.
- The action executor only edits `upd.Tag`; it does not alter item location, parent, slot, stack, children, or inventory structure.
- Added **Interface -> Font size percent** for native UI text scaling from 80% to 130%.
- Added capped button growth so larger font sizes keep button labels readable.
- Moved the HERMES inventory tab after Tasks and preserved the relative order of later tabs for better compatibility with mods such as WeekendDrops.

## Safety Model

- Every proposal is scoped to public instance keys the user selected.
- Confirmation tokens are short-lived and single-use.
- HERMES rechecks item identity, parent, slot, grid location, and previous tag values at confirmation time.
- If any selected item fails validation, the whole action is rejected and no selected item is mutated.

## GitHub Update Log

- Added confirmed inventory tag actions for selected owned items in Items & Market.
- Added **Interface -> Font size percent** with 80% to 130% native UI text scaling.
- Added default and capped button sizing so larger font settings keep labels readable.
- Moved the HERMES inventory tab after Tasks.
- Improved tab placement compatibility with mods such as WeekendDrops by preserving later tab order.

## SPT Forge Update Log

- New Items & Market support for applying, changing, and resetting inventory tags on selected owned items.
- New F12 setting: **Interface -> Font size percent**.
- Buttons now grow up to a capped maximum when font size is increased.
- HERMES now appears after Tasks in the inventory tab strip.
- Better compatibility with tab-adding mods, including WeekendDrops placement after Prestige.
