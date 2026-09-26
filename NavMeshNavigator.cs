using System;
using System.Collections.Generic;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using UnityEngine;

namespace Stellar.AutoGather;

// Shared navmesh corridor query, used by NavGatherFollower. FindNearestPoly (start+end) → FindPath (poly corridor) →
// FindStraightPath (funnel / shortest path) → SOFT WALL-CLEARANCE post-process → spline.
//
// ⭐ SOFT WALL-CLEARANCE (straight in the open, comfortable standoff off walls/props as room allows): the funnel's
// straight path runs STRAIGHT across wide-open polys, but the SHORTEST path hugs a wall on a long straight and pins
// convex corners to the agent-radius erosion margin (~0.5–0.6 m off the wall — the closest the turn gets), so the
// character grazes walls and can step up onto a low prop it brushes. We post-process the WHOLE path (straights
// included): densify long straights so there are points to move, then push EVERY interior point away from the nearest
// navmesh boundary — DotRecast's FindDistanceToWall — up to WallClearance, but ONLY as far as it stays on-mesh
// (TrySnap barely moved it) and never past the medial axis. In the open the full standoff lands in walkable space →
// the path holds off the wall; in a passage narrower than 2·WallClearance both walls push and the point settles at the
// CENTRE (never hugs the far wall); in a gap too tight for any push it collapses to the raw point → still passable.
// This unifies the old per-corner bisector push into one boundary-driven pass (FindDistanceToWall handles convex
// corners too). The graduated-clearance points + exact endpoints are the control polyline fed to the spline.
//
// ⭐ SEARCH EXTENT: the FindNearestPoly half-extent needs a LARGE vertical component (the query point's Y can sit
// several metres off the nearest navmesh poly). Wide Y, moderate XZ — matches the baker's (…,128,…) self-test.
internal sealed class NavMeshNavigator
{
    public const float ExtentXZ = 12f;
    public const float ExtentY  = 256f;
    private const float WallClearance = 1.0f;     // soft target standoff (m) from walls/props — taken only as far as walkable room allows (on-mesh, tapers to 0 in tight gaps). Tune here.
    private const int   WallClearSteps = 5;       // samples along the push while maximizing on-mesh clearance (centres the point in a passage narrower than 2·WallClearance)
    private const float SnapExtentXZ    = 2.0f;   // local XZ search when snapping an offset point back onto the navmesh
    private const float SnapEpsilonXZ   = 0.1f;   // KEEP a pushed point only if the snap moved it under this (m) horizontally — see below

    // ── Spline smoothing (final pass) ───────────────────────────────────────────────────────────────────────────────
    // Turns the funnel+arc POLYLINE into a smooth human-looking CURVE (centripetal Catmull-Rom), then re-clamps every
    // sample onto the navmesh so it can never bow into a wall. Purely runtime — no bake change.
    private const bool  EnableSplineSmoothing = true;   // master off-switch for the whole smoothing pass
    private const float SplineSpacing         = 0.5f;   // arc-length spacing (m) between sampled points along the curve

    // ── Douglas-Peucker simplification (final pass) ─────────────────────────────────────────────────────────────────
    private const float SimplifyTolerance     = 0.15f;  // max deviation (m) when dropping a waypoint — collapses near-straight wall-follow runs + the ~1 m clearance wobble, keeps real corners (which deviate more)

    // ⭐ CLIMB-AVERSE knob: cost added per metre of UPWARD gain along a traversed edge (see ClimbAverseFilter below).
    // At 4, climbing ~5 m up costs like a ~20 m flat detour, so A* takes any flatter route shorter than that. Higher =
    // avoid climbs harder / tolerate longer detours; too high = bizarre long way-rounds to dodge a trivial step.
    private const float ClimbCostPerMeter = 4f;

    private readonly long[]           _polyBuf     = new long[512];
    private readonly DtStraightPath[] _straightBuf = new DtStraightPath[512];

    // ONE reused filter instance so runtime-learned poly exclusions (below) PERSIST across TryComputeCorridor re-plans
    // within a single journey. When the follower physically stalls at a spot the navmesh thought walkable (navmesh ↔
    // runtime-collision mismatch), it blacklists that poly and re-plans; A* (FindPath) then routes AROUND it because
    // ClimbAverseFilter.PassFilter now rejects it. Cleared at the start of each fresh journey via ClearBlockedPolys.
    private readonly ClimbAverseFilter _filter = new();

