﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.HardDependency)]
public class ServerSyncModTemplatePlugin : BaseUnityPlugin
{
    internal const string ModName = "ServeYouRight";
    internal const string ModVersion = "1.0.4";
    internal const string Author = "sighsorry";
    private const string ModGUID = $"{Author}.{ModName}";
    private static string ConfigFileName = $"{ModGUID}.cfg";
    private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    private static ServerSyncModTemplatePlugin? _instance;
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource ServerSyncModTemplateLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private FileSystemWatcher? _watcher;
    private readonly object _reloadLock = new();
    private static readonly object DynamicConfigLock = new();
    private DateTime _lastConfigReloadTime;
    private static readonly TimeSpan ConfigReloadDelay = TimeSpan.FromSeconds(1);
    private static readonly Dictionary<string, PerModCategoryConfig> PerModConfigs = new(StringComparer.OrdinalIgnoreCase);
    private static bool _pendingDynamicConfigSave;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        _instance = this;
        RunWithConfigAutoSaveDisabled(() =>
        {
            JotunnBridge.InitializeAndEnableModQuery();
            Localization.OnLanguageChange += OnLanguageChange;

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SaveConfigWithoutWatcher();
            SetupWatcher();
        });
    }

    private void OnDestroy()
    {
        Localization.OnLanguageChange -= OnLanguageChange;
        _watcher?.Dispose();
        _watcher = null;
        RunWithConfigAutoSaveDisabled(SaveConfigWithoutWatcher);
    }

    private void OnLanguageChange()
    {
        FeasterFoodInjector.RefreshFoodPiecesAndMenu(ObjectDB.instance);
    }

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.IncludeSubdirectories = true;
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastConfigReloadTime < ConfigReloadDelay)
        {
            return;
        }

        _lastConfigReloadTime = now;
        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                ServerSyncModTemplateLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                ServerSyncModTemplateLogger.LogDebug("Reloading configuration...");
                ReloadConfigValues();
                ServerSyncModTemplateLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                ServerSyncModTemplateLogger.LogError($"Error reloading configuration: {ex.Message}");
            }
            finally
            {
                _lastConfigReloadTime = DateTime.UtcNow;
            }
        }
    }

    private void ReloadConfigValues()
    {
        RunWithConfigAutoSaveDisabled(() =>
        {
            Config.Reload();
            FeasterFoodInjector.RefreshFoodPiecesAndMenu(ObjectDB.instance);
        });
    }

    private void RunWithConfigAutoSaveDisabled(Action action)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            action();
        }
        finally
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
    }

    private void SaveConfigWithoutWatcher()
    {
        FileSystemWatcher? watcher = _watcher;
        bool watcherWasEnabled = watcher?.EnableRaisingEvents == true;
        if (watcherWasEnabled)
        {
            watcher!.EnableRaisingEvents = false;
        }

        try
        {
            Config.Save();
        }
        finally
        {
            if (watcherWasEnabled && watcher != null)
            {
                watcher.EnableRaisingEvents = true;
            }
        }
    }


    #region ConfigOptions

    internal static bool UseModSpecificTab(FoodSourceMod mod, Piece.PieceCategory category)
    {
        PerModCategoryConfig cfg = GetOrCreatePerModConfig(mod);
        return category switch
        {
            Piece.PieceCategory.Food => cfg.Food.Value == Toggle.On,
            Piece.PieceCategory.Meads => cfg.Meads.Value == Toggle.On,
            Piece.PieceCategory.Feasts => cfg.Feasts.Value == Toggle.On,
            _ => false
        };
    }

    private static PerModCategoryConfig GetOrCreatePerModConfig(FoodSourceMod mod)
    {
        lock (DynamicConfigLock)
        {
            if (PerModConfigs.TryGetValue(mod.Id, out PerModCategoryConfig? existing))
            {
                return existing;
            }

            if (_instance == null)
            {
                throw new InvalidOperationException("Plugin instance is not initialized.");
            }

            string section = $"ServingTray - {SanitizeConfigText(mod.DisplayName)} ({SanitizeConfigText(mod.Id)})";

            PerModCategoryConfig created = new(
                _instance.config(section, "Food", Toggle.On, $"If on, '{mod.DisplayName}' Food items go to 'Food - {mod.DisplayName}'. If off, they merge into vanilla Food.", 300),
                _instance.config(section, "Meads", Toggle.On, $"If on, '{mod.DisplayName}' Mead items go to 'Meads - {mod.DisplayName}'. If off, they merge into vanilla Meads.", 200),
                _instance.config(section, "Feasts", Toggle.On, $"If on, '{mod.DisplayName}' Feast items go to 'Feasts - {mod.DisplayName}'. If off, they merge into vanilla Feasts.", 100)
            );

            PerModConfigs[mod.Id] = created;
            _pendingDynamicConfigSave = true;
            return created;
        }
    }

    internal static void FlushPendingDynamicConfigSave()
    {
        ServerSyncModTemplatePlugin? instance = _instance;
        if (instance == null)
        {
            return;
        }

        bool shouldSave;
        lock (DynamicConfigLock)
        {
            shouldSave = _pendingDynamicConfigSave;
            _pendingDynamicConfigSave = false;
        }

        if (!shouldSave)
        {
            return;
        }

        instance.RunWithConfigAutoSaveDisabled(instance.SaveConfigWithoutWatcher);
    }

    private static string SanitizeConfigText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Unknown";
        }

        // BepInEx section/key restrictions: = \n \t \ " ' [ ]
        const string disallowed = "=\n\t\\\"'[]";
        char[] buffer = text.ToCharArray();
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '\r' || disallowed.IndexOf(buffer[i]) >= 0 || char.IsControl(buffer[i]))
            {
                buffer[i] = '_';
            }
        }

        string sanitized = new string(buffer).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, int order)
    {
        return Config.Bind(
            group,
            name,
            value,
            new ConfigDescription(
                description,
                null,
                new ConfigurationManagerAttributes
                {
                    Order = order
                }
            )
        );
    }

    private class ConfigurationManagerAttributes
    {
        public int? Order = null!;
    }

