using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

internal static class ServingTrayMenu
{
    private static readonly AccessTools.FieldRef<BuildUi, List<IPieceList>> Lists = AccessTools.FieldRefAccess<BuildUi, List<IPieceList>>("m_pieceLists");
    private static readonly AccessTools.FieldRef<BuildUi, PieceTable> Tool = AccessTools.FieldRefAccess<BuildUi, PieceTable>("m_currentBuildTool");
    private static readonly AccessTools.FieldRef<BuildUi, int> CurrentList = AccessTools.FieldRefAccess<BuildUi, int>("m_currentPieceList");
    private static readonly Dictionary<BuildUi, Registration> Registrations = new();

    internal static void BeforeOpen(BuildUi menu)
    {
        if (!Registrations.TryGetValue(menu, out Registration registration))
            Registrations.Add(menu, registration = new Registration());
        registration.Restore(Lists(menu));
        PieceTable? tool = Player.m_localPlayer?.GetBuildTool();
        if (!FeasterFoodInjector.IsFeasterTool(tool)) return;

        // Optional content can arrive after ObjectDB/Game initialization. Do not
        // refresh the visible menu recursively while its tool is being changed.
        registration.Opening = true;
        try { FeasterFoodInjector.RefreshFoodPiecesAndMenu(ObjectDB.instance); }
        finally { registration.Opening = false; }

        if (!registration.UseCategories(Lists(menu))) return;
        registration.Tool = tool;
        registration.WasSimple = tool!.m_hideAdvancedMenu;
        tool.m_hideAdvancedMenu = false;
    }

    internal static void Close(BuildUi menu)
    {
        if (Registrations.TryGetValue(menu, out Registration registration)) registration.Restore(Lists(menu));
    }

    internal static void Refresh()
    {
        BuildUi? menu = Hud.instance != null ? Hud.instance.m_buildUi : null;
        if (menu == null || !menu.isActiveAndEnabled || Player.m_localPlayer == null ||
            !Registrations.TryGetValue(menu, out Registration registration) || registration.Opening) return;
        PieceTable? tool = Tool(menu);
        if (tool != Player.m_localPlayer.GetBuildTool() || !FeasterFoodInjector.IsFeasterTool(tool)) return;
        menu.SelectPieceList(CurrentList(menu), true);
    }

    internal static void Detach(BuildUi menu, bool destroying)
    {
        if (!Registrations.TryGetValue(menu, out Registration registration)) return;
        registration.Restore(Lists(menu));
        Registrations.Remove(menu);
        if (!destroying && menu.isActiveAndEnabled && Player.m_localPlayer != null)
            menu.SelectPieceList(CurrentList(menu), true);
    }

    internal static void Dispose()
    {
        foreach (BuildUi menu in new List<BuildUi>(Registrations.Keys))
            if (menu != null) Detach(menu, false); else Registrations.Remove(menu!);
    }

    private sealed class Registration
    {
        private readonly FoodList _categories = new();
        private IPieceList? _original;
        internal PieceTable? Tool;
        internal bool WasSimple;
        internal bool Opening;

        internal bool UseCategories(List<IPieceList> lists)
        {
            if (lists.Contains(_categories)) return true;
            // Reuse the existing Categories/By usage slot. Do not add a button,
            // capture another mod's tab index, or replace its unrelated lists.
            int index = lists.FindIndex(list => list is ByUsagePieceList);
            if (index < 0) return false;
            _original = lists[index];
            _categories.DisplayName = _original.DisplayName;
            lists[index] = _categories;
            return true;
        }

        internal void RestoreCategories(List<IPieceList> lists)
        {
            int index = lists.IndexOf(_categories);
            if (index >= 0 && _original != null) lists[index] = _original;
            _original = null;
            _categories.Clear();
        }

        internal void Restore(List<IPieceList> lists)
        {
            RestoreCategories(lists);
            if (Tool != null && !Tool.m_hideAdvancedMenu) Tool.m_hideAdvancedMenu = WasSimple;
            Tool = null;
        }
    }

    private sealed class FoodList : IPieceList
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int Id, string Label)> _tags = new();
        private readonly Dictionary<Piece, int> _pieceTags = new();
        private readonly HashSet<int> _availableTags = new();
        private readonly HashSet<Piece> _seen = new();
        public string DisplayName { get; internal set; } = "$hud_byusage";
        public bool ShowTags => true;
        public bool CanCustomizeTags => false;
        public int TagCount => _tags.Count;
        public int TagSeparatorIndex => -1;
        public string GetTagDisplayName(int index) => _tags[index].Label;
        public int GetTagIdByIndex(int index) => _tags[index].Id;

        internal void Clear()
        { _tags.Clear(); _pieceTags.Clear(); _availableTags.Clear(); _seen.Clear(); }

        public void UpdateAvailableTags(PieceTable table)
        {
            Clear();
            if (!FeasterFoodInjector.IsFeasterTool(table)) return;
            foreach (GameObject prefab in table.m_pieces)
            {
                if (prefab == null || !prefab.TryGetComponent(out Piece piece) || !table.m_availablePieces.Contains(piece) ||
                    piece.m_repairPiece || piece.m_removePiece || piece.m_category == Piece.PieceCategory.All) continue;
                FeasterFoodInjector.GetMenuGroup(table, piece, out string key, out string label);
                if (!_ids.TryGetValue(key, out int id)) { id = _ids.Count; _ids.Add(key, id); }
                _pieceTags[piece] = id;
                if (_availableTags.Add(id)) _tags.Add((id, label));
            }
        }

        public void GetAvailablePiecesWithTag(int tagId, PieceTable table, IList<Piece> resultOut)
        {
            if (!FeasterFoodInjector.IsFeasterTool(table)) return;
            // A removed tag must not leave the player on an empty stale selection.
            bool knownTag = _availableTags.Contains(tagId);
            _seen.Clear();
            foreach (GameObject prefab in table.m_pieces)
            {
                if (prefab == null || !prefab.TryGetComponent(out Piece piece) || !table.m_availablePieces.Contains(piece) || !_seen.Add(piece)) continue;
                if (!knownTag || tagId == -1 || piece.m_repairPiece || piece.m_removePiece || piece.m_category == Piece.PieceCategory.All ||
                    (_pieceTags.TryGetValue(piece, out int id) && id == tagId)) resultOut.Add(piece);
            }
            _seen.Clear();
        }
    }
}

[HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OpenBuildMenu))]
internal static class TrayMenuOpenPatch
{
    private static void Prefix(BuildUi __instance) => ServingTrayMenu.BeforeOpen(__instance);
    private static Exception? Finalizer(BuildUi __instance, Exception? __exception)
    { if (__exception != null) ServingTrayMenu.Close(__instance); return __exception; }
}
[HarmonyPatch(typeof(BuildUi), nameof(BuildUi.Close))]
internal static class TrayMenuClosePatch { private static void Postfix(BuildUi __instance) => ServingTrayMenu.Close(__instance); }
[HarmonyPatch(typeof(BuildUi), "OnDestroy")]
internal static class TrayMenuDestroyPatch { private static void Prefix(BuildUi __instance) => ServingTrayMenu.Detach(__instance, true); }
