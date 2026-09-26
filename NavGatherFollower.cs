using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DotRecast.Detour;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.AutoGather;

// Navmesh corridor FOLLOWER for Auto-Gather — the runtime nav ported from StellarExperimentPlugin's NavPathTester,
// stripped to the ONE thing Auto-Gather needs: given the player pos and the target node pos each frame, return the
// world point to STEER TOWARD so the character walks the navmesh corridor around walls/cliffs instead of straight
// into them. It does NOT drive movement (AutoGather's PlayerMoveDriver does) and it does NOT bake (the shipped
// <sceneFile>.navmesh.bin under <GameInstallDir>\stellar\navmesh\ is loaded read-only).
//
// GRACEFUL DEGRADATION is the contract: no navmesh for the scene, no scene_map entry, DotRecast unavailable, an
// off-mesh player, or an unreachable/exhausted-reroute node all resolve to "return nodePos" (the old straight-line
// behaviour). AutoGather's own StuckTooLong / NoProgress watchdogs stay the authority on skipping a node — the
// follower never hard-stops the gather loop; it only improves the path when it can.
//
// Per-node lifecycle: SteerTo(targetUuid) computes a corridor the first time it sees a new uuid, then advances the
// player through the waypoints (funnel + wall-clearance from NavMeshNavigator), layering a forward sphere-cast local
// avoidance (PhysxSphereCaster) for props the coarse mesh stepped over, and rerouting (blacklist poly + re-plan)
// when the player physically stalls on a spot the mesh baked walkable. Reset() drops the per-node state (ClearPath).
internal sealed class NavGatherFollower
{
    // Follow tuning — carried over verbatim from NavPathTester (validated in-game 2026-09-02/03).
    private const float AdvanceRadius     = 0.6f;   // horizontal dist to pop the current waypoint (≈ eroded corner clearance)
    private const float StuckSeconds      = 2.5f;   // no straight-line progress for this long → blacklist + reroute
    private const float StepStuckSeconds  = 1.5f;   // FAST stall timer while driving straight into a fused step (StepUp)
    private const float StuckEps          = 0.5f;   // "progress" = closing the remaining distance by at least this much
    private const int   MaxReroutes       = 6;      // per-node cap on stuck→blacklist→re-plan cycles

    // Local obstacle avoidance (around small props the coarse navmesh stepped over).
    private const float BodyRadius = 0.5f;   // sphere-cast radius ≈ player capsule radius
    private const float ProbeAhead = 3.5f;   // how far ahead to test the straight leg (clamped to waypoint dist)
    private const float SteerAhead = 5.0f;   // when detouring, steer this far along the chosen clear direction
    private static readonly float[] AvoidAngles = { 30f, 45f, 60f, 90f };   // fan-out magnitudes (both sides)
    // Step-up vs wall discrimination: the bake FUSES climbable steps/curbs (walkableClimb≈0.8) into walkable polys, so
    // a low curb sits ON the navmesh — but the forward avoidance sphere (origin up*0.5, r 0.5) spans ~0–1.0 m and reads
    // that curb as a solid wall, then fans out around a step it should just walk UP. When the primary forward cast is
    // blocked we re-probe at CHEST height (above the max climbable step): clear up there ⇒ climbable step → drive
    // straight in and let the character controller auto-step; blocked up there too ⇒ real wall → fan out.
    private const float EyeCastHeight      = 0.5f;   // primary forward probe origin above feet (skips the ground plane)
    private const float StepLowCastHeight  = 0.2f;   // near-feet confirm cast origin (diagnostic)
    private const float StepLowCastRadius  = 0.3f;   // small radius for the low confirm cast
    private const float AgentMaxClimb      = 0.8f;   // matches the bake's walkableClimb
    private const float StepHighCastHeight = AgentMaxClimb + 0.4f;   // ≈1.2 m: chest height ABOVE the max climbable step

    private enum AvoidState { Straight, StepUp, Detour, BoxedIn }

    private readonly IPluginServices  _services;
    private readonly NavMeshNavigator _navigator = new();   // shared corridor query (funnel + wall-clearance + climb-averse + blacklist)
    private readonly PhysxSphereCaster _caster;             // forward sphere-cast for local avoidance