#endregion
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
public static class ObjectDbAwakePatch
{
    private static void Postfix(ObjectDB __instance)
    {
        FeasterFoodInjector.Inject(__instance);
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
public static class ObjectDbCopyOtherDbPatch
{
    private static void Postfix(ObjectDB __instance)
    {
        FeasterFoodInjector.Inject(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.SetPlaceMode))]
public static class PlayerSetPlaceModePatch
{
    private static void Postfix(Player __instance, PieceTable buildPieces)
    {
        if (__instance != Player.m_localPlayer || buildPieces == null || !buildPieces.m_canRemoveFeasts)
        {
            return;
        }

        FeasterFoodInjector.RefreshFoodPiecesAndMenu(ObjectDB.instance, buildPieces);
    }
}

[HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
public static class PieceTableUpdateAvailablePatch
{
    private static void Postfix(PieceTable __instance)
    {
        if (__instance == null || !__instance.m_canRemoveFeasts)
        {
            return;
        }

        FeasterFoodInjector.ApplyCustomCategoryLabels(__instance);
    }
}

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.MakePiece))]
public static class ItemDropMakePiecePatch
{
    private static void Postfix(ItemDrop __instance)
    {
        if (__instance.GetComponent<ServeYouRightInjectedPieceMarker>() == null)
        {
            return;
        }

        foreach (ParticleSystem particleSystem in __instance.GetComponentsInChildren<ParticleSystem>(true))
        {
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Clear(true);

            ParticleSystem.EmissionModule emission = particleSystem.emission;
            emission.enabled = false;

            ParticleSystem.MainModule main = particleSystem.main;
            main.playOnAwake = false;

            ParticleSystemRenderer? renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.enabled = false;
            }
        }
    }
}

internal static class FeasterFoodInjector
{
    private static bool _isInjecting;
    private static readonly Dictionary<string, ModCategoryInfo> ModCategoryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FoodSourceMod> SourceModHitCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ModCategoryLock = new();

    internal static void RefreshFoodPiecesAndMenu(ObjectDB? objectDb, PieceTable? buildPieces = null)
    {
        Inject(objectDb);
        Player? localPlayer = Player.m_localPlayer;
        if (localPlayer != null)
        {
            localPlayer.UpdateKnownRecipesList();
            localPlayer.UpdateAvailablePiecesList();
            ApplyCustomCategoryLabels(buildPieces ?? localPlayer.m_buildPieces);
        }
    }

