using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using ModAssetOwnership;
using UnityEngine;

namespace ServerSyncModTemplate;

internal static class FoodSourceCatalog
{
    private const string DataForgeGuid = "sighsorry.DataForge";
    private static readonly Dictionary<string, AssetOwner> Owners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Ambiguous = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, BundleContents> Bundles = new();
    private static readonly HashSet<string> Vanilla = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DataForgeClones = new(StringComparer.OrdinalIgnoreCase);
    private static bool _vanillaLoaded;
    private static MethodInfo? _getCloneSources, _getState;
    private static object? _itemsDomain;
    private static EventInfo? _changed;
    private static Delegate? _handler;
    private static string _dataForgeName = "DataForge";
    internal static bool RefreshPending;

    private sealed class BundleContents
    {
        internal readonly AssetBundle Bundle;
        internal readonly string Name;
        internal readonly string[] Paths;
        internal BundleContents(AssetBundle bundle)
        { Bundle = bundle; Name = bundle.name; Paths = bundle.GetAllAssetNames(); }
    }

    internal static void Initialize()
    {
        if (!Chainloader.PluginInfos.TryGetValue(DataForgeGuid, out var plugin)) return;
        try
        {
            Type? api = plugin.Instance.GetType().Assembly.GetType("DataForge.DataForgeApi");
            Type? domain = api?.Assembly.GetType("DataForge.DataForgeDomain");
            if (api == null || domain == null || (int?)api.GetProperty("ApiVersion")?.GetValue(null) != 1) return;
            _itemsDomain = Enum.Parse(domain, "Items");
            _getCloneSources = api.GetMethod("GetCloneSources", new[] { domain });
            _getState = api.GetMethod("GetState", new[] { domain });
            _changed = api.GetEvent("Changed");
            if (_getCloneSources == null || _getState == null || _changed?.EventHandlerType == null) return;
            Type changeType = _changed.EventHandlerType.GetGenericArguments()[0];
            MethodInfo callback = typeof(FoodSourceCatalog).GetMethod(nameof(OnChanged), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(changeType);
            _handler = Delegate.CreateDelegate(_changed.EventHandlerType, callback);
            _changed.AddEventHandler(null, _handler);
            _dataForgeName = string.IsNullOrWhiteSpace(plugin.Metadata.Name) ? DataForgeGuid : plugin.Metadata.Name;
        }
        catch (Exception ex) { LogFailure("DataForge API binding", ex); }
    }

    private static void OnChanged<T>(T change)
    {
        if (change != null && change.GetType().GetProperty("Domain")?.GetValue(change)?.ToString() == "Items")
            RefreshPending = true; // Defer until application/event dispatch has unwound on the Unity thread.
    }

    internal static void Refresh()
    {
        List<AssetOwner> plugins = new();
        foreach (var info in Chainloader.PluginInfos.Values)
        {
            try
            {
                Assembly? assembly = info.Instance?.GetType().Assembly;
                if (assembly != null) plugins.Add(new AssetOwner(info.Metadata.GUID, info.Metadata.Name,
                    assembly.GetName().Name ?? "", assembly.GetManifestResourceNames()));
            }
            catch (Exception ex) { LogFailure("Plugin resource lookup", ex); }
        }
        foreach (AssetBundle bundle in AssetBundle.GetAllLoadedAssetBundles())
        {
            int id = bundle.GetInstanceID();
            if (Bundles.TryGetValue(id, out BundleContents previous) && ReferenceEquals(previous.Bundle, bundle)) continue;
            try { Bundles[id] = new BundleContents(bundle); }
            catch (Exception ex) { LogFailure("Bundle asset lookup", ex); }
        }
        Owners.Clear();
        Ambiguous.Clear();
        // Keep observed paths through bundle.Unload(false), while its prefabs remain alive.
        // The catalog is discarded at world shutdown, never serialized or networked.
        foreach (BundleContents bundle in Bundles.Values)
        {
            AssetOwner? owner = AssetOwnerMatching.Resolve(bundle.Name, plugins);
            if (owner == null) continue;
            foreach (string path in bundle.Paths)
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    AssetOwnerMatching.Add(Owners, Ambiguous, Path.GetFileNameWithoutExtension(path), owner);
        }
        LoadVanillaNames();
        RefreshCloneSnapshot();
    }

    private static void LoadVanillaNames()
    {
        if (_vanillaLoaded) return;
        string path = Path.Combine(Application.dataPath, "StreamingAssets", "SoftRef", "manifest_extended");
        if (!File.Exists(path)) return;
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                const string marker = "path in bundle:";
                int index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                string assetPath = line.Substring(index + marker.Length).Trim();
                if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) Vanilla.Add(Path.GetFileNameWithoutExtension(assetPath));
            }
            _vanillaLoaded = true;
        }
        catch (Exception ex) { LogFailure("Vanilla asset lookup", ex); }
    }

    private static void RefreshCloneSnapshot()
    {
        DataForgeClones.Clear();
        if (_getState == null || _getCloneSources == null || _itemsDomain == null) return;
        try
        {
            object[] args = { _itemsDomain };
            object? state = _getState.Invoke(null, args);
            if (state?.GetType().GetProperty("IsReady")?.GetValue(state) is not true) return;
            if (_getCloneSources.Invoke(null, args) is not IEnumerable clones) return;
            foreach (object pair in clones)
                if (pair.GetType().GetProperty("Key")?.GetValue(pair) is string key) DataForgeClones.Add(key);
        }
        catch (Exception ex) { LogFailure("DataForge clone lookup", ex); }
    }

    internal static bool TryGetOwner(string name, out FoodSourceMod source)
    {
        if (DataForgeClones.Contains(name)) { source = new FoodSourceMod(DataForgeGuid, _dataForgeName); return true; }
        if (!Vanilla.Contains(name) && Owners.TryGetValue(name, out AssetOwner owner))
        { source = new FoodSourceMod(owner.Guid, owner.Name); return true; }
        source = default;
        return false;
    }

    internal static void ResetWorld()
    { Owners.Clear(); Ambiguous.Clear(); Bundles.Clear(); DataForgeClones.Clear(); RefreshPending = false; }

    internal static void Dispose()
    {
        try { if (_handler != null) _changed?.RemoveEventHandler(null, _handler); }
        catch (Exception ex) { LogFailure("DataForge event cleanup", ex); }
        _handler = null; _changed = null; _getCloneSources = null; _getState = null; _itemsDomain = null;
        ResetWorld();
    }

    private static void LogFailure(string operation, Exception ex) =>
        ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogDebug($"{operation} unavailable: {ex.GetBaseException().Message}");
}