    // Loaded navmesh, keyed by CurrentSceneId. _navLoadFailedScene remembers a scene whose file is missing so we don't
    // hit the disk every frame.
    private DtNavMesh?      _navMesh;
    private DtNavMeshQuery? _query;
    private int             _navScene           = int.MinValue;
    private int             _navLoadFailedScene = int.MinValue;

    // Per-node corridor state.
    private readonly List<Vector3> _waypoints = new();
    private int    _wpIndex;
    private long   _activeUuid;        // node the current corridor was planned for (0 = none)
    private bool   _corridorFailed;    // planning/rerouting gave up for _activeUuid → straight-line until the uuid changes
    private double _stuckSince;
    private float  _bestRemaining;
    private int    _rerouteCount;

    // Avoidance / diagnostics scratch (mirrors NavPathTester).
    private float  _lastAvoidSign = 1f;
    private double _lastAvoidNoticeAt, _lastStepUpNoticeAt;
    private bool   _lastPrimaryBlocked, _lastLowBlocked, _lastHighBlocked;
    private float  _lastClearAngle;
    private int    _diagLoggedForWp = -1;

    public NavGatherFollower(IPluginServices services)
    {
        _services = services;
        _caster   = new PhysxSphereCaster(services);   // NOTE: this ctor does NOT touch DotRecast (the navigator field does)
    }

    // The seam. Called every approach frame with the resolved local player entity/pos, the target node pos, and the
    // node uuid. Returns the world point AutoGather should steer PlayerMoveDriver toward this frame. navUnreachable is
    // informational (the navmesh says the node can't be reached / is off-mesh) — the caller keeps straight-lining and
    // lets its own watchdogs skip the node; the follower never forces a skip.
    public Vector3 SteerTo(object localEnt, Vector3 localPos, Vector3 nodePos, long targetUuid, out bool navUnreachable)
    {
        navUnreachable = false;
        if (!EnsureNavMesh()) return nodePos;   // no navmesh for this scene / DotRecast unusable → straight-line

        // Plan on a NEW node. On a failed node we DON'T re-plan every frame (A* is not free) — hold straight-line
        // until the target uuid changes.
        if (targetUuid != _activeUuid)
        {
            _activeUuid = targetUuid;
            _corridorFailed = !BeginCorridor(localPos, nodePos);
        }
        if (_corridorFailed || _waypoints.Count == 0) { navUnreachable = true; return StraightWithAvoid(localPos, nodePos); }

        return Follow(localPos, nodePos, out navUnreachable);
    }

    // Drop all per-node corridor state (called by AutoGatherController.ClearPath on arrive/skip/reset).
    public void Reset()
    {
        _waypoints.Clear();
        _wpIndex = 0;
        _activeUuid = 0;
        _corridorFailed = false;
        _diagLoggedForWp = -1;
        _navigator.ClearBlockedPolys();
    }

    // ── Corridor planning ─────────────────────────────────────────────────────
    private bool BeginCorridor(Vector3 start, Vector3 nodePos)
    {
        _navigator.ClearBlockedPolys();
        _rerouteCount = 0;
        _diagLoggedForWp = -1;
        _waypoints.Clear();
        if (!_navigator.TryComputeCorridor(_navMesh!, _query!, start, nodePos, _waypoints, out string diag) || _waypoints.Count == 0)
        {
            _waypoints.Clear();
            _services.Log.Info($"[GatherNav] no corridor to node {_activeUuid} ({diag}) — straight-line");
            return false;
        }
        _wpIndex = 0;
        _bestRemaining = float.MaxValue;
        _stuckSince = _services.Framework.TimeNow;
        _services.Log.Info($"[GatherNav] corridor to node {_activeUuid}: {_waypoints.Count} wpts {NavMeshNavigator.HorizontalLength(_waypoints):F1}m ({diag})");
        return true;
    }