    public static void Inject(ObjectDB? objectDb)
    {
        if (_isInjecting || objectDb == null)
        {
            return;
        }

        _isInjecting = true;
        try
        {
            List<CandidateFood> candidateFoods = GetCandidateFoods(objectDb);
            if (candidateFoods.Count == 0)
            {
                return;
            }

            foreach (PieceTable feasterTable in GetFeasterPieceTables(objectDb))
            {
                int added = InjectIntoTable(feasterTable, candidateFoods);
                if (added > 0)
                {
                    ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogInfo($"Added {added} mod food pieces to feaster table '{feasterTable.name}'.");
                }

                ApplyCustomCategoryLabels(feasterTable);
            }
        }
        catch (Exception ex)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogError($"Failed to inject feaster foods: {ex}");
        }
        finally
        {
            _isInjecting = false;
            ServerSyncModTemplatePlugin.FlushPendingDynamicConfigSave();
        }
    }

    private static List<CandidateFood> GetCandidateFoods(ObjectDB objectDb)
    {
        List<CandidateFood> candidates = new();
        FeastRoutingData feastRouting = BuildFeastRoutingData(objectDb);

        foreach (GameObject itemPrefab in objectDb.m_items)
        {
            if (itemPrefab == null)
            {
                continue;
            }

            ItemDrop? itemDrop = itemPrefab.GetComponent<ItemDrop>();
            ItemDrop.ItemData.SharedData? shared = itemDrop?.m_itemData?.m_shared;
            if (itemDrop == null || shared == null)
            {
                continue;
            }

            string prefabName = Utils.GetPrefabName(itemPrefab.name);

            // Place feast result via material items: material is the requirement, result is the placed prefab.
            if (feastRouting.MaterialToResultPrefab.TryGetValue(prefabName, out GameObject feastResultPrefab))
            {
                candidates.Add(new CandidateFood(itemDrop, feastResultPrefab, Piece.PieceCategory.Feasts));
                continue;
            }

            // Feast result prefabs must not be exposed as direct placement entries.
            if (feastRouting.ResultPrefabNames.Contains(prefabName))
            {
                continue;
            }

            // Unresolved feast-like prefabs should not become direct place targets.
            if (itemDrop.GetComponent<Feast>() != null)
            {
                continue;
            }

            if (shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable)
            {
                continue;
            }

            if (!LooksLikeConsumableFood(shared))
            {
                continue;
            }

            Piece.PieceCategory category = shared.m_isDrink ? Piece.PieceCategory.Meads : Piece.PieceCategory.Food;
            candidates.Add(new CandidateFood(itemDrop, itemDrop.gameObject, category));
        }

        return candidates;
    }

    private static FeastRoutingData BuildFeastRoutingData(ObjectDB objectDb)
    {
        Dictionary<string, ItemDrop> itemDropsByPrefab = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject itemPrefab in objectDb.m_items)
        {
            if (itemPrefab == null)
            {
                continue;
            }

            ItemDrop? itemDrop = itemPrefab.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                continue;
            }

            string prefabName = Utils.GetPrefabName(itemPrefab.name);
            if (!itemDropsByPrefab.ContainsKey(prefabName))
            {
                itemDropsByPrefab[prefabName] = itemDrop;
            }
        }

        Dictionary<string, GameObject> materialToResultPrefab = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> resultPrefabNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, ItemDrop> entry in itemDropsByPrefab)
        {
            string selfPrefabName = entry.Key;
            ItemDrop itemDrop = entry.Value;
            ItemDrop.ItemData.SharedData? shared = itemDrop.m_itemData?.m_shared;
            Feast? feast = itemDrop.GetComponent<Feast>();
            if (feast == null || shared == null)
            {
                continue;
            }

            ItemDrop? linkedFood = feast.m_foodItem;
            string linkedFoodPrefabName = linkedFood != null ? Utils.GetPrefabName(linkedFood.gameObject.name) : string.Empty;
            bool hasDifferentLinkedFood = !string.IsNullOrWhiteSpace(linkedFoodPrefabName) &&
                                          !string.Equals(linkedFoodPrefabName, selfPrefabName, StringComparison.OrdinalIgnoreCase);
            ItemDrop.ItemData.SharedData? linkedShared = linkedFood?.m_itemData?.m_shared;
            bool linkedLooksLikeResult = linkedFood != null &&
                                         (linkedFood.GetComponent<Feast>() != null ||
                                          (linkedShared != null && (linkedShared.m_itemType == ItemDrop.ItemData.ItemType.Consumable || LooksLikeConsumableFood(linkedShared))));

            if (shared.m_itemType == ItemDrop.ItemData.ItemType.Material)
            {
                if (hasDifferentLinkedFood && linkedLooksLikeResult)
                {
                    materialToResultPrefab[selfPrefabName] = linkedFood!.gameObject;
                    resultPrefabNames.Add(linkedFoodPrefabName);
                }

                continue;
            }

            if (hasDifferentLinkedFood && linkedLooksLikeResult && shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable && !LooksLikeConsumableFood(shared))
            {
                materialToResultPrefab[selfPrefabName] = linkedFood!.gameObject;
                resultPrefabNames.Add(linkedFoodPrefabName);
                continue;
            }

            resultPrefabNames.Add(selfPrefabName);
        }

        foreach (KeyValuePair<string, ItemDrop> entry in itemDropsByPrefab)
        {
            string selfPrefabName = entry.Key;
            ItemDrop itemDrop = entry.Value;
            ItemDrop.ItemData.SharedData? shared = itemDrop.m_itemData?.m_shared;
            if (shared == null || shared.m_itemType != ItemDrop.ItemData.ItemType.Material)
            {
                continue;
            }

            ItemDrop? appendToolTip = shared.m_appendToolTip;
            if (appendToolTip == null)
            {
                continue;
            }

            string tooltipPrefabName = Utils.GetPrefabName(appendToolTip.gameObject.name);
            if (string.Equals(tooltipPrefabName, selfPrefabName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool tooltipIsFeastResult = resultPrefabNames.Contains(tooltipPrefabName) || appendToolTip.GetComponent<Feast>() != null;
            if (!tooltipIsFeastResult)
            {
                continue;
            }

            materialToResultPrefab[selfPrefabName] = appendToolTip.gameObject;
            resultPrefabNames.Add(tooltipPrefabName);
        }

        return new FeastRoutingData(materialToResultPrefab, resultPrefabNames);
    }

    private static bool LooksLikeConsumableFood(ItemDrop.ItemData.SharedData shared)
    {
        return shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f || shared.m_isDrink;
    }

    private static IEnumerable<PieceTable> GetFeasterPieceTables(ObjectDB objectDb)
    {
        HashSet<PieceTable> uniqueTables = new();
        foreach (GameObject itemPrefab in objectDb.m_items)
        {
            if (itemPrefab == null)
            {
                continue;
            }

            ItemDrop? itemDrop = itemPrefab.GetComponent<ItemDrop>();
            PieceTable? pieceTable = itemDrop?.m_itemData?.m_shared?.m_buildPieces;
            if (pieceTable == null)
            {
                continue;
            }

            if (!pieceTable.m_canRemoveFeasts)
            {
                continue;
            }

            bool supportsFoodTabs = pieceTable.m_categories.Contains(Piece.PieceCategory.Food) ||
                                    pieceTable.m_categories.Contains(Piece.PieceCategory.Meads) ||
                                    pieceTable.m_categories.Contains(Piece.PieceCategory.Feasts);
            if (!supportsFoodTabs)
            {
                continue;
            }

            if (uniqueTables.Add(pieceTable))
            {
                yield return pieceTable;
            }
        }
    }

    private static int InjectIntoTable(PieceTable table, List<CandidateFood> candidates)
    {
        EnsureBaseCategories(table);
        HashSet<int> existingFoodHashes = BuildExistingFoodHashSet(table);
        int added = 0;

        foreach (CandidateFood candidateFood in candidates)
        {
            ItemDrop sourceItemDrop = candidateFood.ItemDrop;
            GameObject placePrefab = candidateFood.PlacePrefab;
            int foodHash = GetPrefabNameHash(sourceItemDrop.gameObject);
            bool existsInTable = existingFoodHashes.Contains(foodHash);
            bool injectedByServeYouRight = placePrefab.GetComponent<ServeYouRightInjectedPieceMarker>() != null;
            if (existsInTable && !injectedByServeYouRight)
            {
                continue;
            }

            Piece.PieceCategory baseCategory = candidateFood.Category;
            Piece.PieceCategory targetCategory = ResolveTargetCategory(table, sourceItemDrop.gameObject, baseCategory);

            if (!TryGetTemplate(table, targetCategory, out Piece templatePiece, out WearNTear templateWearNTear))
            {
                continue;
            }

            if (!PrepareFoodPrefab(placePrefab, sourceItemDrop, targetCategory, templatePiece, templateWearNTear))
            {
                continue;
            }

            if (!table.m_pieces.Contains(placePrefab))
            {
                table.m_pieces.Add(placePrefab);
                existingFoodHashes.Add(foodHash);
                added++;
            }
        }

        return added;
    }

    private static void EnsureBaseCategories(PieceTable table)
    {
        EnsureBaseCategoryExists(table, Piece.PieceCategory.Food, GetBaseCategoryLabel(table, Piece.PieceCategory.Food));
        EnsureBaseCategoryExists(table, Piece.PieceCategory.Meads, GetBaseCategoryLabel(table, Piece.PieceCategory.Meads));
        EnsureBaseCategoryExists(table, Piece.PieceCategory.Feasts, GetBaseCategoryLabel(table, Piece.PieceCategory.Feasts));
    }

    public static void ApplyCustomCategoryLabels(PieceTable? table)
    {
        if (table == null)
        {
            return;
        }

        List<ModCategoryInfo> infos;
        lock (ModCategoryLock)
        {
            infos = ModCategoryCache.Values.ToList();
        }

        foreach (ModCategoryInfo info in infos)
        {
            int categoryIndex = table.m_categories.IndexOf(info.Category);
            if (categoryIndex < 0)
            {
                continue;
            }

            while (table.m_categoryLabels.Count <= categoryIndex)
            {
                table.m_categoryLabels.Add(string.Empty);
            }

            string baseLabel = GetBaseCategoryLabel(table, info.BaseCategory);
            string localizedCategory = ResolveCategoryDisplayLabel(info.BaseCategory, baseLabel);
            table.m_categoryLabels[categoryIndex] = $"{localizedCategory} - {info.SourceMod.DisplayName}";
        }
    }

    private static bool TryGetTemplate(PieceTable table, Piece.PieceCategory category, out Piece templatePiece, out WearNTear templateWearNTear)
    {
        templatePiece = null!;
        templateWearNTear = null!;
        Piece? fallbackPiece = null;
        WearNTear? fallbackWearNTear = null;

        foreach (GameObject piecePrefab in table.m_pieces)
        {
            if (piecePrefab == null)
            {
                continue;
            }

            Piece? piece = piecePrefab.GetComponent<Piece>();
            if (piece == null || piece.m_repairPiece || piece.m_removePiece)
            {
                continue;
            }

            WearNTear? wearNTear = piecePrefab.GetComponent<WearNTear>();
            if (wearNTear == null)
            {
                continue;
            }

            if (piece.m_category == category)
            {
                templatePiece = piece;
                templateWearNTear = wearNTear;
                return true;
            }

            if (fallbackPiece == null)
            {
                fallbackPiece = piece;
                fallbackWearNTear = wearNTear;
            }
        }

        if (fallbackPiece != null)
        {
            templatePiece = fallbackPiece;
            templateWearNTear = fallbackWearNTear!;
            return true;
        }

        ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"No feaster template piece found for table '{table.name}'.");
        return false;
    }

    private static bool PrepareFoodPrefab(GameObject foodPrefab, ItemDrop sourceItemDrop, Piece.PieceCategory category, Piece templatePiece, WearNTear templateWearNTear)
    {
        if (foodPrefab.GetComponent<ZNetView>() == null)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"Skipping '{foodPrefab.name}' because it has no ZNetView.");
            return false;
        }

        Piece? piece = foodPrefab.GetComponent<Piece>();
        if (piece == null)
        {
            piece = foodPrefab.AddComponent<Piece>();
            CopyPieceTemplate(templatePiece, piece);
        }

        WearNTear? wearNTear = foodPrefab.GetComponent<WearNTear>();
        if (wearNTear == null)
        {
            wearNTear = foodPrefab.AddComponent<WearNTear>();
            CopyWearNTearTemplate(templateWearNTear, wearNTear);
        }

        ItemDrop.ItemData.SharedData shared = sourceItemDrop.m_itemData.m_shared;
        piece.m_name = shared.m_name;
        piece.m_description = shared.m_description;
        piece.m_enabled = true;
        piece.m_category = category;
        piece.m_repairPiece = false;
        piece.m_removePiece = false;
        piece.m_resources = new[]
        {
            new Piece.Requirement
            {
                m_resItem = sourceItemDrop,
                m_amount = 1,
                m_amountPerLevel = 1,
                m_recover = true
            }
        };

        if (shared.m_icons is { Length: > 0 })
        {
            piece.m_icon = sourceItemDrop.m_itemData.GetIcon();
        }

        if (foodPrefab.GetComponent<ServeYouRightInjectedPieceMarker>() == null)
        {
            foodPrefab.AddComponent<ServeYouRightInjectedPieceMarker>();
        }

        return true;
    }

    private static void CopyPieceTemplate(Piece source, Piece destination)
    {
        destination.m_targetNonPlayerBuilt = source.m_targetNonPlayerBuilt;
        destination.m_icon = source.m_icon;
        destination.m_isUpgrade = source.m_isUpgrade;
        destination.m_comfort = source.m_comfort;
        destination.m_comfortGroup = source.m_comfortGroup;
        destination.m_comfortObject = source.m_comfortObject;
        destination.m_groundPiece = source.m_groundPiece;
        destination.m_allowAltGroundPlacement = source.m_allowAltGroundPlacement;
        destination.m_groundOnly = source.m_groundOnly;
        destination.m_cultivatedGroundOnly = source.m_cultivatedGroundOnly;
        destination.m_waterPiece = source.m_waterPiece;
        destination.m_clipGround = source.m_clipGround;
        destination.m_clipEverything = source.m_clipEverything;
        destination.m_noInWater = source.m_noInWater;
        destination.m_notOnWood = source.m_notOnWood;
        destination.m_notOnTiltingSurface = source.m_notOnTiltingSurface;
        destination.m_inCeilingOnly = source.m_inCeilingOnly;
        destination.m_notOnFloor = source.m_notOnFloor;
        destination.m_noClipping = source.m_noClipping;
        destination.m_onlyInTeleportArea = source.m_onlyInTeleportArea;
        destination.m_allowedInDungeons = source.m_allowedInDungeons;
        destination.m_spaceRequirement = source.m_spaceRequirement;
        destination.m_canRotate = source.m_canRotate;
        destination.m_randomInitBuildRotation = source.m_randomInitBuildRotation;
        destination.m_canBeRemoved = source.m_canBeRemoved;
        destination.m_canRockJade = source.m_canRockJade;
        destination.m_allowRotatedOverlap = source.m_allowRotatedOverlap;
        destination.m_vegetationGroundOnly = source.m_vegetationGroundOnly;
        destination.m_blockingPieces = source.m_blockingPieces == null
            ? null!
            : new List<Piece>(source.m_blockingPieces);
        destination.m_blockRadius = source.m_blockRadius;
        destination.m_mustConnectTo = source.m_mustConnectTo;
        destination.m_connectRadius = source.m_connectRadius;
        destination.m_mustBeAboveConnected = source.m_mustBeAboveConnected;
        destination.m_noVines = source.m_noVines;
        destination.m_extraPlacementDistance = source.m_extraPlacementDistance;
        destination.m_onlyInBiome = source.m_onlyInBiome;
        destination.m_harvest = source.m_harvest;
        destination.m_harvestRadius = source.m_harvestRadius;
        destination.m_harvestRadiusMaxLevel = source.m_harvestRadiusMaxLevel;
        destination.m_placeEffect = source.m_placeEffect;
        destination.m_dlc = source.m_dlc;
        destination.m_craftingStation = source.m_craftingStation;
        destination.m_returnResourceHeightOffset = source.m_returnResourceHeightOffset;
        destination.m_destroyedLootPrefab = source.m_destroyedLootPrefab;
    }

    private static void CopyWearNTearTemplate(WearNTear source, WearNTear destination)
    {
        destination.m_onDestroyed = source.m_onDestroyed;
        destination.m_onDamaged = source.m_onDamaged;
        destination.m_new = source.m_new;
        destination.m_worn = source.m_worn;
        destination.m_broken = source.m_broken;
        destination.m_wet = source.m_wet;
        destination.m_noRoofWear = source.m_noRoofWear;
        destination.m_noSupportWear = source.m_noSupportWear;
        destination.m_ashDamageImmune = source.m_ashDamageImmune;
        destination.m_ashDamageResist = source.m_ashDamageResist;
        destination.m_burnable = source.m_burnable;
        destination.m_materialType = source.m_materialType;
        destination.m_supports = source.m_supports;
        destination.m_comOffset = source.m_comOffset;
        destination.m_forceCorrectCOMCalculation = source.m_forceCorrectCOMCalculation;
        destination.m_staticPosition = source.m_staticPosition;
        destination.m_nonSolidRenderers = source.m_nonSolidRenderers == null
            ? null!
            : new List<Renderer>(source.m_nonSolidRenderers);
        destination.m_health = source.m_health;
        destination.m_damages = source.m_damages;
        destination.m_minToolTier = source.m_minToolTier;
        destination.m_hitNoise = source.m_hitNoise;
        destination.m_destroyNoise = source.m_destroyNoise;
        destination.m_triggerPrivateArea = source.m_triggerPrivateArea;
        destination.m_destroyedEffect = source.m_destroyedEffect;
        destination.m_hitEffect = source.m_hitEffect;
        destination.m_switchEffect = source.m_switchEffect;
        destination.m_autoCreateFragments = source.m_autoCreateFragments;
        destination.m_fragmentRoots = source.m_fragmentRoots == null
            ? null!
            : (GameObject[])source.m_fragmentRoots.Clone();
    }

    private static HashSet<int> BuildExistingFoodHashSet(PieceTable table)
    {
        HashSet<int> existing = new();
        foreach (GameObject piecePrefab in table.m_pieces)
        {
            if (piecePrefab == null)
            {
                continue;
            }

            ItemDrop? itemDrop = piecePrefab.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                continue;
            }

            existing.Add(GetPrefabNameHash(itemDrop.gameObject));

            bool hasSeparateSourceItem = piecePrefab.GetComponent<Feast>() != null ||
                                         piecePrefab.GetComponent<ServeYouRightInjectedPieceMarker>() != null;
            if (!hasSeparateSourceItem)
            {
                continue;
            }

            Piece? piece = piecePrefab.GetComponent<Piece>();
            foreach (Piece.Requirement requirement in piece?.m_resources ?? Array.Empty<Piece.Requirement>())
            {
                if (requirement?.m_resItem != null)
                {
                    existing.Add(GetPrefabNameHash(requirement.m_resItem.gameObject));
                }
            }
        }

        return existing;
    }

    private static Piece.PieceCategory ResolveTargetCategory(PieceTable table, GameObject foodPrefab, Piece.PieceCategory baseCategory)
    {
        string baseLabel = GetBaseCategoryLabel(table, baseCategory);
        EnsureBaseCategoryExists(table, baseCategory, baseLabel);

        if (!TryResolveFoodSourceMod(foodPrefab, out FoodSourceMod sourceMod))
        {
            return baseCategory;
        }

        if (!ServerSyncModTemplatePlugin.UseModSpecificTab(sourceMod, baseCategory))
        {
            return baseCategory;
        }

        return ResolveOrCreateModCategory(baseCategory, sourceMod);
    }

    private static string GetBaseCategoryLabel(PieceTable table, Piece.PieceCategory category)
    {
        int categoryIndex = table.m_categories.IndexOf(category);
        if (categoryIndex >= 0 && categoryIndex < table.m_categoryLabels.Count)
        {
            string existingLabel = table.m_categoryLabels[categoryIndex];
            if (!string.IsNullOrWhiteSpace(existingLabel))
            {
                return existingLabel;
            }
        }

        return GetFallbackCategoryLabel(category);
    }

    private static Piece.PieceCategory ResolveOrCreateModCategory(Piece.PieceCategory baseCategory, FoodSourceMod sourceMod)
    {
        string cacheKey = $"{sourceMod.Id}:{(int)baseCategory}";
        lock (ModCategoryLock)
        {
            if (ModCategoryCache.TryGetValue(cacheKey, out ModCategoryInfo? cachedCategory))
            {
                return cachedCategory.Category;
            }

            string stableCategoryKey = BuildStableCategoryKey(baseCategory, sourceMod.Id);
            if (!JotunnBridge.TryAddPieceCategory(stableCategoryKey, out Piece.PieceCategory createdCategory))
            {
                ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"Could not create Jotunn category '{stableCategoryKey}'. Falling back to vanilla category '{baseCategory}'.");
                return baseCategory;
            }

            ModCategoryCache[cacheKey] = new ModCategoryInfo(createdCategory, baseCategory, sourceMod);
            return createdCategory;
        }
    }

    private static string ResolveCategoryDisplayLabel(Piece.PieceCategory category, string baseLabel)
    {
        if (!string.IsNullOrWhiteSpace(baseLabel))
        {
            string trimmed = baseLabel.Trim();
            string localizable = trimmed;
            // Some mods provide raw token keys without '$' (e.g. vc_hud_food).
            // Prefix '$' and resolve immediately to current language text.
            if (LooksLikeLocalizationTokenKey(trimmed) && !trimmed.StartsWith("$", StringComparison.Ordinal))
            {
                localizable = $"${trimmed}";
            }

            if (Localization.instance != null)
            {
                string localized = Localization.instance.Localize(localizable);
                if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, localizable, StringComparison.Ordinal))
                {
                    return localized.Trim();
                }
            }

            if (LooksLikeLocalizationTokenKey(trimmed))
            {
                return GetFallbackCategoryLabel(category);
            }

            return trimmed;
        }

        return GetFallbackCategoryLabel(category);
    }

    private static bool LooksLikeLocalizationTokenKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith("$", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
        {
            return false;
        }

        // Typical token keys are lower_snake_case (sometimes with dots/hyphens).
        if (!value.Contains("_"))
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string BuildStableCategoryKey(Piece.PieceCategory baseCategory, string modId)
    {
        string categoryKey = baseCategory switch
        {
            Piece.PieceCategory.Food => "food",
            Piece.PieceCategory.Meads => "meads",
            Piece.PieceCategory.Feasts => "feasts",
            _ => $"cat{(int)baseCategory}"
        };

        int modHash = StringExtensionMethods.GetStableHashCode(modId ?? string.Empty);
        if (modHash == int.MinValue)
        {
            modHash = int.MaxValue;
        }

        modHash = Math.Abs(modHash);
        return $"syr_{categoryKey}_{modHash}";
    }

    private static string GetFallbackCategoryLabel(Piece.PieceCategory category)
    {
        return category switch
        {
            Piece.PieceCategory.Food => "Food",
            Piece.PieceCategory.Meads => "Meads",
            Piece.PieceCategory.Feasts => "Feasts",
            _ => category.ToString()
        };
    }

    private static bool TryResolveFoodSourceMod(GameObject foodPrefab, out FoodSourceMod sourceMod)
    {
        string prefabName = Utils.GetPrefabName(foodPrefab.name);

        if (SourceModHitCache.TryGetValue(prefabName, out FoodSourceMod cached))
        {
            sourceMod = cached;
            return true;
        }

        if (KnownCloneSourceBridge.TryResolveSourceMod(prefabName, out FoodSourceMod knownCloneSource))
        {
            sourceMod = knownCloneSource;
            SourceModHitCache[prefabName] = sourceMod;
            return true;
        }

        if (JotunnBridge.TryGetPrefabSourceMod(prefabName, out FoodSourceMod jotunnMod))
        {
            sourceMod = jotunnMod;
            SourceModHitCache[prefabName] = sourceMod;
            return true;
        }

        sourceMod = default;
        return false;
    }

    private static void EnsureBaseCategoryExists(PieceTable table, Piece.PieceCategory category, string label)
    {
        int existingIndex = table.m_categories.IndexOf(category);
        if (existingIndex >= 0)
        {
            return;
        }

        table.m_categories.Add(category);
        while (table.m_categoryLabels.Count < table.m_categories.Count - 1)
        {
            table.m_categoryLabels.Add(table.m_categories[table.m_categoryLabels.Count].ToString());
        }

        table.m_categoryLabels.Add(label);
    }

    private static int GetPrefabNameHash(GameObject prefab)
    {
        string prefabName = Utils.GetPrefabName(prefab.name);
        return StringExtensionMethods.GetStableHashCode(prefabName);
    }

}

