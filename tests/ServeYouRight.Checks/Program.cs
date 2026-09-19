using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ModAssetOwnership;
using Mono.Cecil;

internal static class Program
{
    private static int Passed;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Passed++; }

    private static void Main(string[] args)
    {
        OwnerChecks();
        if (args.Length != 4) throw new ArgumentException("<ServeYouRight.dll> <DataForge.dll> <original Managed directory> <BepInEx core>");
        MenuScopeChecks(args[0]);
        string[] folders = { Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Path.GetDirectoryName(Path.GetFullPath(args[0]))! };
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string folder in folders)
            { string path = Path.Combine(folder, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        };
        Assembly mod = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Check(!mod.GetReferencedAssemblies().Any(a => a.Name == "Jotunn" || a.Name == "DataForge"), "No hard assembly dependency on optional mods");
        RuntimeHelpers.RunClassConstructor(mod.GetType("ServerSyncModTemplate.ServingTrayMenu", true)!.TypeHandle);
        Passed++; // Creates actual menu field accessors against original game types.
        MenuSlotChecks(mod, Assembly.LoadFrom(Path.Combine(folders[0], "assembly_valheim.dll")));
        CategoryConfigChecks(mod, Assembly.LoadFrom(Path.Combine(folders[0], "assembly_valheim.dll")));
        // Player's new default-interface methods cannot be loaded by .NET Framework.
        // Validate its method metadata separately; open delegate execution needs Unity/Mono.
        Check(mod.GetType("ServerSyncModTemplate.JotunnBridge") == null, "Removed Jotunn bridge");
        Check(!mod.GetType("ServerSyncModTemplate.ServerSyncModTemplatePlugin")!.GetCustomAttributesData()
            .Any(a => a.AttributeType.Name == "BepInDependency" && (string)a.ConstructorArguments[0].Value! == "com.jotunn.jotunn"), "No Jotunn loader dependency");
        CloneApiChecks(mod, Assembly.LoadFrom(Path.GetFullPath(args[1])));
        System.Console.WriteLine($"ServeYouRight checks passed: {Passed}. No Unity/native game execution performed.");
    }

    private static void OwnerChecks()
    {
        var a = new AssetOwner("a.food", "Same Name", "FoodA", new[] { "A.Resources.foods" });
        var b = new AssetOwner("b.food", "Same Name", "FoodB", new[] { "B.Resources.foods" });
        Check(AssetOwnerMatching.Resolve("foods", new[] { a }) == a, "Resource owner retains identity");
        Check(AssetOwnerMatching.Resolve("foods", new[] { a, b }) == null, "Duplicate resource with different GUID is ambiguous");
        Check(AssetOwnerMatching.Resolve("foods", new[] { b, a }) == null, "No load-order winner");
        Check(AssetOwnerMatching.Resolve("food", new[] { a }) == null, "Substring rejected");
        Check(AssetOwnerMatching.Resolve("FoodA.bundle", new[] { a }) == a, "Unique complete assembly token");
        Check(AssetOwnerMatching.Resolve("SameName", new[] { a, b }) == null, "Display name collision is not GUID identity");
        var c = new AssetOwner("a.food", "Renamed", "Other", new[] { "foods" });
        Check(AssetOwnerMatching.Resolve("foods", new[] { a, c })?.Guid == a.Guid, "Same GUID is one owner");
        Check(AssetOwnerMatching.Resolve("foods", new[] { new AssetOwner("x", "x", "x", new[] { "notfoods" }) }) == null, "Resource suffix needs a boundary");
        var owners = new Dictionary<string, AssetOwner>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AssetOwnerMatching.Add(owners, ambiguous, "Soup", a);
        AssetOwnerMatching.Add(owners, ambiguous, "SOUP", b);
        AssetOwnerMatching.Add(owners, ambiguous, "soup", a);
        Check(owners.Count == 0 && ambiguous.Contains("Soup"), "Asset ambiguity survives later insertions with same display name");
        Check(AssetOwnerMatching.Resolve("", new[] { a }) == null, "Empty input");
    }

    private static object? Invoke(Type type, string name, params object?[] args) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    private static void Set(Type type, string name, object? value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);

    private static void MenuScopeChecks(string pluginPath)
    {
        using var module = ModuleDefinition.ReadModule(pluginPath);
        IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) =>
            types.SelectMany(type => new[] { type }.Concat(Types(type.NestedTypes)));
        var members = Types(module.Types).SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions).Select(instruction => instruction.Operand).OfType<MemberReference>().ToArray();
        Check(!members.Any(member => member.DeclaringType?.FullName == "PieceTable" && member.Name == "m_canRemoveFeasts"),
            "Food injection and UI never use mutable removal capability to identify Feaster");
        Check(!members.Any(member => member.DeclaringType?.FullName == "BuildUi" && member.Name == "m_tabButtons") &&
            !members.Any(member => member.DeclaringType?.FullName == "TabHandler"),
            "ServeYouRight does not add, hide or intercept top-level tabs");
    }

    private static void MenuSlotChecks(Assembly mod, Assembly game)
    {
        Type registrationType = mod.GetType("ServerSyncModTemplate.ServingTrayMenu+Registration", true)!;
        object registration = Activator.CreateInstance(registrationType, true)!;
        Type listType = typeof(List<>).MakeGenericType(game.GetType("IPieceList", true)!);
        var lists = (IList)Activator.CreateInstance(listType)!;
        object categories = Activator.CreateInstance(game.GetType("ByUsagePieceList", true)!, "$hud_byusage")!;
        object recent = Activator.CreateInstance(game.GetType("RecentPieceList", true)!, "$hud_recent", 50)!;
        object otherMod = Activator.CreateInstance(game.GetType("RecentPieceList", true)!, "Other mod", 10)!;
        MethodInfo use = registrationType.GetMethod("UseCategories", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo restore = registrationType.GetMethod("RestoreCategories", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool Use() => (bool)use.Invoke(registration, new object[] { lists })!;
        void Restore() => restore.Invoke(registration, new object[] { lists });

        lists.Add(categories); lists.Add(recent); lists.Add(otherMod);
        Check(Use(), "Feaster reuses the original Categories slot");
        object grouped = lists[0]!;
        Check(lists.Count == 3 && ReferenceEquals(lists[1], recent) && ReferenceEquals(lists[2], otherMod), "No new tab or changes to other lists");
        Check(!ReferenceEquals(grouped, categories) && (string)grouped.GetType().GetProperty("DisplayName")!.GetValue(grouped)! == "$hud_byusage", "Preserves the existing Categories label");
        Check(Use() && lists.Count == 3 && ReferenceEquals(lists[0], grouped), "Repeated activation does not duplicate a list");
        Restore();
        Check(ReferenceEquals(lists[0], categories) && lists.Count == 3, "Close/tool switch restores the exact original list");
        Restore();
        Check(ReferenceEquals(lists[0], categories), "Repeated cleanup is harmless");

        Check(Use(), "Reopen activates grouping again");
        lists.Insert(0, otherMod);
        Restore();
        Check(ReferenceEquals(lists[0], otherMod) && ReferenceEquals(lists[1], categories) && ReferenceEquals(lists[2], recent), "Restore follows list identity when another mod inserts before it");
        Check(Use(), "Categories may live at a different index");
        lists[1] = otherMod;
        Restore();
        Check(ReferenceEquals(lists[1], otherMod), "Cleanup does not overwrite another mod's replacement");
        Check(!Use() && lists.Count == 4, "No recognized Categories list leaves the menu untouched");
        lists.Clear();
        Check(!Use() && lists.Count == 0, "Uninitialized menu remains untouched");
    }

    private static void CategoryConfigChecks(Assembly mod, Assembly game)
    {
        Type plugin = mod.GetType("ServerSyncModTemplate.ServerSyncModTemplatePlugin", true)!;
        Type toggle = mod.GetType("ServerSyncModTemplate.ServerSyncModTemplatePlugin+Toggle", true)!;
        Type sourceType = mod.GetType("ServerSyncModTemplate.FoodSourceMod", true)!;
        Type configType = Type.GetType("BepInEx.Configuration.ConfigFile, BepInEx", true)!;
        object file = Activator.CreateInstance(configType, Path.Combine(Path.GetTempPath(), "ServeYouRight-checks-" + Guid.NewGuid() + ".cfg"), false, null)!;
        configType.GetProperty("SaveOnConfigSet")!.SetValue(file, false);
        MethodInfo bind = configType.GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition &&
            m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType == typeof(string)).MakeGenericMethod(toggle);
        string[] names = { "Misc", "Food", "Meads", "Feasts" };
        object on = Enum.Parse(toggle, "On"), off = Enum.Parse(toggle, "Off");
        object[] entries = names.Select(name => bind.Invoke(file, new[] { "ServingTray - Test (test.food)", name, on, "" })!).ToArray();
        object config = Activator.CreateInstance(mod.GetType("ServerSyncModTemplate.PerModCategoryConfig", true)!, entries)!;
        var configurations = (IDictionary)plugin.GetField("PerModConfigs", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object source = Activator.CreateInstance(sourceType, "test.food", "Test")!;
        Type category = game.GetType("Piece+PieceCategory", true)!;
        configurations.Add("test.food", config);
        try
        {
            for (int i = 0; i < names.Length; i++)
            {
                object value = Enum.Parse(category, names[i]);
                Check((bool)Invoke(plugin, "UseModSpecificTab", source, value)!, names[i] + " On uses its mod group");
                entries[i].GetType().GetProperty("Value")!.SetValue(entries[i], off);
                Check(!(bool)Invoke(plugin, "UseModSpecificTab", source, value)!, names[i] + " Off merges into its base group");
                entries[i].GetType().GetProperty("Value")!.SetValue(entries[i], on);
            }
            Check(!(bool)Invoke(plugin, "UseModSpecificTab", source, Enum.Parse(category, "Crafting"))!, "Unrelated categories are not assigned the food grouping policy");
        }
        finally { configurations.Remove("test.food"); }
    }

    private static void CloneApiChecks(Assembly mod, Assembly dataForge)
    {
        Type catalog = mod.GetType("ServerSyncModTemplate.FoodSourceCatalog", true)!;
        Type api = dataForge.GetType("DataForge.DataForgeApi", true)!;
        Type domain = dataForge.GetType("DataForge.DataForgeDomain", true)!;
        object items = Enum.Parse(domain, "Items");
        Invoke(api, "Shutdown");
        Invoke(catalog, "RefreshCloneSnapshot");
        Check(!Lookup(catalog, "SoupClone"), "Absent optional API returns unknown");
        Set(catalog, "_getState", api.GetMethod("GetState"));
        Set(catalog, "_getCloneSources", api.GetMethod("GetCloneSources"));
        Set(catalog, "_itemsDomain", items);
        EventInfo changed = api.GetEvent("Changed")!;
        Type changeType = changed.EventHandlerType!.GetGenericArguments()[0];
        MethodInfo callback = catalog.GetMethod("OnChanged", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(changeType);
        Delegate handler = Delegate.CreateDelegate(changed.EventHandlerType, callback);
        changed.AddEventHandler(null, handler);
        Set(catalog, "_changed", changed); Set(catalog, "_handler", handler);
        Invoke(catalog, "RefreshCloneSnapshot");
        Check(!Lookup(catalog, "SoupClone"), "Reset state not ready");
        Apply(api, items, true, new Dictionary<string, string> { ["SoupClone"] = "Soup" });
        Invoke(api, "DispatchPending");
        Check((bool)catalog.GetField("RefreshPending", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!, "Actual reflected Changed delegate queues refresh");
        Invoke(catalog, "RefreshCloneSnapshot");
        Check(Lookup(catalog, "SoupClone"), "Managed clone found in merged API snapshot");
        Apply(api, items, false, null);
        Invoke(catalog, "RefreshCloneSnapshot");
        Check(!Lookup(catalog, "SoupClone"), "Failed apply invalidates positive clone snapshot");
        Apply(api, items, true, new Dictionary<string, string> { ["SoupClone"] = "Soup" });
        Invoke(catalog, "RefreshCloneSnapshot");
        Invoke(api, "ResetSession", false);
        Invoke(catalog, "RefreshCloneSnapshot");
        Check(!Lookup(catalog, "SoupClone"), "World reset invalidates clone snapshot");
        Invoke(catalog, "Dispose");
        Apply(api, items, true, null); Invoke(api, "DispatchPending");
        Check(!(bool)catalog.GetField("RefreshPending", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!, "Dispose unsubscribes event");
    }

    private static bool Lookup(Type catalog, string name)
    {
        object?[] args = { name, null };
        bool result = (bool)Invoke(catalog, "TryGetOwner", args)!;
        if (result) Check((string)args[1]!.GetType().GetProperty("Id")!.GetValue(args[1])! == "sighsorry.DataForge", "Clone belongs to creator, not source item name");
        return result;
    }

    private static void Apply(Type api, object domain, bool complete, Dictionary<string, string>? clones)
    {
        object scope = Invoke(api, "BeginApply", domain, false, null, true, null)!;
        if (complete) scope.GetType().GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(scope, new object?[] { null, clones });
        ((IDisposable)scope).Dispose();
    }
}