    // ── Per-frame corridor follow ─────────────────────────────────────────────
    private Vector3 Follow(Vector3 localPos, Vector3 nodePos, out bool navUnreachable)
    {
        navUnreachable = false;
        double now = _services.Framework.TimeNow;

        // Advance past every waypoint we're already within AdvanceRadius of OR have PASSED along its outgoing leg
        // (the latter progresses without pinpoint arrival while NOT turning early, which would cut the corner).
        while (_wpIndex < _waypoints.Count - 1)
        {
            Vector3 a = _waypoints[_wpIndex];
            float da = Horizontal(localPos, a);
            Vector3 legDir = _waypoints[_wpIndex + 1] - a; legDir.y = 0f;
            bool passed = false;
            if (legDir.sqrMagnitude > 1e-4f)
            {
                legDir.Normalize();
                Vector3 fromWp = localPos - a; fromWp.y = 0f;
                passed = Vector3.Dot(fromWp, legDir) >= 0f;
            }
            if (da < AdvanceRadius || passed)
            {
                _wpIndex++; _bestRemaining = float.MaxValue; _stuckSince = now; _diagLoggedForWp = -1;
                continue;
            }
            break;
        }

        Vector3 wp = _waypoints[_wpIndex];
        bool isLast = _wpIndex == _waypoints.Count - 1;
        float dist = Horizontal(localPos, wp);

        // Final corridor waypoint (≈ node) → hand the last leg back as a straight-line approach to nodePos; AutoGather's
        // own stop distance + watchdogs finish the arrival.
        if (isLast) return StraightWithAvoid(localPos, nodePos);

        // Local avoidance FIRST — its decision (step-up / detour / boxed-in) gates the reroute watchdog below.
        Vector3 steerTarget = ComputeAvoidSteer(localPos, wp, dist, out AvoidState state);
        if (state != AvoidState.Straight && _diagLoggedForWp != _wpIndex)
        { _diagLoggedForWp = _wpIndex; LogNavDiag("first block", localPos, wp, dist); }

        // Stuck watchdog: remaining distance to the current waypoint must keep shrinking. Don't accrue time while
        // actively maneuvering a step/detour — only pure straight-line / boxed-in no-progress rerouted.
        bool reroute = false;
        if (dist < _bestRemaining - StuckEps) { _bestRemaining = dist; _stuckSince = now; }
        else if (state == AvoidState.StepUp && now - _stuckSince > StepStuckSeconds) reroute = true;   // lone curb the controller can't climb
        else if (state == AvoidState.Detour) { _stuckSince = now; }                                    // legit go-around → don't accrue
        else if (now - _stuckSince > StuckSeconds) reroute = true;                                      // navmesh walkable, collision blocks it

        if (reroute)
        {
            if (TryReroute(localPos, nodePos, wp, dist))
                // Steer toward the new corridor's current waypoint this frame; full avoidance resumes next frame.
                return _waypoints.Count > 0 ? _waypoints[Mathf.Min(_wpIndex, _waypoints.Count - 1)] : nodePos;
            _corridorFailed = true;   // exhausted → straight-line until the node changes; caller's watchdogs skip it
            navUnreachable = true;
            return StraightWithAvoid(localPos, nodePos);
        }

        return steerTarget;
    }

    // Stuck → blacklist the poly we're failing to enter and re-plan from where we stand. Returns whether a fresh
    // corridor was found (false = give up: nothing to blame, the culprit IS the goal, no path, or the budget is spent).
    private bool TryReroute(Vector3 localPos, Vector3 nodePos, Vector3 wp, float dist)
    {
        _navigator.TryFindPolyAt(_query!, wp,      out long blockPoly);
        _navigator.TryFindPolyAt(_query!, nodePos, out long goalPoly);
        LogNavDiag("stuck", localPos, wp, dist);
        _services.Log.Info($"[GatherNav] stuck wp {_wpIndex + 1}/{_waypoints.Count} (dist={dist:F2}m) — " +
            $"blockPoly=0x{blockPoly:X} goalPoly=0x{goalPoly:X} reroute {_rerouteCount}/{MaxReroutes}");

        if (blockPoly == 0 || blockPoly == goalPoly || _rerouteCount >= MaxReroutes) return false;
        _navigator.MarkPolyBlocked(blockPoly);

        var fresh = new List<Vector3>();
        if (!_navigator.TryComputeCorridor(_navMesh!, _query!, localPos, nodePos, fresh, out string diag) || fresh.Count == 0)
        { _services.Log.Info($"[GatherNav] reroute: no path after blocking 0x{blockPoly:X} ({diag})"); return false; }

        _waypoints.Clear();
        _waypoints.AddRange(fresh);
        _wpIndex = 0;
        _bestRemaining = float.MaxValue;
        _stuckSince = _services.Framework.TimeNow;
        _diagLoggedForWp = -1;
        _rerouteCount++;
        _services.Log.Info($"[GatherNav] re-routing ({_rerouteCount}): {fresh.Count} wpts, {_navigator.BlockedCount} poly(s) blocked, {NavMeshNavigator.HorizontalLength(fresh):F1}m");
        return true;
    }