internal readonly struct FoodSourceMod
{
    public FoodSourceMod(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }
    public string DisplayName { get; }
}

internal readonly struct CandidateFood
{
    public CandidateFood(ItemDrop itemDrop, GameObject placePrefab, Piece.PieceCategory category)
    {
        ItemDrop = itemDrop;
        PlacePrefab = placePrefab;
        Category = category;
    }

    public ItemDrop ItemDrop { get; }
    public GameObject PlacePrefab { get; }
    public Piece.PieceCategory Category { get; }
}

internal sealed class FeastRoutingData
{
    public FeastRoutingData(Dictionary<string, GameObject> materialToResultPrefab, HashSet<string> resultPrefabNames)
    {
        MaterialToResultPrefab = materialToResultPrefab;
        ResultPrefabNames = resultPrefabNames;
    }

    public Dictionary<string, GameObject> MaterialToResultPrefab { get; }
    public HashSet<string> ResultPrefabNames { get; }
}

internal sealed class ModCategoryInfo
{
    public ModCategoryInfo(Piece.PieceCategory category, Piece.PieceCategory baseCategory, FoodSourceMod sourceMod)
    {
        Category = category;
        BaseCategory = baseCategory;
        SourceMod = sourceMod;
    }

    public Piece.PieceCategory Category { get; }
    public Piece.PieceCategory BaseCategory { get; }
    public FoodSourceMod SourceMod { get; }
}