    // Exclude a poly from future corridor queries (A* routes around it). Persists until ClearBlockedPolys.
    public void MarkPolyBlocked(long polyRef) => _filter.Block(polyRef);
    // Drop all poly exclusions — called at the start of a fresh journey.
    public void ClearBlockedPolys() => _filter.ClearBlocked();
    // How many polys are currently excluded.
    public int BlockedCount => _filter.BlockedCount;

    // Compute a clearance-aware world-space waypoint corridor from start to end. Returns false (with a diagnostic) on failure.
    public bool TryComputeCorridor(DtNavMesh navMesh, DtNavMeshQuery query, Vector3 start, Vector3 end, List<Vector3> waypoints, out string diag)
    {
        waypoints.Clear();
        // Climb-averse filter: same PassFilter/base cost as the default, plus an upward-gain penalty so A* prefers the
        // flatter corridor. REUSED instance (see field) so the blocked-poly set accumulated across re-plans is honored —
        // PassFilter now also rejects any blacklisted poly, steering A* around spots the follower proved un-crossable.
        var filter = _filter;
        var ext = new RcVec3f(ExtentXZ, ExtentY, ExtentXZ);
        var startPos = new RcVec3f(start.x, start.y, start.z);
        var endPos   = new RcVec3f(end.x, end.y, end.z);

        var s1 = query.FindNearestPoly(startPos, ext, filter, out long startRef, out RcVec3f startPt, out _);
        var s2 = query.FindNearestPoly(endPos,   ext, filter, out long endRef,   out RcVec3f endPt,   out _);
        bool startOk = s1.Succeeded() && startRef != 0;
        bool endOk   = s2.Succeeded() && endRef   != 0;
        if (!startOk || !endOk) { diag = $"startPolyValid={startOk} endPolyValid={endOk}"; return false; }

        var fp = query.FindPath(startRef, endRef, startPt, endPt, filter, _polyBuf.AsSpan(), out int nPolys, _polyBuf.Length);
        if (!fp.Succeeded() || nPolys == 0) { diag = $"FindPath status={fp} polys={nPolys}"; return false; }

        // Unreachable check: FindPath returns a PARTIAL path to the nearest reachable poly when the goal can't be
        // reached. Refuse it (don't navigate a partial corridor). Detect via DT_PARTIAL_RESULT AND the corridor's
        // last poly not being the snapped-goal poly (belt + suspenders).
        long lastPoly = _polyBuf[nPolys - 1];
        bool lastNeGoal = lastPoly != endRef;
        if (fp.IsPartial() || lastNeGoal)
        { diag = $"unreachable (partial={fp.IsPartial()}, polys={nPolys}, last≠goal={lastNeGoal})"; return false; }

        var sp = query.FindStraightPath(startPt, endPt, _polyBuf.AsSpan(0, nPolys), nPolys,
            _straightBuf.AsSpan(), out int nStraight, _straightBuf.Length, 0);
        if (!sp.Succeeded() || nStraight == 0) { diag = $"FindStraightPath status={sp} waypoints={nStraight}"; return false; }

        // Raw funnel points (straight in the open, corners hugged tight to the erosion margin).
        var raw = new List<Vector3>(nStraight);
        for (int i = 0; i < nStraight; i++) { var p = _straightBuf[i].pos; raw.Add(new Vector3(p.X, p.Y, p.Z)); }

        // SOFT WALL-CLEARANCE PASS (runtime-only, no bake change): DENSIFY long straights (~every SplineSpacing) so a
        // wall-hugging straight has interior points to move, then push EVERY interior point off the nearest navmesh
        // boundary up to WallClearance — as far as it stays on-mesh, tapering to nothing (or the corridor centre) in a
        // tight passage. Endpoints stay EXACT. One pass covers straights AND corners (FindDistanceToWall handles convex
        // corners), replacing the old per-corner bisector push.
        Densify(raw, SplineSpacing, waypoints);
        int pushed = 0;
        for (int i = 1; i < waypoints.Count - 1; i++)
            if (ClearWall(query, filter, waypoints[i], out Vector3 c)) { waypoints[i] = c; pushed++; }
        int controlCount = waypoints.Count;

        // ⚠️ We do NOT simplify before the spline: a point pushed only a little (a tight gap) has a small perpendicular
        // offset from its neighbours, so simplifying here would EAT the intentional standoff before the spline ever sees
        // it. Simplification runs ONCE, on the finished path below (Douglas-Peucker), where every standoff is baked in.
        //
        // FINAL pass: run the wall-cleared control polyline through a centripetal Catmull-Rom spline, re-sampled at a
        // fixed arc-length spacing and re-clamped on-mesh, so the path holds off walls and curves like a person walking
        // instead of straight legs with hard corners. Additive — funnel, climb filter, reroute untouched.
        int smoothed = EnableSplineSmoothing ? SmoothSpline(query, filter, waypoints) : waypoints.Count;

        // FINAL pass: Douglas-Peucker simplify. The densify (~0.5 m) + per-point wall push + spline leave a near-straight
        // wall-follow as a LONG, faintly-wavy run (points wobble 0.9↔1.0 m off an imperfect wall) — a 50 m straight can
        // carry ~100 near-collinear points. Collapse to the fewest waypoints that still trace the same SHAPE: a real
        // corner deviates far more than the wobble and survives; the wobble does not. Replaces the old collinear cull.
        SimplifyDouglasPeucker(waypoints, SimplifyTolerance);
        int simplified = waypoints.Count;

        diag = $"polys={nPolys} straight={nStraight} control={controlCount} pushed={pushed} smoothed={smoothed} simplified={simplified}";
        return waypoints.Count > 0;
    }

