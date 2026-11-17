using System;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using UnityEngine;

namespace Stellar.AutoGather;

// PARTIAL-corridor acceptance for TryComputeCorridor.
//
// ⭐ WHY (2026-11-17, Luna Pure Ore, fld001_east): Detour's FindPath returns DT_PARTIAL_RESULT whenever the goal poly is
// not connected to the start — and a gather node's OWN rock collider carves / disconnects the baked mesh around it
// (or it sits on a little ledge just off the walkable strip). The old code refused EVERY partial, so with skip-unreachable
// forced on (2026-09-25) each of ~9 perfectly walkable ore nodes was skipped + blacklisted 15 s without ever walking,
// the status read "No <name> nearby", and the loop looked stopped (log: ~50 partials, polys=1..4, vs ~20 full corridors).
//
// FIX: find the corridor poly whose closest point comes nearest the NODE position (the requested `end`, not the snapped
// endPt — which may sit on the far side of the carved rock), truncate the corridor there, and ACCEPT it when that
// point is within PartialAcceptGapXZ horizontally and PartialAcceptGapY vertically. The follower already hands the LAST
// leg back as a straight-line approach to nodePos with local avoidance, and AutoGather's StuckTooLong / NoProgress
// watchdogs still skip a node that genuinely can't be reached — so a real cliff/island (a big gap) is still refused up
// front and the user's 09-25 "skip truly unreachable nodes" behaviour stands.
//
// Plus ONE cheap retry for an OFF-ISLAND START: when the partial is tiny (≤ TinyPartialPolys — the player stands on a
// small mesh fragment) and the gap is too large, re-snap the start from a point StartNudge m toward the node and plan
// again once. Runs once per target (planning is never per frame), so the extra FindNearestPoly + FindPath are fine.
internal sealed partial class NavMeshNavigator
{
    public const float PartialAcceptGapXZ = 4f;     // max horizontal gap (m) from the partial corridor's reachable end to the node
    public const float PartialAcceptGapY  = 1.5f;   // max vertical gap (m) — beyond this it's a ledge/cliff, not a carved rock
    private const float StartNudge        = 1f;     // off-island start retry: re-snap from this far (m) toward the node
    private const int   TinyPartialPolys  = 2;      // a partial this short means the START sits on a tiny mesh fragment

    // On a partial FindPath result in _polyBuf[0..nPolys): decide accept vs refuse. On accept, nPolys is truncated to the
    // best poly, reachPt is the point to end the funnel at (and startPt may be the re-snapped start), note = diag prefix.
    private bool TryAcceptPartial(DtNavMeshQuery query, IDtQueryFilter filter, RcVec3f ext, Vector3 start, Vector3 goal,
        ref RcVec3f startPt, long endRef, RcVec3f endPt, ref int nPolys, out RcVec3f reachPt, out string note)
    {
        int best = BestPartialEnd(query, nPolys, goal, out reachPt, out float gap, out float dy);
        if (best >= 0 && GapOk(gap, dy))
        {
            nPolys = best + 1;
            note = $"partial accepted gap={gap:F1}m dy={dy:F1} ";
            return true;
        }
        int origPolys = nPolys;

        // Off-island start retry (once). Compare against _polyBuf[0] (= the start poly) BEFORE FindPath overwrites it.
        string retry = "";
        Vector3 toGoal = goal - start; toGoal.y = 0f;
        if (nPolys <= TinyPartialPolys && toGoal.sqrMagnitude > 1e-4f)
        {
            Vector3 n = start + toGoal.normalized * StartNudge;
            var s = query.FindNearestPoly(new RcVec3f(n.x, n.y, n.z), ext, filter, out long r2, out RcVec3f p2, out _);
            if (!s.Succeeded() || r2 == 0 || r2 == _polyBuf[0]) retry = " retry=same-start";
            else
            {
                var fp2 = query.FindPath(r2, endRef, p2, endPt, filter, _polyBuf.AsSpan(), out int n2, _polyBuf.Length);
                if (!fp2.Succeeded() || n2 == 0) retry = $" retry=fail({fp2})";
                else if (!fp2.IsPartial() && _polyBuf[n2 - 1] == endRef)
                {
                    startPt = p2; nPolys = n2; reachPt = endPt;
                    note = "partial: re-snapped start → full path ";
                    return true;
                }
                else
                {
                    int b2 = BestPartialEnd(query, n2, goal, out RcVec3f rp2, out float g2, out float dy2);
                    if (b2 >= 0 && GapOk(g2, dy2))
                    {
                        startPt = p2; nPolys = b2 + 1; reachPt = rp2;
                        note = $"partial accepted (re-snapped start) gap={g2:F1}m dy={dy2:F1} ";
                        return true;
                    }
                    retry = b2 >= 0 ? $" retry gap={g2:F1}m dy={dy2:F1}" : " retry=no-closest";
                }
            }
        }

        note = best >= 0
            ? $"unreachable (partial, polys={origPolys}, gap={gap:F1}m dy={dy:F1}{retry})"
            : $"unreachable (partial, polys={origPolys}, no closest point{retry})";
        return false;
    }

    private static bool GapOk(float gapXZ, float dy) => gapXZ <= PartialAcceptGapXZ && dy <= PartialAcceptGapY;

    // Scan the corridor polys _polyBuf[0..n) for the one whose closest point lies nearest the goal (3D distance, so a
    // poly directly UNDER a cliff-top node doesn't win on XZ alone). Detour's partial end is usually that poly already,
    // but its heuristic measures to the snapped endPt, not the node — scanning is cheap (once per plan) and robust.
    // Returns the index (−1 if no ClosestPointOnPoly succeeded) plus that point and its XZ / |Y| gap to the goal.
    private int BestPartialEnd(DtNavMeshQuery query, int n, Vector3 goal, out RcVec3f bestPt, out float gapXZ, out float dy)
    {
        var g = new RcVec3f(goal.x, goal.y, goal.z);
        int best = -1; float bestD2 = float.MaxValue;
        bestPt = default; gapXZ = float.MaxValue; dy = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (!query.ClosestPointOnPoly(_polyBuf[i], g, out RcVec3f c, out _).Succeeded()) continue;
            float dx = c.X - goal.x, dz = c.Z - goal.z, ddy = c.Y - goal.y;
            float d2 = dx * dx + dz * dz + ddy * ddy;
            if (d2 >= bestD2) continue;
            bestD2 = d2; best = i; bestPt = c;
            gapXZ = Mathf.Sqrt(dx * dx + dz * dz); dy = Mathf.Abs(ddy);
        }
        return best;
    }
}