internal sealed class PerModCategoryConfig
{
    public PerModCategoryConfig(ConfigEntry<ServerSyncModTemplatePlugin.Toggle> food, ConfigEntry<ServerSyncModTemplatePlugin.Toggle> meads, ConfigEntry<ServerSyncModTemplatePlugin.Toggle> feasts)
    {
        Food = food;
        Meads = meads;
        Feasts = feasts;
    }

    public ConfigEntry<ServerSyncModTemplatePlugin.Toggle> Food { get; }
    public ConfigEntry<ServerSyncModTemplatePlugin.Toggle> Meads { get; }
    public ConfigEntry<ServerSyncModTemplatePlugin.Toggle> Feasts { get; }
}

internal static class KnownCloneSourceBridge
{
    private const string WackyGuid = "WackyMole.WackysDatabase";
    private const string DefaultWackyName = "WackysDatabase";

    private static bool _initialized;
    private static MethodInfo? _wackyGetClonedMap;
    private static string _wackyDisplayName = DefaultWackyName;
    private static readonly HashSet<string> WackyCloneHitCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolveSourceMod(string prefabName, out FoodSourceMod sourceMod)
    {
        sourceMod = default;
        EnsureInitialized();
        if (_wackyGetClonedMap == null)
        {
            return false;
        }

        if (WackyCloneHitCache.Contains(prefabName))
        {
            sourceMod = new FoodSourceMod(WackyGuid, _wackyDisplayName);
            return true;
        }

        try
        {
            object? result = _wackyGetClonedMap.Invoke(null, new object[] { prefabName });
            string? clonedFrom = result as string;
            if (string.IsNullOrWhiteSpace(clonedFrom))
            {
                return false;
            }

            WackyCloneHitCache.Add(prefabName);
            sourceMod = new FoodSourceMod(WackyGuid, _wackyDisplayName);
            return true;
        }
        catch (Exception ex)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogDebug($"Known clone source resolution failed for '{prefabName}': {ex.Message}");
            return false;
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            Type? wackyApiType = Type.GetType("API.WackyAPI, WackysDatabase");
            _wackyGetClonedMap = wackyApiType?.GetMethod("GetClonedMap", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);

            if (Chainloader.PluginInfos.TryGetValue(WackyGuid, out PluginInfo? pluginInfo))
            {
                string pluginName = pluginInfo?.Metadata?.Name ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(pluginName))
                {
                    _wackyDisplayName = pluginName;
                }
            }
        }
        catch (Exception ex)
        {
            _wackyGetClonedMap = null;
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogDebug($"Known clone source bridge init failed: {ex.Message}");
        }
    }
}