    // Straight-line FALLBACK steer (no corridor / unreachable / final leg): head for the node but still fan around any
    // PHYSICAL prop in the way. ComputeAvoidSteer is pure PhysX sphere-casting (navmesh-independent), so it's valid even
    // when the mesh gave up — an open leg returns nodePos unchanged (forward cast clear), a stone gets steered around,
    // and its own probe<0.5 m guard disables avoidance in the final approach so it won't fight AutoGather's stop dist.
    private Vector3 StraightWithAvoid(Vector3 localPos, Vector3 nodePos)
    {
        float distToNode = Horizontal(localPos, nodePos);
        return ComputeAvoidSteer(localPos, nodePos, distToNode, out _);
    }

    // Steer point for this frame: straight at the waypoint when the leg ahead is clear; otherwise a local detour around
    // the blocking prop (or a straight-into-it step-up when the obstacle is a climbable step). Only deviates when a
    // forward sphere-cast is actually blocked, and resumes toward the waypoint the moment it clears.
    private Vector3 ComputeAvoidSteer(Vector3 pos, Vector3 wp, float distToWp, out AvoidState state)
    {
        state = AvoidState.Straight;
        _lastPrimaryBlocked = false; _lastLowBlocked = false; _lastHighBlocked = false; _lastClearAngle = float.NaN;
        Vector3 to = wp - pos; to.y = 0f;
        if (to.sqrMagnitude < 1e-4f || !_caster.Ready) return wp;   // at waypoint, or no caster → straight
        Vector3 dir = to.normalized;
        float probe = Mathf.Min(distToWp, ProbeAhead);              // don't probe past the waypoint
        if (probe < 0.5f) return wp;

        Vector3 fromEye = pos + Vector3.up * EyeCastHeight;         // cast a bit above the feet (skip the ground plane)
        if (!_caster.Blocked(fromEye, BodyRadius, fromEye + dir * probe)) return wp;   // clear → straight at the waypoint
        _lastPrimaryBlocked = true;

        // Blocked forward. Discriminate a CLIMBABLE STEP (curb/ledge/rock fused into walkable) from a real WALL:
        //   • high cast at chest height (above the max climbable step) — CLEAR up there ⇒ low step → walk straight up.
        //   • low cast near the feet (small radius) — confirms the obstacle is actually there (diagnostics only).
        Vector3 highFrom = pos + Vector3.up * StepHighCastHeight;
        _lastHighBlocked = _caster.Blocked(highFrom, BodyRadius, highFrom + dir * probe);
        Vector3 lowFrom = pos + Vector3.up * StepLowCastHeight;
        _lastLowBlocked = _caster.Blocked(lowFrom, StepLowCastRadius, lowFrom + dir * probe);

        if (!_lastHighBlocked)   // obstacle low but chest-height clear ⇒ step-up within climb height → drive straight in
        {
            state = AvoidState.StepUp;
            NoteStepUp();
            return wp;   // the character controller auto-steps up
        }

        // Real wall (blocked low AND high): fan out, smallest deviation first, preferring the side used last (hysteresis).
        foreach (float ang in AvoidAngles)
            for (int s = 0; s < 2; s++)
            {
                float sign = (s == 0) ? _lastAvoidSign : -_lastAvoidSign;
                Vector3 cand = Quaternion.AngleAxis(sign * ang, Vector3.up) * dir;
                if (!_caster.Blocked(fromEye, BodyRadius, fromEye + cand * probe))
                {
                    _lastAvoidSign = sign;
                    _lastClearAngle = sign * ang;
                    state = AvoidState.Detour;
                    NoteAvoiding(ang);
                    return pos + cand * SteerAhead;
                }
            }

        state = AvoidState.BoxedIn;
        NoteAvoiding(-1f);   // boxed in → straight at the waypoint; the stuck watchdog is the backstop
        return wp;
    }

