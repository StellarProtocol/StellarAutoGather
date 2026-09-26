using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.AutoGather;

// Nearby life-resource node roster + config resolution for Auto-Gather. Walks EntCollection entities out of
// ZEntityMgr.entityDict_ (same walk as FollowController.Roster.cs), resolves each node's config id via
// ZEntityHelper.TryGetConfigIdByUuid (C# reflection, exact long — no precision loss), then reads CollectionTable
// (Type / PickRange / CollectionName) via the Lua TableMgr. A node is kept when it's life-gatherable: Type==1 OR it
// has a LifeCollectListTable row (Category != 0). Type alone is NOT reliable — some farm/log nodes are Type==0. Config rows
// are cached per config id (≈76 distinct ever) so the Lua round trips stop after warm-up. The per-uuid _nodes list
// drives selection + distances; it's aggregated into _typeRows (one row per distinct configId) for the picker.
// Refresh throttled ~1s and only when the loop or the open menu asks — nothing scans at load.
internal sealed partial class AutoGatherController
{
    private readonly struct NodeCfg
    {
        public readonly int    Type;      // CollectionTable.Type; ==1 is only ONE gather category — some life-gather nodes are Type==0 (see Category)
        public readonly int    Category;  // LifeCollectListTable.LifeProId (101/102/103; 0 = unknown)
        public readonly float  PickRange; // CollectionTable.PickRange
        public readonly string Name;      // CollectionTable.CollectionName
        public readonly int    Cost;      // LifeCollectListTable.Cost[2] = Focus (item 20003) amount a focus gather spends (0 = normal-only / unknown)
        public NodeCfg(int type, int category, float pickRange, string name, int cost)
        { Type = type; Category = category; PickRange = pickRange; Name = name; Cost = cost; }
    }

    private readonly struct NodeEntry
    {
        public readonly long   Uuid;
        public readonly int    ConfigId;
        public readonly string Name;
        public readonly float  PickRange;
        public readonly float  Dist;     // float.MaxValue when position unavailable
        public NodeEntry(long uuid, int configId, string name, float pickRange, float dist)
        { Uuid = uuid; ConfigId = configId; Name = name; PickRange = pickRange; Dist = dist; }
    }

    // One picker row per distinct configId currently nearby (aggregated from _nodes).
    private readonly struct TypeRow
    {
        public readonly int    ConfigId;
        public readonly string Name;
        public readonly int    Count;
        public readonly float  NearestDist;
        public TypeRow(int configId, string name, int count, float nearestDist)
        { ConfigId = configId; Name = name; Count = count; NearestDist = nearestDist; }
    }

    private readonly List<NodeEntry>          _nodes       = new();   // life-resource nodes, sorted nearest-first
    private readonly List<TypeRow>            _typeRows    = new();   // distinct types nearby, nearest-first (picker rows)
    private readonly Dictionary<long, int>    _uuidConfig  = new();   // uuid → config id (stable per node)
    private readonly Dictionary<int, NodeCfg> _cfgCache    = new();   // config id → resolved table row
    private readonly Dictionary<long, double> _blacklist   = new();   // uuid → expiry time (failed/aborted nodes)
    private double _rosterTimer = -999;
    private double _focusGateTimer = -999;   // throttle for the strict-Focus seek-time balance check (~1s)
    private const double BlacklistSeconds = 15.0;

    // Focus (item 20003) cost of the LOCKED type (0 = normal-only / unknown / not resolved yet).
    private int LockedFocusCost => (_lockedConfigId != 0 && _cfgCache.TryGetValue(_lockedConfigId, out var c)) ? c.Cost : 0;

    // Current Focus (item 20003) balance; false/-1 when unreadable. Read via the game items VM (same call FireBeginInteraction uses).
    private bool TryReadFocusBalance(out int bal)
    {
        bal = -1;
        if (!_services.Lua.Ready) return false;
        _services.Lua.DoString("pcall(function() rawset(_G,'__ag_bal', (Z.VMMgr.GetVM('items')):GetItemTotalCount(20003)) end)");
        if (_services.Lua.TryReadGlobalNumber("__ag_bal", out var v)) { bal = (int)v; return bal >= 0; }
        return false;
    }