    // Densify `raw` into `outp`: keep every original point (endpoints and funnel corners), plus interior samples every
    // ~`spacing` (XZ) along each segment, so a wall-hugging STRAIGHT (no interior funnel points) gains points the
    // wall-clearance push can move. Uniform subdivision per segment; the endpoint of each segment is always emitted.
    private static void Densify(List<Vector3> raw, float spacing, List<Vector3> outp)
    {
        outp.Clear();
        if (raw.Count == 0) return;
        outp.Add(raw[0]);
        for (int i = 1; i < raw.Count; i++)
        {
            Vector3 a = raw[i - 1], b = raw[i];
            float dx = b.x - a.x, dz = b.z - a.z;
            int parts = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(dx * dx + dz * dz) / spacing));
            for (int k = 1; k < parts; k++) outp.Add(Vector3.Lerp(a, b, (float)k / parts));
            outp.Add(b);
        }
    }

    // SOFT wall/prop standoff: push a single on-mesh point away from the nearest navmesh boundary up to WallClearance,
    // but ONLY as far as it stays on-mesh and never past the corridor's medial axis. DotRecast's FindDistanceToWall gives
    // the distance `hitDist` to the nearest boundary and the boundary `hitNormal`; we orient the push outward
    // sign-independently (via center−hitPos, so a wrong-signed normal can NEVER push us into the wall), then sample the
    // push distance and KEEP the on-mesh candidate with the GREATEST wall clearance:
    //   • open space → clearance rises monotonically to WallClearance → we take the full push (comfortable standoff),
    //   • passage < 2·WallClearance → clearance peaks mid-gap → we stop at the CENTRE (both walls balance; no far-wall hug),
    //   • gap too tight → nothing on-mesh beats the origin → we keep the point (still passable — never blocks a passage).
    // Every emitted candidate is TrySnap'd on-mesh (moved < SnapEpsilonXZ, else rejected). Sets `pushed`; returns true if moved.
    private bool ClearWall(DtNavMeshQuery query, IDtQueryFilter filter, Vector3 p, out Vector3 pushed)
    {
        pushed = p;
        // FindDistanceToWall needs a start poly ref; snap p on-mesh to get one (and the exact on-mesh center).
        if (!TrySnapRef(query, filter, p, out long startRef, out Vector3 center, out _)) return false;
        var st = query.FindDistanceToWall(startRef, new RcVec3f(center.x, center.y, center.z), WallClearance, filter,
            out float hitDist, out RcVec3f hitPos, out RcVec3f hitNormal);
        if (!st.Succeeded() || hitDist >= WallClearance) return false;   // already ≥ standoff (or no wall in range) → leave it

        // Direction AWAY from the wall: prefer the boundary normal, but ORIENT it outward with center−hitPos (center is
        // on-mesh, hitPos is the wall) and fall back to that radial when the normal is degenerate. Sign-independent.
        Vector3 away = new Vector3(center.x - hitPos.X, 0f, center.z - hitPos.Z);
        Vector3 n = new Vector3(hitNormal.X, 0f, hitNormal.Z);
        if (Vector3.Dot(n, away) < 0f) n = -n;
        if (n.sqrMagnitude <= 1e-6f) n = away;
        if (n.sqrMagnitude <= 1e-6f) return false;              // point sits exactly on the boundary, no direction → keep it
        n = n.normalized;

        float target = WallClearance - hitDist;                 // how far short of the standoff we are
        Vector3 best = center; float bestClear = hitDist;
        for (int i = 1; i <= WallClearSteps; i++)
        {
            float d = target * i / WallClearSteps;
            if (!TrySnapRef(query, filter, center + n * d, out long candRef, out Vector3 cand, out float moved)) continue;
            if (moved >= SnapEpsilonXZ) continue;               // fell off the eroded strip → reject (this is the taper)
            float clear = hitDist;
            if (query.FindDistanceToWall(candRef, new RcVec3f(cand.x, cand.y, cand.z), WallClearance, filter,
                    out float cd, out _, out _).Succeeded()) clear = cd;
            if (clear > bestClear + 1e-3f) { bestClear = clear; best = cand; }   // keep the largest-clearance on-mesh candidate
        }
        if ((best - center).sqrMagnitude <= 1e-6f) return false;
        pushed = best;
        return true;
    }

    // Nearest point ON the navmesh to p plus its poly ref (a small local XZ search). Also reports `movedXZ` = how far the
    // snap displaced p horizontally: ~0 means p was already on-mesh (a pushed offset is real clearance); a large value
    // means FindNearestPoly CLAMPED an off-mesh point back to the boundary (the caller then rejects the offset). Falls
    // back to p / ref 0 if nothing is found nearby (movedXZ=0).
    private static bool TrySnapRef(DtNavMeshQuery query, IDtQueryFilter filter, Vector3 p, out long polyRef, out Vector3 snapped, out float movedXZ)
    {
        polyRef = 0; snapped = p; movedXZ = 0f;
        var ext = new RcVec3f(SnapExtentXZ, ExtentY, SnapExtentXZ);
        var st = query.FindNearestPoly(new RcVec3f(p.x, p.y, p.z), ext, filter, out long r, out RcVec3f near, out _);
        if (st.Succeeded() && r != 0)
        {
            polyRef = r;
            snapped = new Vector3(near.X, near.Y, near.Z);
            float dx = snapped.x - p.x, dz = snapped.z - p.z;
            movedXZ = Mathf.Sqrt(dx * dx + dz * dz);
            return true;
        }
        return false;
    }

    // Convenience overload used by the spline pass (no poly ref needed).
    private static bool TrySnap(DtNavMeshQuery query, IDtQueryFilter filter, Vector3 p, out Vector3 snapped, out float movedXZ)
        => TrySnapRef(query, filter, p, out _, out snapped, out movedXZ);

    // Douglas-Peucker path simplification: keep the fewest points that still trace the same shape, dropping any point
    // whose perpendicular XZ deviation from the line between its kept neighbours stays under `tol`. A near-straight run
    // collapses to its two ends; a real corner deviates well past `tol` and survives. Operates in the horizontal XZ
    // plane (Y is snapped on-mesh anyway) and preserves the EXACT first/last points.
    //
    // Iterative with an EXPLICIT stack of index ranges — a naive recursive DP recurses once per retained point and can
    // blow the call stack on a multi-hundred-point corridor (this runs AFTER densify + spline). Each popped [lo,hi]
    // range finds its farthest interior point: if it deviates > tol, keep it and push both sub-ranges; else the whole
    // span flattens onto the lo→hi chord and every interior point in it is dropped.
    //
    // ⭐ Only REMOVES points (never moves a kept one), so every survivor is still the exact on-mesh point the spline
    // emitted. The straight left behind between two kept points stays within `tol` (0.15 m) of the original curve — far
    // inside the path's ~0.6 m+ wall clearance — so it can't shortcut into a wall; no extra on-mesh re-check is needed.
    private static void SimplifyDouglasPeucker(List<Vector3> wps, float tol)
    {
        int n = wps.Count;
        if (n < 3) return;                                      // 0–2 points: nothing to simplify

        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;                           // endpoints always survive (exact)
        var stack = new Stack<(int lo, int hi)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (lo, hi) = stack.Pop();
            if (hi - lo < 2) continue;                          // no interior point between lo and hi

            float maxDev = -1f; int split = -1;
            for (int i = lo + 1; i < hi; i++)
            {
                float dev = PerpDistXZ(wps[lo], wps[hi], wps[i]);
                if (dev > maxDev) { maxDev = dev; split = i; }
            }
            if (maxDev > tol)                                   // farthest point bends the chord too far to drop → keep it, recurse both halves
            {
                keep[split] = true;
                stack.Push((lo, split));
                stack.Push((split, hi));
            }
            // else: every interior point in [lo,hi] is within tol of the lo→hi chord → drop them all (leave keep=false)
        }

        int w = 0;
        for (int r = 0; r < n; r++) if (keep[r]) wps[w++] = wps[r];   // compact survivors in place, order preserved
        wps.RemoveRange(w, n - w);
    }

    // ── Centripetal Catmull-Rom spline smoothing ────────────────────────────────────────────────────────────────────
    // Replace the corner polyline in `wps` with a smooth curve that PASSES THROUGH every original waypoint, sampled at a
    // fixed arc-length spacing and clamped back onto the navmesh. Returns the new point count (== wps.Count on exit).
    //
    // ⭐ CENTRIPETAL parameterization (alpha = 0.5): the knot spacing between control points is chord^0.5, not uniform
    // (chord^0) — this is what kills the cusps/self-overshoot that uniform Catmull-Rom throws on sharp turns, so the
    // curve stays inside the corridor near tight corners instead of looping out through a wall.
    //
    // ⭐ ENDPOINTS: we DUPLICATE the first and last waypoints as phantom control points (cp[0]=wp[0], cp[n+1]=wp[n-1]).
    // A duplicated phantom coincides with its neighbour, so its centripetal knot delta would be zero → the Barry-Goldman
    // evaluator divides by (t1-t0)=0. KnotDelta clamps every delta to a tiny floor (KnotEps) so no denominator is ever
    // zero; the near-zero interval contributes a negligible slice of parameter space, and each segment still interpolates
    // its own two endpoints exactly (Catmull-Rom passes through p1 and p2 regardless of the tangent-defining p0/p3).
    //
    // ⭐ ON-MESH CLAMP is NON-NEGOTIABLE: the smoothed curve is intentionally a touch wider than the funnel (that's the
    // human look), so a sample can drift toward a wall — every sample is snapped via TrySnap and we emit the SNAPPED
    // on-mesh point (never the raw sample). A sample with no poly within the local snap search is dropped outright. The
    // exact original start and goal are forced as the first/last output points.
    private int SmoothSpline(DtNavMeshQuery query, IDtQueryFilter filter, List<Vector3> wps)
    {
        int n = wps.Count;
        if (n < 3) return n;                                    // straight shot (≤2 points) → nothing to curve

        Vector3 start = wps[0], goal = wps[n - 1];

        // Control points with duplicated-endpoint phantoms: cp[0]=wp[0], cp[1..n]=wp[0..n-1], cp[n+1]=wp[n-1].
        var cp = new Vector3[n + 2];
        cp[0] = wps[0];
        for (int i = 0; i < n; i++) cp[i + 1] = wps[i];
        cp[n + 1] = wps[n - 1];

        // Densely evaluate each interior segment (cp[i]→cp[i+1], i=1..n-1) into one fine polyline, so the arc-length
        // resample below is spacing-accurate regardless of segment curvature. Sub-steps are finer than the target spacing.
        var fine = new List<Vector3>(n * 8);
        fine.Add(cp[1]);
        for (int i = 1; i <= n - 1; i++)
        {
            Vector3 p0 = cp[i - 1], p1 = cp[i], p2 = cp[i + 1], p3 = cp[i + 2];

            float t0 = 0f;
            float t1 = t0 + KnotDelta(p0, p1);
            float t2 = t1 + KnotDelta(p1, p2);
            float t3 = t2 + KnotDelta(p2, p3);

            // Degenerate ACTIVE segment (p1≈p2 coincide) → treat it linearly: no curve, just land on the endpoint.
            if ((p2 - p1).sqrMagnitude <= 1e-6f) { fine.Add(p2); continue; }

            int steps = Mathf.Max(2, Mathf.CeilToInt((p2 - p1).magnitude / (SplineSpacing * 0.5f)));
            for (int s = 1; s <= steps; s++)
            {
                float t = Mathf.Lerp(t1, t2, (float)s / steps);
                fine.Add(CatmullRom(p0, p1, p2, p3, t0, t1, t2, t3, t));
            }
        }

        // Resample the fine polyline at a constant SplineSpacing (3D arc length), carrying leftover across segments.
        var sampled = new List<Vector3>();
        sampled.Add(fine[0]);
        float leftover = 0f;
        for (int i = 1; i < fine.Count; i++)
        {
            Vector3 a = fine[i - 1], b = fine[i];
            float d = Vector3.Distance(a, b);
            if (d < 1e-6f) continue;
            float pos = SplineSpacing - leftover;               // distance into THIS segment to the first new sample
            while (pos <= d) { sampled.Add(Vector3.Lerp(a, b, pos / d)); pos += SplineSpacing; }
            leftover = d - (pos - SplineSpacing);               // remainder carried into the next segment
        }

        // Clamp every interior sample on-mesh; force the exact original start/goal as first/last.
        var result = new List<Vector3>(sampled.Count + 2);
        result.Add(start);
        for (int i = 1; i < sampled.Count; i++)
        {
            // Never emit an off-mesh point: use the SNAPPED position when a poly is found, drop the sample otherwise.
            if (!TrySnap(query, filter, sampled[i], out Vector3 onMesh, out _)) continue;
            if ((onMesh - result[^1]).sqrMagnitude > 1e-4f) result.Add(onMesh);   // skip sub-cm duplicates
        }
        // Trim a stubby final leg, then land exactly on the goal.
        if (result.Count > 1 && (result[^1] - goal).sqrMagnitude < (SplineSpacing * 0.5f) * (SplineSpacing * 0.5f))
            result.RemoveAt(result.Count - 1);
        result.Add(goal);

        // No collinear cull here — TryComputeCorridor runs Douglas-Peucker on the finished path (a stronger, shape-aware
        // simplification that subsumes it), so leave the dense samples intact for it to collapse in one place.
        wps.Clear();
        wps.AddRange(result);
        return wps.Count;
    }

    // Centripetal knot delta between two control points = chord^alpha with alpha=0.5 (== sqrt of the chord length),
    // floored at KnotEps so a coincident/duplicated phantom can never make a Barry-Goldman denominator zero.
    private const float KnotEps = 1e-4f;
    private static float KnotDelta(Vector3 a, Vector3 b) => Mathf.Max(Mathf.Sqrt(Vector3.Distance(a, b)), KnotEps);

    // Barry-Goldman recursive evaluation of the Catmull-Rom segment p1→p2 (tangents from p0/p3) at parameter t∈[t1,t2].
    // Every denominator here is a difference of knots, each ≥KnotEps by construction, so no division by zero.
    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
        float t0, float t1, float t2, float t3, float t)
    {
        Vector3 a1 = ((t1 - t) / (t1 - t0)) * p0 + ((t - t0) / (t1 - t0)) * p1;
        Vector3 a2 = ((t2 - t) / (t2 - t1)) * p1 + ((t - t1) / (t2 - t1)) * p2;
        Vector3 a3 = ((t3 - t) / (t3 - t2)) * p2 + ((t - t2) / (t3 - t2)) * p3;
        Vector3 b1 = ((t2 - t) / (t2 - t0)) * a1 + ((t - t0) / (t2 - t0)) * a2;
        Vector3 b2 = ((t3 - t) / (t3 - t1)) * a2 + ((t - t1) / (t3 - t1)) * a3;
        return ((t2 - t) / (t2 - t1)) * b1 + ((t - t1) / (t2 - t1)) * b2;
    }

    // Perpendicular XZ distance from point p to the line a→c.
    private static float PerpDistXZ(Vector3 a, Vector3 c, Vector3 p)
    {
        float acx = c.x - a.x, acz = c.z - a.z;
        float len = Mathf.Sqrt(acx * acx + acz * acz);
        if (len < 1e-4f) { float dx = p.x - a.x, dz = p.z - a.z; return Mathf.Sqrt(dx * dx + dz * dz); }
        float cross = Mathf.Abs((p.x - a.x) * acz - (p.z - a.z) * acx);
        return cross / len;
    }

    // Total horizontal length of a waypoint list (for logging).
    public static float HorizontalLength(List<Vector3> wps)
    {
        float len = 0f;
        for (int i = 1; i < wps.Count; i++)
        {
            float dx = wps[i].x - wps[i - 1].x, dz = wps[i].z - wps[i - 1].z;
            len += Mathf.Sqrt(dx * dx + dz * dz);
        }
        return len;
    }

    // Resolve the navmesh poly nearest a world point (same search extent as the corridor query, and the navigator's own
    // filter — so an already-blocked poly is skipped, forcing each stuck-reroute to blame a genuinely NEW poly). The
    // follower uses it to name the poly to blacklist when it gets stuck, and to fetch the goal poly (never excluded).
    // Returns false with polyRef=0 if no poly is near p.
    public bool TryFindPolyAt(DtNavMeshQuery query, Vector3 p, out long polyRef)
    {
        polyRef = 0;
        var ext = new RcVec3f(ExtentXZ, ExtentY, ExtentXZ);
        var st = query.FindNearestPoly(new RcVec3f(p.x, p.y, p.z), ext, _filter, out long r, out _, out _);
        if (st.Succeeded() && r != 0) { polyRef = r; return true; }
        return false;
    }

    // ── Climb-averse query filter ───────────────────────────────────────────────────────────────────────────────
    // Steers A* corridor selection (FindPath) toward FLATTER routes. The bake fuses climbable steps/rocks (walkableClimb
    // 0.8) INTO the walkable navmesh, so a climb poly is INDISTINGUISHABLE from flat ground by flags — we can't reject
    // it, and MUST NOT (a climb-only route has to stay walkable). Instead we RE-WEIGHT cost: every edge keeps the exact
    // default base cost (segment distance × area-cost map — delegated to DtQueryDefaultFilter), but a corridor that
    // gains height also pays ClimbCostPerMeter per metre of UPWARD gain, so A* prefers a modest detour over a climb.
    // Only upward gain is charged (downhill is free). PassFilter is UNCHANGED (delegated), so this never blocks a poly
    // the default would pass. FindStraightPath (the funnel) ignores cost — fine; we only steer which corridor A* picks.
    //
    // NOTE: DtQueryDefaultFilter.PassFilter/GetCost are sealed (IL virtual+final) → not override-able; so we implement
    // IDtQueryFilter directly and COMPOSE a default filter to inherit its flag test and base cost verbatim.
    private sealed class ClimbAverseFilter : IDtQueryFilter
    {
        private readonly DtQueryDefaultFilter _base = new DtQueryDefaultFilter();

        // Runtime-learned exclusions: polys the follower PROVED it physically can't cross (a spot the navmesh baked as
        // walkable but the game's collision blocks). PassFilter rejects them so A* routes AROUND them instead of the
        // follower stalling there forever. Empty until MarkPolyBlocked adds one; ClearBlocked wipes them per journey.
        private readonly HashSet<long> _blockedPolys = new();
        public void Block(long polyRef) => _blockedPolys.Add(polyRef);
        public void ClearBlocked() => _blockedPolys.Clear();
        public int BlockedCount => _blockedPolys.Count;

        public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly)
            => !_blockedPolys.Contains(refs) && _base.PassFilter(refs, tile, poly);

        public float GetCost(RcVec3f pa, RcVec3f pb,
            long prevRef, DtMeshTile prevTile, DtPoly prevPoly,
            long curRef,  DtMeshTile curTile,  DtPoly curPoly,
            long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
        {
            float baseCost = _base.GetCost(pa, pb, prevRef, prevTile, prevPoly,
                curRef, curTile, curPoly, nextRef, nextTile, nextPoly);
            float climb = pb.Y - pa.Y;      // + when this edge gains height over its pa→pb traversal
            if (climb < 0f) climb = 0f;     // penalise UP only; descending is free
            return baseCost + ClimbCostPerMeter * climb;
        }
    }
}
