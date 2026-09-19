# You can place Modded Foods with ServingTray!
![](https://i.ibb.co/xSRQzCXB/Screenshot-2026-03-05-195624.png)
<br>
(Example of placing ValheimCuisine feasts with ServingTray)  
※SideNote: You can take back placed food with `shift+e`

![](https://i.ibb.co/8LbQbTsD/Screenshot-2026-03-05-195337.png)  
Builds against original Valheim 1.0.15 assemblies. Jotunn is not required by ServeYouRight.
Serving Tray (the `Feaster` item) uses its existing **Categories / By usage** list, with optional per-mod groups:
- `Misc - <ModName>`
- `Food - <ModName>`
- `Meads - <ModName>`
- `Feasts - <ModName>`

![](https://i.ibb.co/QF16ScQS/Screenshot-2026-03-05-195401.png)
<br>
(Example of ValheimCuinse Feasts merged into Vanilla Feasts tab)  
Lets you decide per mod and per category whether items stay in per-mod groups or merge back into base groups.

![](https://i.ibb.co/jvQnxnrc/Screenshot-2026-03-05-203206.png)
- Supports live config reload when the config file changes.
- `On`: use `Category - ModName` tag
- `Off`: merge into base category group
- Category names reuse Serving Tray's base labels, so `Misc/Food/Meads/Feasts` follow the game's current language labels.
- WackysDatabase clone ownership takes priority. DataForge-managed item clones are recognized through its optional public API; DataForge is not required.
- Other mod foods use a conservative bundle/resource owner lookup. Ambiguous, unobserved or renamed runtime prefabs stay in a base group. This lookup does not guarantee the same coverage as Jotunn ModQuery.
- Existing configuration section names and Food/Meads/Feasts keys are retained; Misc is an additional per-mod option for pieces already on Feaster. Mod identity uses the plugin GUID and original display name.
- No ServeYouRight top-level tab is added. Grouping and food registration target the table attached to the registered Feaster item, not a tool's ability to remove food. Other hammers and terrain tools keep their own menus, including when Infinity Hammer enables `Remove anything`.
- Opening Serving Tray exposes the game's advanced menu locally, including its normal search/recent/favorite lists. The tool's simple-menu flag is restored on close, tool switch or menu destruction. The screenshots above show the previous UI.
- Other installed food mods can still require Jotunn; removing ServeYouRight's dependency does not update those mods.

## Developer builds

Run this command from the repository root with the .NET SDK and .NET Framework 4.8 targeting pack installed. First check that `environment.props` points to your Valheim and BepInEx locations; compilation uses original game DLLs.

```powershell
dotnet build .\ServeYouRight.sln -c Debug -p:DeployToGame=true
if ($LASTEXITCODE -ne 0) { throw "Debug build or game DLL copy failed." }
```

After successful compilation, the build copies only the final `bin\Debug\ServeYouRight.dll` to the plugin destinations in `environment.props`, overwriting the existing DLL. The default destination is `C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins`. Verify that the source and installed DLL have matching SHA-256 hashes. This command does not run automated tests or Valheim, change release versions, or create release ZIPs.

If a build without a local game update is explicitly needed, use `DeployToGame=false`:

```powershell
dotnet build .\ServeYouRight.sln -c Debug -p:DeployToGame=false
```

Use Release builds only for an explicitly requested release. On Windows, the normal Release build updates `Thunderstore/manifest.json` and creates Thunderstore and Nexus ZIP packages. A new ZIP in a Mod Release Manager watch folder can trigger automatic uploads; the manager handles uploads according to its saved settings.

See [compatibility checks and remaining runtime checks](tests/README.md) for validation commands and limits.