internal static class JotunnBridge
{
    private static bool _initialized;
    private static bool _available;
    private static MethodInfo? _modQueryEnable;
    private static MethodInfo? _modQueryGetPrefab;
    private static PropertyInfo? _pieceManagerInstance;
    private static MethodInfo? _pieceManagerAddPieceCategory;

    public static void InitializeAndEnableModQuery()
    {
        if (_initialized)
        {
            EnableModQuery();
            return;
        }

        _initialized = true;
        try
        {
            Type? modQueryType = Type.GetType("Jotunn.Utils.ModQuery, Jotunn");
            Type? pieceManagerType = Type.GetType("Jotunn.Managers.PieceManager, Jotunn");

            _modQueryEnable = modQueryType?.GetMethod("Enable", BindingFlags.Public | BindingFlags.Static);
            _modQueryGetPrefab = modQueryType?.GetMethod("GetPrefab", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            _pieceManagerInstance = pieceManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _pieceManagerAddPieceCategory = pieceManagerType?.GetMethod("AddPieceCategory", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);

            _available = _modQueryEnable != null &&
                         _modQueryGetPrefab != null &&
                         _pieceManagerInstance != null &&
                         _pieceManagerAddPieceCategory != null;
        }
        catch (Exception ex)
        {
            _available = false;
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"Failed to initialize Jotunn bridge: {ex.Message}");
        }

        if (!_available)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning("Jotunn bridge is unavailable. Mod-specific tabs will fall back to vanilla categories.");
            return;
        }