    // ── Picker accessors — one row per distinct type nearby (mirror FollowController.Roster.cs surface) ─────
    public int RosterDisplayCount { get { EnsureRosterFresh(); return _typeRows.Count; } }

    public string RosterRowLabel(int i)
    {
        EnsureRosterFresh();
        if (i < 0 || i >= _typeRows.Count) return "";
        var r = _typeRows[i];
        string dist = r.NearestDist >= float.MaxValue ? "?" : $"{r.NearestDist:F1}m";
        string mark = r.ConfigId == _lockedConfigId ? "▶ " : "";
        return $"{mark}{r.Name} ×{r.Count}  {dist}";
    }

    public bool IsRosterRowSelected(int i) => i >= 0 && i < _typeRows.Count && _typeRows[i].ConfigId == _lockedConfigId;

    // Click a picker row → lock that resource type; the loop then farms all nearby nodes of THAT configId.
    public void SelectRosterRow(int i)
    {
        EnsureRosterFresh();
        if (i < 0 || i >= _typeRows.Count) return;
        var r = _typeRows[i];
        _lockedConfigId = r.ConfigId;
        _lockedName     = r.Name;
        ResetLoop(_loc.TFormat("ag.status.locked", r.Name));
        _services.Log.Info($"[Gather] locked type configId={r.ConfigId} '{r.Name}'");
    }

    public string LockedName => _lockedConfigId == 0 ? "" : _lockedName;

    // Count of nearby, currently-available (non-blacklisted) nodes of the LOCKED type (drives the status line).
    public int LockedTypeCount
    {
        get
        {
            EnsureRosterFresh();
            if (_lockedConfigId == 0) return 0;
            int n = 0;
            foreach (var e in _nodes)
            {
                if (IsBlacklisted(e.Uuid)) continue;   // skip nodes we're currently backing off from
                if (e.ConfigId == _lockedConfigId) n++;
            }
            return n;
        }
    }

    // ── Nearest-of-locked-type selection (drives the Seek state) ──────────────
    private bool TrySelectNearest(Vector3 localPos, out long uuid, out float pickRange, out string name)
    {
        uuid = 0; pickRange = DefaultStopDistance; name = _lockedName;
        if (_lockedConfigId == 0) return false;
        EnsureRosterFresh();
        float best = float.MaxValue;
        foreach (var e in _nodes)
        {
            if (e.ConfigId != _lockedConfigId) continue;
            if (IsBlacklisted(e.Uuid)) continue;
            if (e.Dist >= best) continue;
            best = e.Dist; uuid = e.Uuid; pickRange = e.PickRange > 0.1f ? e.PickRange : DefaultStopDistance; name = e.Name;
        }
        return uuid != 0;
    }

    // ── Blacklist ────────────────────────────────────────────────────────────
    private void AddBlacklist(long uuid)
    {
        _blacklist[uuid] = _services.Framework.TimeNow + BlacklistSeconds;
        _services.Log.Info($"[Gather] blacklist node {uuid} for {BlacklistSeconds:F0}s");
    }

    private bool IsBlacklisted(long uuid)
    {
        if (!_blacklist.TryGetValue(uuid, out var expiry)) return false;
        if (_services.Framework.TimeNow >= expiry) { _blacklist.Remove(uuid); return false; }
        return true;
    }

    // ── Throttled roster rebuild ─────────────────────────────────────────────
    private void EnsureRosterFresh()
    {
        double now = _services.Framework.TimeNow;
        if (now - _rosterTimer < 1.0) return;
        _rosterTimer = now;
        RebuildRoster();
    }

