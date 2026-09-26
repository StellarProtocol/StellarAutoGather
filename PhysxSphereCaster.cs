using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.AutoGather;

// Forward sphere-cast obstacle test, reflected from Panda.ZGame.ZPhysics.CheckCollideSphereCast (returns TRUE when
// BLOCKED). This is a self-contained copy of the exact cast the game's collision uses (BlockLayerMask, the pinned-arg
// pattern, ignoreStep=true / imitateServer=false). Used by NavGatherFollower for local avoidance around small props
// the coarse navmesh stepped over. Resolves lazily; on any failure it degrades to "not blocked" so navigation just
// falls back to straight-line steering.
internal sealed class PhysxSphereCaster
{
    private static readonly object BoxTrue = true, BoxFalse = false;

    private readonly IPluginServices _services;
    private bool _resolved, _ok, _warned;
    private MethodInfo? _miSphere;   // CheckCollideSphereCast(from, radius, to, mask, out hit, ignoreStep, imitateServer)
    private int _blockMask;
    private object? _maskBox, _hit;
    private object[]? _args;

    public PhysxSphereCaster(IPluginServices services) => _services = services;

    public bool Ready => EnsureApi();

    private bool EnsureApi()
    {
        if (_resolved) return _ok;
        _resolved = true;
        try
        {
            var zt = StellarInterop.FindType("Panda.ZGame.ZPhysics");
            if (zt == null) { _services.Log.Info("[GatherNav] avoid: ZPhysics not found — straight-line only"); return false; }
            var piBlock = zt.GetProperty("BlockLayerMask", BindingFlags.Public | BindingFlags.Static);
            foreach (var m in zt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "CheckCollideSphereCast" && m.GetParameters().Length == 7) { _miSphere = m; break; }
            _blockMask = ReadMask(piBlock);
            // out param is the game's ZPhysx.RaycastHit — box a placeholder of THAT element type for the slot.
            var hitT = _miSphere?.GetParameters()[4].ParameterType.GetElementType();
            _hit = MakeHit(hitT);
            _maskBox = _blockMask;
            _args = new object[7];
            _ok = _miSphere != null && _blockMask != 0 && _hit != null;
            _services.Log.Info($"[GatherNav] avoid caster ok={_ok} blockMask={_blockMask} sphere={_miSphere != null}");
            return _ok;
        }
        catch (Exception ex) { _services.Log.Warning($"[GatherNav] avoid caster resolve error: {ex.InnerException?.Message ?? ex.Message}"); return false; }
    }

    // TRUE when a sphere of `radius` swept from→to hits BlockLayerMask geometry.
    public bool Blocked(Vector3 from, float radius, Vector3 to)
    {
        if (!EnsureApi()) return false;
        var a = _args!;
        try
        {
            a[0] = from; a[1] = radius; a[2] = to; a[3] = _maskBox!; a[4] = _hit!; a[5] = BoxTrue; a[6] = BoxFalse;
            return _miSphere!.Invoke(null, a) is bool b && b;
        }
        catch (Exception ex)
        {
            if (!_warned) { _warned = true; _services.Log.Warning($"[GatherNav] avoid cast failed ({ex.InnerException?.Message ?? ex.Message}) — straight-line"); }
            return false;
        }
    }

    private static int ReadMask(PropertyInfo? pi)
    {
        try
        {
            var v = pi?.GetValue(null);
            if (v is int i) return i;
            if (v != null && v.GetType().GetProperty("value")?.GetValue(v) is int lm) return lm;   // LayerMask → .value
        }
        catch { }
        return 0;
    }

    private object? MakeHit(Type? t)
    {
        if (t == null) return null;
        try { return Activator.CreateInstance(t); }
        catch { try { return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t); } catch { return null; } }
    }
}
