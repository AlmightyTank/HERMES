# HERMES 0.1.0-alpha12.0.1 local release checklist

1. Open `HERMES.sln` in Visual Studio with the SPT root configured as `C:\RealSPT`.
2. Run **Build → Rebuild Solution** and confirm both `Hermes.Server.dll` and `Hermes.Client.dll` compile.
3. Confirm `UnityEngine.TextRenderingModule.dll` resolves for the client project.
4. Confirm the build project creates `HERMES-0.1.0-alpha12.0.1.zip` and deploys both DLLs when auto-deploy is enabled.
5. Start SPT.Server and confirm HERMES loads without DI or route errors.
6. Start the client and confirm the log reports `HERMES 0.1.0-alpha12.0.1 loaded`.
7. Open HERMES and confirm the Assistant tab appears when enabled in F12.
8. Ask `Am I ready for a raid?` and verify the answer matches the Loadout tab.
9. Ask `What is the best raid for me right now?` and verify the map and requirements match Raid Planner.
10. Ask `What items can I safely sell?` and verify totals match the Stash tab under the current reservation settings.
11. Ask `What crafts are ready now?` and verify results match Crafts.
12. Ask `What hideout upgrades need attention?` and verify results match Hideout.
13. Select an item through Item Search or the context menu, then ask `What is this item worth?`.
14. Confirm Assistant navigation buttons open the expected main tab and Raid Planner sub-view.
15. Change Assistant F12 settings and confirm suggested prompts, selected-item context, message retention, and tab visibility update.
16. Use global Refresh while Assistant is open and confirm the last question is rerun after cache invalidation.
17. Confirm HERMES performs no inventory, trader, flea, craft, insurance, or quest write actions.
18. Copy diagnostics after a forced route failure and confirm no profile or inventory contents are included.
