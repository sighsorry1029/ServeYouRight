# ServeYouRight checks

The plugin targets .NET Framework 4.8. The Feaster menu fix is built and checked against original Valheim 1.0.15 assemblies. No publicized game DLL is used or shipped. Tests are separate from the plugin project and packaging.

```powershell
dotnet build ServeYouRight.sln -c Debug -p:DeployToGame=true
dotnet build tests/ServeYouRight.Checks -c Debug
& tests/ServeYouRight.Checks/bin/Debug/net48/ServeYouRight.Checks.exe bin/Debug/ServeYouRight.dll C:/Users/blizz/RiderProjects/dataforge/bin/Debug/DataForge.dll 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed' 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core'
```

The integration check takes the optional DataForge DLL explicitly; the installed plugin itself does not reference that assembly. Run against both original client and dedicated-server Managed directories. Do not use compile-only publicized references or modified game stubs for compatibility claims.

## Automated coverage

- Unique embedded resource and complete-token ownership, substring rejection, same display name/different GUID, load order and permanently ambiguous asset entries. `AssetOwnerMatching.cs` is identically vendored in both repositories; its SHA-256 must match after policy changes. DataForge also runs the policy in its own LogicChecks suite.
- Actual final plugin assembly has no Jotunn/DataForge assembly reference or Jotunn loader dependency.
- Actual cached BuildUi field accessors initialize against original game fields.
- The final DLL never reads `m_canRemoveFeasts` and does not access top-level tab buttons or TabHandler. Removal capability must not identify Feaster.
- Existing Categories slot replacement/restoration runs with the game's original IPieceList classes: repeated open/close, shifted indices, other-mod replacement and missing Categories leave tab counts and unrelated lists intact. This is a managed state test, not Unity UI execution.
- All four per-mod category settings are checked On/Off against the actual plugin configuration dispatch, including Misc. Unrelated categories do not inherit this policy.
- The reflected DataForge event delegate binds to its final merged DLL. Clone readiness, success, failure, world reset and disposal/unsubscription are exercised using the real public API implementation. A clone belongs to its creator, not automatically to the creator of its source item.
- DataForge's separate `tests/DataForge.GameCompatibilityChecks` executable can check this plugin's game member instructions and Harmony target/argument metadata. It does not check dynamic reflection names or execute patches.

2026-09-10: ServeYouRight checks passed 22 assertions against client 1.0.7 and dedicated-server 1.0.7 original assemblies. DataForge passed 19 logic cases (including the owner collision case with eight assertions) and 67 API assertions. Both Debug builds succeeded without warnings/errors and copied their final DLLs to the game plugins directory with matching SHA-256.

2026-09-19 Feaster menu fix: Debug build succeeded with zero warnings/errors. The updated suite passed 45 assertions for each original 1.0.15 client (Steam 25390630) and dedicated-server (25390671) assembly set. Static compatibility checks resolved 270 game member instructions and checked 9 Harmony targets/injections, with zero failures on each set. The final DLL SHA-256 is `7D4FE0CE03792EFB6682999F16E2A2698CB7C87DB54A5D8108886C3069309C65`; copies in the local Steam plugins directory and the Gale profile identified by the reported session matched. The profile copy used the existing MSBuild `CopyOutputDLLPath2` property for this build only. No other profile or mod was changed.

The reported session loaded ServeYouRight 1.0.6, Infinity Hammer 1.84 and Jotunn 2.30.1 on Valheim 1.0.15. Its Infinity Hammer configuration enabled `Remove anything`. Inspection of that installed Hammer DLL shows `HoldUse.Prefix` temporarily setting removal capability around `Player.UpdatePlacement`, which opens BuildUi before the finalizer restores it. The fix removes this capability-based identification from both UI and injection. These are log/configuration/binary findings, not a recorded runtime trace of the failing frame. Feaster's item name is present in the original 1.0.15 SoftRef manifest and the session's item reference data.

2026-09-19 release 1.0.7: the normal Release build succeeded with zero warnings/errors. The final Release DLL passed the same 45 assertions and 270 game member / 9 Harmony target checks for each original 1.0.15 client and dedicated-server assembly set. Assembly version is 1.0.7.0; file/product version and the package manifest are 1.0.7, with BepInExPack 5.4.2350 as the only required dependency. Both ZIPs contain only their expected files; every archive entry matches its source SHA-256. Release DLL SHA-256: `74781CEAD108D43263F2F802E0EF44A0D7667B42FD1998A367C10284049072A3`. Thunderstore ZIP: `A0442D67D3B52926BAA3D3934870D292812555D18BBC4E8F37AA09C42D71ED1D`. Nexus ZIP: `6FB7FF55FFDB02987B0174EDC2EDECFB84DCA4989F6EE8531D5811A44F892B1B`. Local Steam and the previously identified Gale profile contain matching Release DLLs. These checks do not execute Unity gameplay.

The standalone .NET Framework runtime cannot load Player's new default-interface methods; Player refresh delegates require Unity/Mono execution verification. .NET 8 cannot initialize the installed Harmony build in this standalone check. Neither failure was worked around by modifying game DLLs. The successful net48 test initializes the supported menu accessors and tests source/API logic; it does not claim player delegate, Unity or Harmony patch execution success.

## Required game checks

1. Restart Valheim to load the new DLL. Test without Jotunn when using compatible food providers, and with the actual optional food mods. Their separate Jotunn requirements and old-game failures remain their own compatibility constraints.
2. On Feaster, compare each known food's GUID/display name and Misc/Food/Meads/Feasts grouping with the prior configuration. Include a mod-owned Misc piece and pre-existing food pieces; unknown ownership stays in the base category. Switch each On/Off setting, including while open and without changing the number of available pieces. Check language reload, search, recent/favorites, mouse and controller. Grouping uses the existing Categories / By usage slot; no new ServeYouRight tab should exist. The advanced menu flag is temporarily enabled while the tray menu is open and restored on close/tool change/destruction.
3. With Infinity Hammer's `Remove anything` both On and Off, switch hammer → hoe → Feaster → hammer and reopen each menu. Verify repeated menu/Hud lifecycles, another mod adding tabs before/after Categories, duplicate prefab entries, removed/late food entries, a second world and late DataForge apply/reset events. Other tools must retain their original lists and no food injection; closed/destroyed menus must restore the original Categories object. Catalog work runs on data refresh/menu open, not in the per-frame Update path (which only checks a pending bit).
4. Test Wacky and DataForge absent/present, creation/removal/failure of clones, matching names owned by different GUIDs and bundles unloaded after observation. Compare unidentified runtime-generated/renamed foods explicitly. Conservative bundle lookup can have less coverage than Jotunn ModQuery; unidentified foods remain available in base groups.
5. In disposable client/host/dedicated worlds, place/recover normal food, mead and Feast. Check original material/result mapping, one item consumed/recovered, simultaneous/repeated actions, owner changes, remote players, reconnect and saved placed food. The patch does not add RPCs, rename network prefabs or change item save formats. DataForge's local IsReady/Revision metadata is not network authority or synchronization acknowledgement.
6. Profile a large mod pack on repeated opens and idle frames. Asset name enumeration is cached for observed bundle objects; no FPS or allocation improvement has been measured in-game.

No game session was started for this patch. UI/native behavior, actual food-source coverage, client/host/dedicated multiplayer and crossplay remain unverified. The implementation step did not change the version or produce a Release ZIP; the separately requested 1.0.7 release packages this fix.