    private void RebuildRoster()
    {
        _nodes.Clear();
        try
        {
            if (!EnsureApi()) return;
            EnsureRosterDict();
            var mgr = _piEntMgrInstance!.GetValue(null);
            if (mgr == null) return;
            var dict = _piEntityDict != null ? _piEntityDict.GetValue(mgr) : _fiEntityDict?.GetValue(mgr);
            var keys = dict?.GetType().GetProperty("Keys")?.GetValue(dict);
            if (keys == null) return;

            Vector3 localPos = default;
            long localUuid = _services.CombatSnapshot.LocalEntityId.Value;
            bool haveLocal = localUuid != 0 && TryGetPosByUuid(localUuid, out localPos);

            foreach (var k in WalkIl2Cpp(keys))
            {
                long uuid = Convert.ToInt64(k);
                if (((uuid >> 6) & 31) != EntCollection) continue;   // life/collection entities only
                int configId = ResolveConfigId(uuid);
                if (configId <= 0) continue;
                var cfg = ResolveConfig(configId);
                if (cfg == null) continue;
                // Life-profession gatherable = Type==1 OR has a LifeCollectListTable row (Category=LifeProId != 0).
                // Type alone is NOT reliable: 6 farm/log nodes (e.g. Asteria Cotton, id 20000019) are Type==0 but ARE
                // life-gather; Stokes Mine (20000110) is Type==1 with no life-list row. Union keeps all 82, excludes chests.
                if (cfg.Value.Type != 1 && cfg.Value.Category == 0) continue;
                float dist = (haveLocal && TryGetPosByUuid(uuid, out var p))
                    ? Vector3.Distance(localPos, p) : float.MaxValue;
                _nodes.Add(new NodeEntry(uuid, configId, cfg.Value.Name, cfg.Value.PickRange, dist));
            }

            _nodes.Sort((a, b) => a.Dist.CompareTo(b.Dist));   // nearest-first
            AggregateTypeRows();
        }
        catch (Exception ex) { LogErrOnce("roster: " + (ex.InnerException?.Message ?? ex.Message)); }
    }

    // One row per distinct configId nearby: count + nearest distance (from the already-sorted _nodes).
    // Counts only currently-available (non-blacklisted) nodes — a type whose nodes are all blacklisted drops its row.
    private void AggregateTypeRows()
    {
        _typeRows.Clear();
        var seen = new Dictionary<int, int>();   // configId → index into _typeRows
        foreach (var e in _nodes)
        {
            if (IsBlacklisted(e.Uuid)) continue;   // skip nodes we're currently backing off from
            if (seen.TryGetValue(e.ConfigId, out var idx))
            {
                var r = _typeRows[idx];
                _typeRows[idx] = new TypeRow(r.ConfigId, r.Name, r.Count + 1, r.NearestDist);
            }
            else
            {
                seen[e.ConfigId] = _typeRows.Count;
                _typeRows.Add(new TypeRow(e.ConfigId, e.Name, 1, e.Dist));   // _nodes is nearest-first → first seen = nearest
            }
        }
        _typeRows.Sort((a, b) => a.NearestDist.CompareTo(b.NearestDist));
    }

    // ── uuid → config id (C# reflection, exact long) ─────────────────────────
    private int _cfgIdLogged;
    private int ResolveConfigId(long uuid)
    {
        if (_uuidConfig.TryGetValue(uuid, out var id)) return id;
        if (_miTryGetConfigId == null) return 0;
        try
        {
            var args = new object[] { uuid, 0 };                       // out configId is args[1]
            bool ok = _miTryGetConfigId.Invoke(null, args) is bool b && b;
            if (!ok) return 0;
            int cid = Convert.ToInt32(args[1] ?? 0);
            if (cid <= 0) return 0;
            _uuidConfig[uuid] = cid;
            if (_cfgIdLogged < 5) { _cfgIdLogged++; _services.Log.Info($"[Gather] resolved uuid {uuid} → configId {cid}"); }
            return cid;
        }
        catch (Exception ex) { LogErrOnce("cfgId: " + (ex.InnerException?.Message ?? ex.Message)); return 0; }
    }

