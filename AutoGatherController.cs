using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.AutoGather;

/// <summary>
/// Auto-Gather engine: continuously harvests nearby life-resource nodes of ONE locked resource type (picked by
/// its real CollectionName from the nearby-node list, NOT a broad category). Mirrors FollowController's shape — a
/// per-frame state machine over the shared PlayerMoveDriver, with an EntCollection roster
/// (AutoGatherController.Roster.cs), a channeled Interaction trigger + completion detection
/// (AutoGatherController.Gather.cs), and a progress-bar completion hook (AutoGatherController.Progress.cs).
///
/// Loop: Seek nearest non-blacklisted node of the locked configId → walk toward it via the move inject →
/// InRange (dist ≤ PickRange) → CUT the movement inject (moving aborts the channel) → pick the Focus/Normal
/// interaction option + fire BeginInteraction (Lua bridge) → Channel (InteractionUIProgressEnd primary; attr-poll
/// fallback) → Done (blacklist the just-gathered node — server despawn is LATE) → back to Seek.
///
/// Gates every tick: Phase==World, local player alive, out of combat (AttrCombatState==0). All research-derived —
/// NOT yet in-game validated; see Auto-Gather-and-Interaction.md "Open items to validate". Every decision point logs [Gather].
/// </summary>
internal sealed partial class AutoGatherController
{
    private const int AttrHp             = 11310;
    private const int AttrMaxHp          = 11320;
    private const int AttrCombatState    = 104;   // >0 = in combat → gathering blocked
    private const int AttrInteractionStage = 154; // EInteractionStage: 3=Interacting, 0=Default (channel done)
    private const int AttrCollectCounter = 500;   // increments on a completed gather (success signal)
    private const int EntCollection      = 16;    // EEntityType.EntCollection — (uuid >> 6) & 31 == 16

    private const float DefaultStopDistance = 2.5f;   // used when CollectionTable.PickRange is unavailable

    private enum GState { Seeking, Approaching, Gathering }

    private readonly IPluginServices  _services;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly ILocalization    _loc;

    private bool   _enabled;
    private bool   _subscribed;
    private bool   _focusMode;          // true = prefer the Focus (spends ~20 Focus) option; false = always Normal
    private int    _lockedConfigId;     // the resource type the loop farms (0 = none picked)
    private string _lockedName = "";    // its CollectionName (for status text)
    private string _status      = "off";

    private GState _state = GState.Seeking;
    private long   _targetUuid;         // node currently being approached / gathered
    private int    _targetConfigId;
    private float  _targetPickRange;
    private int    _targetFocusCost;    // target node's Focus (item 20003) cost from LifeCollectListTable; 0 = unknown / normal-only

    public AutoGatherController(IPluginServices services, PlayerMoveDriver moveDriver, ILocalization loc)
    {
        _services   = services;
        _moveDriver = moveDriver;
        _loc        = loc;
        // Localize the initial "off" the field initializer set (it runs before _loc exists); this default
        // shows in the window until the loop first runs, so it must not stay an English literal.
        _status     = _loc.T("ag.status.off");
    }

    public string Status => _status;

    public Action? OnAutoStopped;   // fired when the loop self-stops (e.g. out of Focus in strict Focus mode)

    // Strict Focus mode ran out of Focus → stop the whole loop rather than silently doing a Normal gather or
    // grinding through nodes. Tears down like SetEnabled(false) then reports a clear status + notifies the host.
    private void StopOutOfFocus()
    {
        _services.Log.Info("[Gather] out of Focus — stopping auto-gather (strict Focus mode)");
        SetEnabled(false);                   // full teardown (unsubscribe, stop move, abort channel)
        SetStatus(_loc.T("ag.status.outOfFocus")); // override the "off" status SetEnabled just set
        OnAutoStopped?.Invoke();
    }

    // Focus/Normal gather mode (KB §5b). Focus prefers the [63]/focus option (falls back to Normal when its
    // CheckCondition fails or the node has no focus template); Normal always takes the non-focus option.
    public bool FocusMode
    {
        get => _focusMode;
        set { if (value != _focusMode) { _focusMode = value; _services.Log.Info($"[Gather] mode = {(_focusMode ? "Focus" : "Normal")}"); } }
    }

    // Navmesh routing toggle. ON (default) = steer along the navmesh corridor (NavGatherFollower). OFF = direct/
    // straight-line to the node — ComputeApproachSteer short-circuits to nodePos and never touches the follower/DotRecast.
    public bool UseNavmesh { get; set; } = true;

    // Straight-line fallback for a node the navmesh can't route to. ON = straight-line toward an unreachable node (the
    // watchdogs skip it if it's genuinely stuck). OFF (default) = skip an unreachable node IMMEDIATELY (blacklist +
    // reseek) instead of approaching it. Inert when UseNavmesh is OFF — the follower never runs, so the
    // navmesh-unreachable flag is never set.
    public bool NavmeshFallback { get; set; } = false;

