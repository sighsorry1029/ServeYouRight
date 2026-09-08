# You can place Modded Foods with ServingTray!
![](https://i.ibb.co/xSRQzCXB/Screenshot-2026-03-05-195624.png)
<br>
(Example of placing ValheimCuisine feasts with ServingTray)  
※SideNote: You can take back placed food with `shift+e`

![](https://i.ibb.co/8LbQbTsD/Screenshot-2026-03-05-195337.png)  
Creates per-mod tabs via Jotunn category support:
- `Food - <ModName>`
- `Meads - <ModName>`
- `Feasts - <ModName>`

![](https://i.ibb.co/QF16ScQS/Screenshot-2026-03-05-195401.png)
<br>
(Example of ValheimCuinse Feasts merged into Vanilla Feasts tab)  
Lets you decide per mod and per category whether items stay in per-mod tabs or merge back into vanilla tabs  

![](https://i.ibb.co/jvQnxnrc/Screenshot-2026-03-05-203206.png)
- Supports live config reload when the config file changes.
- `On`: use `Category - ModName` tab
- `Off`: merge into vanilla category tab
- Category names reuse Serving Tray's base labels, so `Food/Meads/Feasts` follow the game's current language labels.
- Wackysdatabase cloned items are supoorted. But the config will show after you get into the world and click the wackysdatabase tab on ServingTray

## Developer builds

Run this command from the repository root with the .NET SDK and .NET Framework 4.8 targeting pack installed. First check that `environment.props` points to your Valheim, BepInEx, and publicized assembly locations.

```powershell
dotnet build .\ServeYouRight.sln -c Debug -p:DeployToGame=true
if ($LASTEXITCODE -ne 0) { throw "Debug build or game DLL copy failed." }
```

After successful compilation, the build copies only the final `bin\Debug\ServeYouRight.dll` to the plugin destinations in `environment.props`, overwriting the existing DLL. The default destination is `C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins`. Verify that the source and installed DLL have matching SHA-256 hashes. This command does not run automated tests or Valheim, change release versions, or create release ZIPs.

If a build without a local game update is explicitly needed, use `DeployToGame=false`:

```powershell
dotnet build .\ServeYouRight.sln -c Debug -p:DeployToGame=false
```

Use Release builds only for an explicitly requested release. On Windows, the normal Release build updates `Thunderstore/manifest.json` and creates Thunderstore and Nexus ZIP packages. A new ZIP in a Mod Release Manager watch folder can trigger automatic uploads; check the configured project and publishing destinations before packaging.
