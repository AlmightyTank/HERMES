# HERMES 0.1.0-alpha12.1 local release checklist

1. Open `HERMES.sln` in Visual Studio with the SPT root still set to `C:\RealSPT`.
2. Run **Build → Rebuild Solution** and confirm both projects compile without warnings promoted to errors.
3. Confirm `Hermes.Client.dll` and `Hermes.Server.dll` deploy to the configured SPT installation.
4. Confirm the build project creates `HERMES-0.1.0-alpha12.1.zip`.
5. Start the SPT server and confirm the HERMES server module loads with version `0.1.0-alpha12.1`.
6. Start the client and confirm the log reports `HERMES 0.1.0-alpha12.1 loaded`.
7. Open the Assistant and test the broad prompts: loadout readiness, best raid, safe-to-sell stash, ready crafts, and hideout attention.
8. Test named quest recognition with `What do I need for Saving the Mole?` and verify localized objectives plus the acquire-during-raid science-office key.
9. Test map recognition with `What quests do I have on Ground Zero?` and `Am I ready for Customs?`.
10. Test recipe recognition with a known output using `Why can't I craft <output>?`; verify station, quest lock, and missing ingredients.
11. Test crafting-station recognition with `What can I craft at Workbench?`.
12. Test hideout-area recognition with `What does Medstation need?` and verify exact item/progression requirements.
13. Test item recognition with a full item name, a short name, and a minor typo while fuzzy matching is enabled.
14. Lower and raise **Entity confidence percent** and confirm broad matches become more or less strict.
15. Create an ambiguous item or subject query and verify HERMES lists no more than the configured ambiguity limit instead of guessing.
16. Disable fuzzy entity matching and verify exact and substring matches still work.
17. Verify selected-item questions continue to use the exact selected stash/equipped instance where available.
18. Confirm all Assistant actions remain navigation-only and that no inventory, trader, flea, craft, insurance, or quest state is changed.
19. Run the existing Item Search, Hideout, Crafts, Stash, Loadout, and Raid Planner smoke tests to confirm no regression.