    public void SetEnabled(bool value)
    {
        if (value == _enabled) return;
        _enabled = value;
        if (value)
        {
            _services.Framework.Update += OnUpdate;
            _subscribed = true;
            _moveDriver.Install(this);
            InstallProgressHook();
            _services.Log.Info($"[Gather] enabled (mode={(_focusMode ? "Focus" : "Normal")} lockedType='{(_lockedConfigId == 0 ? "(none)" : _lockedName)}')");
        }
        else
        {
            if (_subscribed) { _services.Framework.Update -= OnUpdate; _subscribed = false; }
            AbortActiveChannel("disabled");
            ClearPath();
            _moveDriver.Uninstall(this);
            UninstallProgressHook();
            _state = GState.Seeking; _targetUuid = 0;
            SetStatus(_loc.T("ag.status.off"));
            _services.Log.Info("[Gather] disabled");
        }
    }

    public void Dispose()
    {
        if (_subscribed) { try { _services.Framework.Update -= OnUpdate; } catch { } _subscribed = false; }
        try { AbortActiveChannel("dispose"); ClearPath(); _moveDriver.Uninstall(this); UninstallProgressHook(); } catch { }
        _enabled = false;
    }

    // ── Per-frame driver ────────────────────────────────────────────────────
    private void OnUpdate(float dt)
    {
        if (!_enabled) return;
        try
        {
            if (_services.ClientState.Phase != GamePhase.World) { ResetLoop("not in world"); return; }
            if (!EnsureApi()) { SetStatus("api not ready"); return; }

            // After auto-closing a reward popup, let the UI settle before driving/gathering again.
            if (_services.Framework.TimeNow < _popupResumeAt) { SetStatus(_loc.T("ag.status.popupPaused"), "popup"); return; }

            long localUuid = _services.CombatSnapshot.LocalEntityId.Value;
            if (localUuid == 0) { ResetLoop("no local player"); return; }
            var localEnt = GetEntity(localUuid);
            if (localEnt == null) { ResetLoop("no local entity"); return; }

            if (IsDead(localEnt)) { ResetLoop(_loc.T("ag.status.dead")); return; }

            // Auto-dismiss the daily monthly-card / reward popup (opt-in) so an AFK session isn't interrupted.
            if (DismissMonthlyCard && CheckMonthlyCardPopup()) return;

            // Out-of-combat gate (KB §5): in combat blocks the gather → pause the loop, abort any active channel.
            long combat = GetAttrLong(localEnt, _boxCombatState);
            // Route through ResetLoop so the server pathfind is cleared too (else CtrlAutoMoveComp keeps auto-walking
            // during combat, and combat-exit would resume on the stale pre-combat corridor).
            if (combat != 0) { ResetLoop(_loc.T("ag.status.inCombat")); return; }

            if (!TryGetPos(localEnt, out var localPos)) { SetStatus("position unavailable"); return; }

            switch (_state)
            {
                case GState.Seeking:     TickSeek(localPos);                 break;
                case GState.Approaching: TickApproach(localEnt, localPos);   break;
                case GState.Gathering:   TickGather(localEnt);               break;
            }
        }
        catch (Exception ex)
        {
            string msg = ex.InnerException?.Message ?? ex.Message;
            SetStatus($"err: {ex.GetType().Name}: {msg}", "err");
            LogErrOnce(msg);
        }
    }

    // Reset the loop back to Seeking + stop moving (used by the global gates). Keeps the toggle on.
    private void ResetLoop(string status)
    {
        AbortActiveChannel(status);
        ClearPath();
        _moveDriver.Stop(this);
        _state = GState.Seeking; _targetUuid = 0;
        SetStatus(status);
    }

    // UI Status updates every frame (silent); the log line fires only when the CATEGORY changes (the moving-distance
    // statuses embed a live {dist} — pass a stable logCategory so the log doesn't spam).
    private string _lastLoggedCategory = "";
    private void SetStatus(string status, string? logCategory = null)
    {
        _status = status;
        string cat = logCategory ?? status;
        if (cat == _lastLoggedCategory) return;
        _lastLoggedCategory = cat;
        _services.Log.Info($"[Gather] {status}");
    }

    // ── Live reads (duplicated from FollowController — small, self-contained; keeps Gather decoupled from Follow) ─
    private long GetAttrLong(object ent, object? boxedKey)
    {
        if (boxedKey == null || _miGetAttrLong == null) return 0;
        try { return Convert.ToInt64(_miGetAttrLong.Invoke(ent, new object[] { boxedKey, true }) ?? 0L); }
        catch { return 0; }
    }

    private bool IsDead(object ent)
    {
        long maxhp = GetAttrLong(ent, _boxMaxHp);
        if (maxhp <= 0) return false;   // unknown → treat as alive
        return GetAttrLong(ent, _boxHp) <= 0;
    }

