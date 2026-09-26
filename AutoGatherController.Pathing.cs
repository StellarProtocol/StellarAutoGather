using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.AutoGather;

// Approach-phase steering + the movement-independent skip watchdogs. Movement follows the navmesh CORRIDOR toward the
// node (via NavGatherFollower — routes around walls/cliffs the coarse straight-line walked into), degrading gracefully
// to STRAIGHT-LINE whenever the nav can't help (no navmesh for the scene, off-mesh, unreachable, or DotRecast missing).
// The no-progress + IsStuckInPlace watchdogs stay the safety net: when a node genuinely can't be reached they skip it.
// The getPrimitiveInputDir inject (PlayerMoveDriver, shared with Follow) is NOT modified — we only pick the steer point.
internal sealed partial class AutoGatherController
{
    private const double StuckSkipSecs      = 1.2;   // IsStuckInPlace continuous ≥ this → skip the node
    private const double NoProgressSkipSecs = 2.5;   // distance-to-node not improving for this long → skip (wall-slide)
    private const float  ProgressEps        = 0.3f;  // min distance improvement that counts as progress

    private double _stuckSince = -1;
    private bool   _approachSkip;   // set by ComputeApproachSteer when NavmeshFallback is OFF and the node is nav-unreachable; TickApproach then skips it

    // No-progress watchdog state (catches wall-sliding, where IsStuckInPlace doesn't trip).
    private long   _progressUuid;
    private float  _bestApproachDist = float.MaxValue;
    private double _progressAt;

    // Navmesh follower — lazily constructed on first use so ALL DotRecast contact sits behind the try/catch below. If
    // its construction or a steer throws (DotRecast DLLs missing/unloadable), it's disabled for good and we straight-line.
    private NavGatherFollower? _follower;
    private bool               _navFollowerDisabled;

    // Entry point from TickApproach: the world point to steer toward this frame. Delegates to the navmesh follower for a
    // corridor-aware steer point (returns nodePos itself for the final approach / any nav fallback). navUnreachable means
    // the follower found no route: with NavmeshFallback ON we still straight-line (the StuckTooLong / NoProgress
    // watchdogs then skip it if genuinely stuck); with NavmeshFallback OFF we set _approachSkip so TickApproach skips it
    // immediately. A coverage gap / DotRecast-disabled / !UseNavmesh path never sets the flag, so it always straight-lines.
    private Vector3 ComputeApproachSteer(object localEnt, Vector3 localPos, Vector3 nodePos)
    {
        _approachSkip = false;
        Vector3 flat = nodePos - localPos; flat.y = 0f;
        if (flat.sqrMagnitude < 1e-4f) return nodePos;   // basically on top of the node → straight

        if (!UseNavmesh) return nodePos;                 // user forced direct nav → straight-line, never touch the follower/DotRecast
        if (_navFollowerDisabled) return nodePos;        // DotRecast unavailable → permanent straight-line
        try
        {
            _follower ??= new NavGatherFollower(_services);   // first touch of DotRecast (NavMeshNavigator field init)
            Vector3 steer = _follower.SteerTo(localEnt, localPos, nodePos, _targetUuid, out bool navUnreachable);
            if (!NavmeshFallback && navUnreachable) _approachSkip = true;   // fallback OFF → skip the node instead of straight-lining
            return steer;
        }
        catch (Exception ex)
        {
            _navFollowerDisabled = true;
            _services.Log.Warning($"[Gather] navmesh follower disabled — straight-line only: {ex.InnerException?.Message ?? ex.Message}");
            return nodePos;
        }
    }

    // Cheap reset of the watchdog state + the follower's per-node corridor. Called on arrival/skip/reset.
    private void ClearPath()
    {
        _stuckSince = -1;
        _progressUuid = 0;
        _bestApproachDist = float.MaxValue;
        _focusRetryUntil = 0;   // don't let a strict-Focus retry deadline leak across nodes
        try { _follower?.Reset(); } catch { }
    }

    // No-progress backstop: if distance-to-node hasn't improved by > ProgressEps for NoProgressSkipSecs → skip.
    private bool NoProgress(float distToNode)
    {
        double now = _services.Framework.TimeNow;
        if (_targetUuid != _progressUuid) { _progressUuid = _targetUuid; _bestApproachDist = float.MaxValue; _progressAt = now; }
        if (distToNode < _bestApproachDist - ProgressEps) { _bestApproachDist = distToNode; _progressAt = now; return false; }
        if (now - _progressAt < NoProgressSkipSecs) return false;
        _services.Log.Info($"[Gather] no progress {NoProgressSkipSecs:F1}s — skipping node {_targetUuid}");
        return true;
    }

    // Universal skip: IsStuckInPlace true continuously ≥ StuckSkipSecs → give up on this node. Logs the rising edge.
    private bool StuckTooLong(object localEnt)
    {
        double now = _services.Framework.TimeNow;
        if (!ReadIsStuck(localEnt)) { _stuckSince = -1; return false; }
        if (_stuckSince < 0) { _stuckSince = now; _services.Log.Info("[Gather] IsStuckInPlace rising edge"); return false; }
        double dur = now - _stuckSince;
        if (dur >= StuckSkipSecs) { _services.Log.Info($"[Gather] stuck {dur:F1}s — skipping node {_targetUuid}"); return true; }
        return false;
    }

    // ── ZCharController stuck read: ZEntity.CtrlComp → CharCtrl → IsStuckInPlace ──────────────────────────────
    private PropertyInfo? _piCtrlComp, _piCharCtrl, _piIsStuck;
    private bool _stuckApiTried, _stuckUnavailLogged;

    private object? GetCharCtrl(object localEnt)
    {
        if (_piCtrlComp == null && !_stuckApiTried)
        {
            _stuckApiTried = true;
            _piCtrlComp = StellarInterop.FindType("Panda.ZGame.ZEntity")?.GetProperty("CtrlComp", BindingFlags.Public | BindingFlags.Instance);
        }
        var ctrl = _piCtrlComp?.GetValue(localEnt);
        if (ctrl == null) return null;
        _piCharCtrl ??= ctrl.GetType().GetProperty("CharCtrl", BindingFlags.Public | BindingFlags.Instance);
        var cc = _piCharCtrl?.GetValue(ctrl);
        if (cc == null || (cc is UnityEngine.Object uo && uo == null)) return null;   // IL2CPP fake-null guard
        return cc;
    }

    private bool ReadIsStuck(object localEnt)
    {
        try
        {
            var cc = GetCharCtrl(localEnt);
            if (cc == null) return StuckUnavailable();
            _piIsStuck ??= cc.GetType().GetProperty("IsStuckInPlace", BindingFlags.Public | BindingFlags.Instance);
            if (_piIsStuck == null) return StuckUnavailable();
            return _piIsStuck.GetValue(cc) is bool b && b;
        }
        catch { return StuckUnavailable(); }
    }

    private bool StuckUnavailable()
    {
        if (!_stuckUnavailLogged) { _stuckUnavailLogged = true; _services.Log.Info("[Gather] stuck-detect unavailable (CharCtrl chain) — no auto-skip"); }
        return false;
    }
}