        EnableModQuery();
    }

    public static bool TryGetPrefabSourceMod(string prefabName, out FoodSourceMod sourceMod)
    {
        sourceMod = default;
        if (!_available || _modQueryGetPrefab == null)
        {
            return false;
        }

        try
        {
            object? modPrefab = _modQueryGetPrefab.Invoke(null, new object[] { prefabName });
            if (modPrefab == null)
            {
                return false;
            }

            object? sourceModObj = modPrefab.GetType().GetProperty("SourceMod", BindingFlags.Public | BindingFlags.Instance)?.GetValue(modPrefab);
            if (sourceModObj == null)
            {
                return false;
            }

            if (sourceModObj is BepInPlugin plugin)
            {
                string pluginName = string.IsNullOrWhiteSpace(plugin.Name) ? plugin.GUID : plugin.Name;
                sourceMod = new FoodSourceMod(plugin.GUID, pluginName);
                return true;
            }

            string? guid = sourceModObj.GetType().GetProperty("GUID", BindingFlags.Public | BindingFlags.Instance)?.GetValue(sourceModObj) as string;
            string? name = sourceModObj.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(sourceModObj) as string;
            string guidValue = guid ?? string.Empty;
            if (string.IsNullOrWhiteSpace(guidValue))
            {
                return false;
            }

            string nameValue = string.IsNullOrWhiteSpace(name) ? guidValue : name!;
            sourceMod = new FoodSourceMod(guidValue, nameValue);
            return true;
        }
        catch (Exception ex)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogDebug($"ModQuery.GetPrefab failed for '{prefabName}': {ex.Message}");
            return false;
        }
    }

    public static bool TryAddPieceCategory(string categoryName, out Piece.PieceCategory category)
    {
        category = Piece.PieceCategory.Food;
        if (!_available || _pieceManagerInstance == null || _pieceManagerAddPieceCategory == null)
        {
            return false;
        }

        try
        {
            object? manager = _pieceManagerInstance.GetValue(null);
            if (manager == null)
            {
                return false;
            }

            object? result = _pieceManagerAddPieceCategory.Invoke(manager, new object[] { categoryName });
            if (result is Piece.PieceCategory pieceCategory)
            {
                category = pieceCategory;
                return true;
            }

            if (result is int categoryValue)
            {
                category = (Piece.PieceCategory)categoryValue;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"Failed to add Jotunn piece category '{categoryName}': {ex.Message}");
            return false;
        }
    }

    private static void EnableModQuery()
    {
        if (!_available || _modQueryEnable == null)
        {
            return;
        }

        try
        {
            _modQueryEnable.Invoke(null, null);
        }
        catch (Exception ex)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"Failed to enable Jotunn ModQuery: {ex.Message}");
        }
    }
}

internal sealed class ServeYouRightInjectedPieceMarker : MonoBehaviour
{
}