    private bool TryGetPos(object ent, out Vector3 pos)
    {
        pos = default;
        try
        {
            var model = _piModel!.GetValue(ent);
            if (model == null) return false;
            var go = _piModelGoComp!.GetValue(model);
            if (go == null) return false;
            if (_piPosition!.GetValue(go) is Vector3 p) { pos = p; return true; }
        }
        catch { }
        return false;
    }

    private object? GetEntity(long uuid)
    {
        try
        {
            var mgr = _piEntMgrInstance!.GetValue(null);
            return mgr == null ? null : _miGetEntity!.Invoke(mgr, new object[] { uuid });
        }
        catch { return null; }
    }

    private bool _loggedErr;
    private void LogErrOnce(string msg) { if (_loggedErr) return; _loggedErr = true; _services.Log.Warning($"[Gather] {msg}"); }

    // ── Reflection resolve (entity / model / position / attrs / config-id) ───
    private bool          _apiResolved, _apiOk;
    private PropertyInfo? _piEntMgrInstance;
    private MethodInfo?   _miGetEntity;
    private MethodInfo?   _miGetAttrLong;    // ZEntity.GetAttr<long>(EAttrType, bool)
    private PropertyInfo? _piModel;          // ZEntity.Model
    private PropertyInfo? _piModelGoComp;    // ZModel.ModelGoComp
    private PropertyInfo? _piPosition;       // ModelGoComp.Position
    private MethodInfo?   _miTryGetConfigId; // ZEntityHelper.TryGetConfigIdByUuid(long, out int)
    private object?       _boxHp, _boxMaxHp, _boxCombatState, _boxInteractionStage, _boxCollectCounter;

    private bool EnsureApi()
    {
        if (_apiResolved) return _apiOk;
        _apiResolved = true;
        try
        {
            var entMgr   = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
            var entType  = StellarInterop.FindType("Panda.ZGame.ZEntity");
            var modelT   = StellarInterop.FindType("Panda.ZGame.ZModel");
            var attrEnum = StellarInterop.FindType("Zproto.EAttrType");
            var helper   = StellarInterop.FindType("Panda.ZGame.ZEntityHelper");
            if (entMgr == null || entType == null || modelT == null || attrEnum == null)
            {
                _services.Log.Warning($"[Gather] type resolve failed entMgr={entMgr != null} ent={entType != null} model={modelT != null} attr={attrEnum != null}");
                return false;
            }

            const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;
            _piEntMgrInstance = entMgr.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            foreach (var m in entMgr.GetMethods(pubInst))
                if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                { _miGetEntity = m; break; }

            foreach (var m in entType.GetMethods(pubInst))
            {
                if (m.Name != "GetAttr" || !m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType == attrEnum && ps[1].ParameterType == typeof(bool))
                { _miGetAttrLong = m.MakeGenericMethod(typeof(long)); break; }
            }

            _piModel       = entType.GetProperty("Model", pubInst);
            _piModelGoComp = modelT.GetProperty("ModelGoComp", pubInst);
            _piPosition    = _piModelGoComp?.PropertyType?.GetProperty("Position", pubInst);
            _miTryGetConfigId = ResolveTryGetConfigId(helper);
            if (_miTryGetConfigId == null)
                _services.Log.Warning("[Gather] ZEntityHelper.TryGetConfigIdByUuid unresolved — node config-id lookup unavailable, roster will be EMPTY (not 'no nodes nearby')");

            _boxHp               = Enum.ToObject(attrEnum, AttrHp);
            _boxMaxHp            = Enum.ToObject(attrEnum, AttrMaxHp);
            _boxCombatState      = Enum.ToObject(attrEnum, AttrCombatState);
            _boxInteractionStage = Enum.ToObject(attrEnum, AttrInteractionStage);
            _boxCollectCounter   = Enum.ToObject(attrEnum, AttrCollectCounter);

            _apiOk = _piEntMgrInstance != null && _miGetEntity != null && _miGetAttrLong != null
                     && _piModel != null && _piModelGoComp != null && _piPosition != null && _miTryGetConfigId != null;
            _services.Log.Info($"[Gather] api ok={_apiOk} getEnt={_miGetEntity != null} getAttr={_miGetAttrLong != null} " +
                $"model={_piModel != null} pos={_piPosition != null} cfgId={_miTryGetConfigId != null}");
            return _apiOk;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Gather] api resolve error: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    // ZEntityHelper.TryGetConfigIdByUuid(long uuid, out int configId) — static, [ForceToLua] (dump 234187).
    private static MethodInfo? ResolveTryGetConfigId(Type? helper)
    {
        if (helper == null) return null;
        foreach (var m in helper.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "TryGetConfigIdByUuid") continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(long) && ps[1].IsOut) return m;
        }
        return null;
    }
}