    private void NoteAvoiding(float ang)
    {
        double now = _services.Framework.TimeNow;
        if (now - _lastAvoidNoticeAt < 2.0) return;   // throttle
        _lastAvoidNoticeAt = now;
        _services.Log.Info(ang > 0f ? $"[GatherNav] avoiding obstacle (detour {ang:F0}°)" : "[GatherNav] avoiding obstacle (boxed in)");
    }

    private void NoteStepUp()
    {
        double now = _services.Framework.TimeNow;
        if (now - _lastStepUpNoticeAt < 2.0) return;   // throttle
        _lastStepUpNoticeAt = now;
        _services.Log.Info("[GatherNav] stepping up (climbable step ahead — walking straight in)");
    }

    private void LogNavDiag(string tag, Vector3 pos, Vector3 wp, float dist)
    {
        Vector3 next = (_wpIndex + 1 < _waypoints.Count) ? _waypoints[_wpIndex + 1] : wp;
        string cast = !_lastPrimaryBlocked ? "fwd=clear"
            : $"low={(_lastLowBlocked ? "blk" : "clr")} high={(_lastHighBlocked ? "blk" : "clr")} " +
              (float.IsNaN(_lastClearAngle) ? (_lastHighBlocked ? "boxed-in" : "step-up") : $"clear@{_lastClearAngle:F0}°");
        _services.Log.Info($"[GatherNav] {tag}: pos=({pos.x:F1},{pos.y:F1},{pos.z:F1}) wp{_wpIndex + 1}/{_waypoints.Count}=" +
            $"({wp.x:F1},{wp.y:F1},{wp.z:F1}) dyToWp={wp.y - pos.y:F2} distH={dist:F2} " +
            $"next=({next.x:F1},{next.y:F1},{next.z:F1}) cast[{cast}]");
    }

