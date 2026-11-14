using System;

namespace Stellar.AutoGather;

// Focus bookkeeping for Auto-Gather (KB Auto-Gather-and-Interaction.md §5b): the Focus LIMIT (stop after spending N Focus
// this run), the per-run counters the window shows (Focus spent, nodes gathered), why the loop last stopped itself, the
// pause reason, and a ~1 s-throttled cache of the live Focus balance for the UI.
//
// Spent = the SUM of the locked node's Focus cost over each SUCCESSFUL Focus gather this run — NOT a balance delta,
// because Focus regenerates while you gather (a delta would under-count and let the run overshoot the limit). Reset on
// Start. The limit is a manual setting (Rule 12: never auto-learned); raising it while running takes effect on the very
// next seek because the gate re-reads FocusLimit every time.
internal sealed partial class AutoGatherController
{
    public const int NoFocusLimit = -1;   // FocusLimit value meaning "never stop for the limit"

    public enum StopReason { None, FocusLimit, OutOfFocus }
    public enum PauseKind  { None, Combat, Dead, Popup }

    private int        _focusLimit = 200;   // set by the host from config (autogather_focus_limit)
    private int        _focusSpent;          // Focus spent by successful Focus gathers this run
    private int        _gatheredCount;       // successful gathers this run (any mode)
    private bool       _hasRun;              // a run has been started this session (the window then shows the counters)
    private bool       _lastFiredFocus;      // the in-flight channel was started on the Focus option
    private StopReason _lastStop = StopReason.None;
    private PauseKind  _pause    = PauseKind.None;

    private int    _focusBal = -1;            // last read Focus balance; -1 = unknown / not read yet
    private double _focusBalReadAt = -999;    // throttle stamp for the UI-driven balance read

    public bool       Enabled       => _enabled;
    public int        FocusSpent    => _focusSpent;
    public int        GatheredCount => _gatheredCount;
    public bool       HasRun        => _hasRun;
    public StopReason LastStop      => _lastStop;
    public PauseKind  Pause         => _enabled ? _pause : PauseKind.None;

    // NoFocusLimit (-1) or 0..N Focus. Only Focus mode honours it.
    public int FocusLimit
    {
        get => _focusLimit;
        set { if (value != _focusLimit) { _focusLimit = value; _services.Log.Info($"[Gather] focus limit = {(value < 0 ? "none" : value.ToString())}"); } }
    }

    // Live Focus balance for the window, read at most once a second and only when something asks (the window's value
    // Funcs only run while it is shown). The seek gate and every BeginInteraction fire also refresh it for free.
    public int FocusBalance
    {
        get
        {
            double now = _services.Framework.TimeNow;
            if (now - _focusBalReadAt >= 1.0)
            {
                _focusBalReadAt = now;
                if (TryReadFocusBalance(out int bal)) _focusBal = bal;
            }
            return _focusBal;
        }
    }

    // Focus (item 20003) cost of the LOCKED type (0 = normal-only / unknown / not resolved yet).
    private int LockedFocusCost => (_lockedConfigId != 0 && _cfgCache.TryGetValue(_lockedConfigId, out var c)) ? c.Cost : 0;

    // Start pressed: a new run counts from zero and any earlier "stopped" reason is cleared.
    private void BeginRun()
    {
        _focusSpent = 0;
        _gatheredCount = 0;
        _lastFiredFocus = false;
        _lastStop = StopReason.None;
        _pause = PauseKind.None;
        _hasRun = true;
    }

    // Called on every completed gather. Only a channel that was started on the Focus option spends Focus.
    private void CountGather()
    {
        _gatheredCount++;
        if (_lastFiredFocus && _targetFocusCost > 0) _focusSpent += _targetFocusCost;
        _lastFiredFocus = false;
    }

    // Would gathering one more node of cost `cost` break the limit? (Focus mode only; no limit → never.)
    private bool FocusLimitReached(int cost) => _focusLimit >= 0 && _focusSpent + cost > _focusLimit;

    // Current Focus (item 20003) balance; false/-1 when unreadable. Read via the game items VM (same call FireBeginInteraction uses).
    private bool TryReadFocusBalance(out int bal)
    {
        bal = -1;
        if (!_services.Lua.Ready) return false;
        // DOT call: GetItemTotalCount has no self param — a colon passes the VM as configId and drops 20003 (see condition_helper.lua:623).
        _services.Lua.DoString("pcall(function() rawset(_G,'__ag_bal', (Z.VMMgr.GetVM('items')).GetItemTotalCount(20003)) end)");
        if (_services.Lua.TryReadGlobalNumber("__ag_bal", out var v)) { bal = (int)v; _focusBal = bal; return bal >= 0; }
        return false;
    }

    // Strict Focus mode ran out of Focus → stop the whole loop rather than silently doing a Normal gather or grinding
    // through nodes. Tears down like SetEnabled(false) then reports a clear status + notifies the host.
    private void StopOutOfFocus()
    {
        _services.Log.Info("[Gather] out of Focus — stopping auto-gather (strict Focus mode)");
        StopSelf(StopReason.OutOfFocus, "ag.status.outOfFocus");
    }

    // The Focus limit would be exceeded by the next gather → same clean stop as out-of-Focus, different reason.
    private void StopFocusLimit(int cost)
    {
        _services.Log.Info($"[Gather] focus limit reached (spent={_focusSpent} + cost={cost} > limit={_focusLimit}) — stopping");
        StopSelf(StopReason.FocusLimit, "ag.status.focusLimit");
    }

    private void StopSelf(StopReason why, string statusKey)
    {
        SetEnabled(false);              // full teardown (unsubscribe, stop move, abort channel)
        _lastStop = why;
        SetStatus(_loc.T(statusKey));   // override the empty status SetEnabled just set
        OnAutoStopped?.Invoke();        // host clears autogather_enabled → the button flips back to Start
    }

    // ── cached activity strings (SetStatus runs every frame while enabled — never format per frame) ──────────────
    private int     _approachM = int.MinValue;
    private string? _approachLang;
    private string  _approachText = "";

    private string ApproachStatus(float dist)
    {
        int m = (int)MathF.Round(dist);
        string lang = _loc.Language;
        if (m != _approachM || !ReferenceEquals(lang, _approachLang))
        {
            _approachM = m; _approachLang = lang;
            _approachText = _loc.TFormat("ag.status.approaching", m);
        }
        return _approachText;
    }

    private int     _noNodesId = int.MinValue;
    private string? _noNodesLang;
    private string  _noNodesText = "";

    private string NoNodesStatus()
    {
        if (_lockedConfigId == 0) return _loc.T("ag.status.pickType");
        string lang = _loc.Language;
        if (_lockedConfigId != _noNodesId || !ReferenceEquals(lang, _noNodesLang))
        {
            _noNodesId = _lockedConfigId; _noNodesLang = lang;
            _noNodesText = _loc.TFormat("ag.status.noNodes", LockedName);
        }
        return _noNodesText;
    }
}