    // ── config id → table row (Lua TableMgr, cached) ─────────────────────────
    private int _cfgLogged;
    private NodeCfg? ResolveConfig(int configId)
    {
        if (_cfgCache.TryGetValue(configId, out var cached)) return cached;
        if (!_services.Lua.Ready) return null;

        _services.Lua.DoString(
            "pcall(function()" +
            " rawset(_G,'__ag_ok', nil)" +
            " local id = " + configId.ToString(CultureInfo.InvariantCulture) +
            " local row = (Z.TableMgr).GetRow(\"CollectionTableMgr\", id)" +
            " if row == nil then return end" +
            " rawset(_G,'__ag_type', row.Type)" +
            " rawset(_G,'__ag_pick', row.PickRange)" +
            " rawset(_G,'__ag_name', row.CollectionName)" +
            " local cat = 0" +
            " local cost = 0" +
            " local lr = (Z.TableMgr).GetRow(\"LifeCollectListTableMgr\", id)" +
            " if lr ~= nil then" +
            "  cat = lr.LifeProId" +
            // Cost is a flat 1-based array {20003, amount}: [1]=currency id, [2]=Focus amount (0 = normal-only). Guard the id.
            "  if lr.Cost ~= nil then local a, b = lr.Cost[1], lr.Cost[2] if a == 20003 then cost = b or 0 elseif b == 20003 then cost = a or 0 else cost = b or a or 0 end end" +
            " end" +
            " rawset(_G,'__ag_cat', cat)" +
            " rawset(_G,'__ag_cost', cost)" +
            " rawset(_G,'__ag_ok', true)" +
            " end)");

        if (!_services.Lua.TryReadGlobalBool("__ag_ok", out var ok) || !ok) return null;
        _services.Lua.TryReadGlobalNumber("__ag_type", out var type);
        _services.Lua.TryReadGlobalNumber("__ag_pick", out var pick);
        _services.Lua.TryReadGlobalNumber("__ag_cat",  out var cat);
        _services.Lua.TryReadGlobalNumber("__ag_cost", out var cost);
        string name = _services.Lua.ReadGlobalString("__ag_name") ?? "";

        var cfg = new NodeCfg((int)type, (int)cat, (float)pick, string.IsNullOrEmpty(name) ? $"Node {configId}" : name, (int)cost);
        _cfgCache[configId] = cfg;
        if (_cfgLogged < 8) { _cfgLogged++; _services.Log.Info($"[Gather] config {configId}: type={cfg.Type} cat={cfg.Category} pick={cfg.PickRange:F1} cost={cfg.Cost} name='{cfg.Name}'"); }
        return cfg;
    }

    private bool TryGetPosByUuid(long uuid, out Vector3 pos)
    {
        pos = default;
        var ent = GetEntity(uuid);
        return ent != null && TryGetPos(ent, out pos);
    }

    // ── ZEntityMgr entity-dictionary resolve (mirrors FollowController.Roster.cs) ─
    private PropertyInfo? _piEntityDict;
    private FieldInfo?    _fiEntityDict;
    private bool          _rosterDictResolved;

    private void EnsureRosterDict()
    {
        if (_rosterDictResolved) return;
        _rosterDictResolved = true;
        var entMgr = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
        if (entMgr == null) return;
        const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _piEntityDict = entMgr.GetProperty("entityDict_", anyInst);
        if (_piEntityDict == null) _fiEntityDict = entMgr.GetField("entityDict_", anyInst);
    }

    private static List<object> WalkIl2Cpp(object collection)
    {
        var res = new List<object>();
        var getEnum = FindNoArg(collection.GetType(), "GetEnumerator");
        var en = getEnum?.Invoke(collection, null);
        if (en == null) return res;
        var enT  = en.GetType();
        var move = FindNoArg(enT, "MoveNext");
        var cur  = enT.GetProperty("Current");
        if (move == null || cur == null) return res;
        while ((bool)move.Invoke(en, null)!)
        {
            var v = cur.GetValue(en);
            if (v != null) res.Add(v);
        }
        return res;
    }

    private static MethodInfo? FindNoArg(Type t, string name)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == name && m.GetParameters().Length == 0) return m;
        return null;
    }
}