    private static float Horizontal(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

    // ── Navmesh load (keyed by CurrentSceneId → scene_map → <file>.navmesh.bin) ──
    private bool EnsureNavMesh()
    {
        int scene = ReadSceneId();
        if (scene == 0) return false;
        if (_navMesh != null && _query != null && _navScene == scene) return true;
        if (_navLoadFailedScene == scene) return false;   // already tried & missing → don't hit the disk every frame

        // New scene we haven't resolved yet → drop the stale mesh + any corridor planned against it, then load.
        _navMesh = null; _query = null; Reset();

        string? file = CurrentSceneFile(scene);
        if (file == null)
        {
            _navLoadFailedScene = scene;
            _services.Log.Info($"[GatherNav] no navmesh for scene {scene} (no scene_map entry) — straight-line only");
            return false;
        }
        try
        {
            DtNavMesh? mesh = LoadNavMesh(file, out string source);
            if (mesh == null)
            {
                _navLoadFailedScene = scene;
                _services.Log.Info($"[GatherNav] no navmesh for scene {scene} (file '{file}' not embedded and not in game folder) — straight-line only");
                return false;
            }
            _navMesh = mesh;
            _query = new DtNavMeshQuery(_navMesh);
            _navScene = scene;
            _navLoadFailedScene = int.MinValue;
            _services.Log.Info($"[GatherNav] loaded {file} ({source}) — scene {scene}");
            return true;
        }
        catch (Exception ex)
        {
            _navLoadFailedScene = scene;
            _services.Log.Warning($"[GatherNav] navmesh load failed for scene {scene} (file '{file}'): {ex.Message} — straight-line only");
            return false;
        }
    }

    // Resolution order for a scene's <file>.navmesh.bin: EMBEDDED (merged into the plugin DLL) first, then the shared
    // game-folder copy, then null (caller straight-lines). Embedding makes the plugin self-contained; the game folder
    // stays a fallback so a locally-dropped/updated navmesh still wins if the embedded set lacks the scene.
    private DtNavMesh? LoadNavMesh(string file, out string source)
    {
        // a. Embedded resource — the deterministic LogicalName built from the scene's `file` token (matches the csproj
        //    transform "Stellar.AutoGather.navmesh.%(Filename)%(Extension)"). Dynamic name by design — the registry's
        //    check-embedded-resources.py only validates single-literal GetManifestResourceStream calls, not this one.
        string resource = $"Stellar.AutoGather.navmesh.{file}.navmesh.bin";
        using (var s = typeof(NavGatherFollower).Assembly.GetManifestResourceStream(resource))
        {
            if (s != null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                source = "embedded";
                return NavmeshLoader.ReadBytes(ms.ToArray());
            }
        }
        // b. Game folder (<GameInstallDir>\stellar\navmesh\<file>.navmesh.bin) — separate game data, left in place.
        string path = Path.Combine(NavmeshDir, file + ".navmesh.bin");
        if (File.Exists(path))
        {
            source = "game folder";
            return NavmeshLoader.Read(path);
        }
        source = "missing";
        return null;   // c. not embedded and not on disk → caller disables nav for the scene (straight-line)
    }

    // Bokura.Table.ITable.CurrentSceneId (static int) — clean C# read via reflection, resolved once.
    private bool          _sceneIdTried;
    private PropertyInfo? _piSceneId;
    private FieldInfo?    _fiSceneId;
    private int ReadSceneId()
    {
        if (!_sceneIdTried)
        {
            _sceneIdTried = true;
            var t = StellarInterop.FindType("Bokura.Table.ITable");
            if (t != null)
            {
                const BindingFlags sa = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
                _piSceneId = t.GetProperty("CurrentSceneId", sa);
                if (_piSceneId == null) _fiSceneId = t.GetField("CurrentSceneId", sa);
            }
        }
        try { if (_piSceneId != null) return Convert.ToInt32(_piSceneId.GetValue(null)); } catch { }
        try { if (_fiSceneId != null) return Convert.ToInt32(_fiSceneId.GetValue(null)); } catch { }
        return 0;
    }

    // Shipped scene_map.json → the scene's navmesh `file` token (e.g. "fld001"), which LoadNavMesh turns into either an
    // embedded resource name or a game-folder path. Null when we can't key the scene (no map / no entry) → straight-line.
    private Dictionary<int, (string name, string file)>? _sceneMap;
    private bool _sceneMapLoaded;
    private string? CurrentSceneFile(int scene)
    {
        EnsureSceneMap();
        if (_sceneMap != null && _sceneMap.TryGetValue(scene, out var e) && e.file.Length > 0)
            return e.file;
        return null;
    }

    private void EnsureSceneMap()
    {
        if (_sceneMapLoaded) return;
        _sceneMapLoaded = true;
        // Embedded scene_map.json (merged into the plugin DLL); a file next to the DLL is honored only as a dev override.
        _sceneMap = SceneMap.Load(Path.Combine(PluginDir, "scene_map.json"));
        if (_sceneMap != null) _services.Log.Info($"[GatherNav] scene_map loaded: {_sceneMap.Count} scenes");
        else _services.Log.Warning("[GatherNav] scene_map unavailable — navmesh disabled, straight-line only");
    }

    // Plugin folder (<GameInstallDir>\stellar\plugins\autogather) and the shared navmesh dir two levels up.
    private static string PluginDir =>
        Path.GetDirectoryName(typeof(Plugin).Assembly.Location) is { Length: > 0 } d ? d : AppContext.BaseDirectory;
    private static string NavmeshDir => Path.GetFullPath(Path.Combine(PluginDir, "..", "..", "navmesh"));
}
