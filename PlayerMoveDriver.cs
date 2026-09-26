using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.AutoGather;

/// <summary>
/// Shared local-player locomotion driver — the one place that walks the LOCAL player toward a world
/// position by injecting a camera-relative move stick.
///
/// <b>Mechanism (from FollowController.Move.cs, extracted so Follow + Auto-Gather share ONE Harmony patch and
/// ONE set of inject statics instead of two competing patches on the same hot method):</b> the move axes are
/// NOT read via <c>GetAxis(int)</c> in S3 — movement is composed in <c>PlayerInputController.getPrimitiveInputDir()</c>
/// (private, returns Vector2), which <c>Update()</c> calls each frame to set <c>inputDir_</c>. We Harmony-postfix
/// that method and override its result with our camera-relative stick while a consumer is driving. Fallback chain
/// if that method can't be patched: <c>InputDir</c> getter → <c>CharCtrlCompBase.GetInputDir</c> (world Vector3) +
/// <c>PlayerInputController.Move</c>. See Player-Locomotion-and-Follow.md §SOLVED + Harmony-Rewired-Input-Patching.md.
///
/// <b>Single-owner arbitration:</b> only one consumer may drive at a time (a <c>TryDrive(owner,…)</c> guard) so
/// Follow and Auto-Gather can't fight over the stick if both are enabled. The Harmony patch install is
/// reference-counted over the set of consumers that called <c>Install</c>.
/// </summary>
internal sealed class PlayerMoveDriver
{
    // Read by the static postfixes. Camera-relative stick (Vector2) for the primary/InputDir paths; world dir
    // (Vector3) for the GetInputDir fallback. Active only while a consumer is actively driving (real input passes
    // through otherwise — the postfix leaves __result untouched when inactive).
    private static Vector2 InjectStick;
    private static Vector3 InjectWorldDir;
    private static bool    InjectActive;

    private static void PostfixPrimitive(ref Vector2 __result)   { if (InjectActive) __result = InjectStick; }
    private static void PostfixInputDir(ref Vector2 __result)    { if (InjectActive) __result = InjectStick; }
    private static void PostfixGetInputDir(ref Vector3 __result) { if (InjectActive) __result = InjectWorldDir; }

    private readonly IPluginServices _services;
    public PlayerMoveDriver(IPluginServices services) => _services = services;

    // ── Ownership + install refcount ─────────────────────────────────────────
    private object?                 _owner;         // the consumer currently allowed to drive (null = free)
    private readonly HashSet<object> _installers = new();
    private bool _camWarned;

    /// <summary>Reference-counted install of the inject patch. Idempotent per owner.</summary>
    public void Install(object owner)
    {
        _installers.Add(owner);
        InstallInject();
    }

    /// <summary>Release this owner: stop its drive (if it holds the stick) and uninstall the patch when the last
    /// consumer leaves.</summary>
    public void Uninstall(object owner)
    {
        Stop(owner);
        _installers.Remove(owner);
        if (_installers.Count == 0) UninstallInject();
    }

    /// <summary>Whether <paramref name="owner"/> currently holds (or can acquire) the stick.</summary>
    public bool CanDrive(object owner) => _owner == null || _owner == owner;

    /// <summary>
    /// Drive the local player from <paramref name="localPos"/> toward <paramref name="targetPos"/> this frame.
    /// <paramref name="scale"/> scales the stick magnitude (1 = full run, 0.5 = walk). Returns false — without
    /// touching the inject — when another owner is currently driving, or when already at the target.
    /// </summary>
    public bool TryDrive(object owner, Vector3 localPos, Vector3 targetPos, float scale)
    {
        if (_owner != null && _owner != owner) return false;   // someone else holds the stick

        Vector3 wd = targetPos - localPos; wd.y = 0f;
        if (wd.sqrMagnitude < 1e-6f) { Stop(owner); return false; }
        wd.Normalize();

        // Camera-relative stick (orthonormal horizontal basis → (sx,sy) is unit = full run).
        float sx, sy;
        var cam = GetHudCamera();
        if (cam != null && TryCamAxes(cam, out var fwd, out var right))
        { sx = Vector3.Dot(wd, right); sy = Vector3.Dot(wd, fwd); }
        else
        {
            if (!_camWarned) { _camWarned = true; _services.Log.Info("[Move] no HUD camera — using world axes for the move stick"); }
            sx = wd.x; sy = wd.z;
        }
        sx *= scale; sy *= scale;

        _owner         = owner;
        InjectStick    = new Vector2(sx, sy);   // camera-relative — getPrimitiveInputDir/InputDir return this form
        InjectWorldDir = wd;                     // world dir — for the CharCtrlCompBase.GetInputDir(Vector3) fallback
        InjectActive   = true;
        if (_injectPath == "GetInputDir") TryMove(new Vector2(sx, sy));   // engage isInputMoving() for fallback b
        return true;
    }

    /// <summary>Stop driving for this owner (releases the stick). No-op if another owner holds it.</summary>
    public void Stop(object owner)
    {
        if (_owner != owner) return;
        _owner = null;
        if (!InjectActive) return;
        InjectActive = false;
        if (_injectPath == "GetInputDir") TryMove(Vector2.zero);
    }

    /// <summary>The inject path actually patched ("getPrimitiveInputDir"/"InputDir"/"GetInputDir"/"none").</summary>
    public string InjectPath => _injectPath;

    // ── Camera resolve (HudMgr.GetHudMainCamera) ─────────────────────────────
    private static bool TryCamAxes(Camera cam, out Vector3 fwd, out Vector3 right)
    {
        fwd = Vector3.forward; right = Vector3.right;
        try
        {
            var t = cam.transform;   // Il2CppInterop fake-null / destroyed-camera guard
            var f = t.forward; f.y = 0f;
            var r = t.right;   r.y = 0f;
            if (f.sqrMagnitude < 1e-6f || r.sqrMagnitude < 1e-6f) return false;
            fwd = f.normalized; right = r.normalized;
            return true;
        }
        catch { return false; }
    }

    private bool          _hudResolved;
    private PropertyInfo? _piHudMgrInstance;
    private MethodInfo?   _miGetHudMainCamera;
    private Camera? GetHudCamera()
    {
        try
        {
            if (!_hudResolved)
            {
                _hudResolved = true;
                var t = StellarInterop.FindType("Panda.Hud.HudMgr");
                _piHudMgrInstance   = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _miGetHudMainCamera = t?.GetMethod("GetHudMainCamera", BindingFlags.Public | BindingFlags.Instance);
            }
            var inst = _piHudMgrInstance?.GetValue(null);
            if (inst == null) return null;
            return _miGetHudMainCamera?.Invoke(inst, null) as Camera;
        }
        catch { return null; }
    }

    // ── Harmony inject install (primary → InputDir getter → GetInputDir) ──────
    private HarmonyLib.Harmony? _harmony;
    private bool   _injectPatched;
    private string _injectPath = "none";

    private void InstallInject()
    {
        if (_injectPatched) return;
        try
        {
            _harmony ??= _services.Harmony.Create("playermovedriver");
            var picType = StellarInterop.FindType("Panda.ZInput.PlayerInputController");
            if (picType != null)
            {
                var prim = FindNoArg(picType, "getPrimitiveInputDir", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (prim != null && TryPatch(prim, nameof(PostfixPrimitive))) { _injectPath = "getPrimitiveInputDir"; _injectPatched = true; goto done; }

                var getInputDir = picType.GetMethod("get_InputDir", BindingFlags.Public | BindingFlags.Instance);
                if (getInputDir != null && TryPatch(getInputDir, nameof(PostfixInputDir))) { _injectPath = "InputDir"; _injectPatched = true; goto done; }
            }

            var ctrlType = StellarInterop.FindType("Panda.ZGame.CharCtrlCompBase");
            var gid = ctrlType == null ? null : FindNoArg(ctrlType, "GetInputDir", BindingFlags.Public | BindingFlags.Instance);
            if (gid != null && TryPatch(gid, nameof(PostfixGetInputDir)))
            {
                _injectPath = "GetInputDir"; _injectPatched = true;
                ResolveMoveFacade();   // fallback b also calls Move(stick) each frame
                goto done;
            }

            _injectPath = "none";
            _services.Log.Warning("[Move] no inject point could be patched");
            return;

        done:
            _services.Log.Info($"[Move] move inject installed via {_injectPath}");
        }
        catch (Exception ex) { _services.Log.Warning($"[Move] InstallInject error: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    private void UninstallInject()
    {
        InjectActive = false;
        _owner       = null;
        if (_injectPath == "GetInputDir") TryMove(Vector2.zero);
        if (_injectPatched)
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _injectPatched = false;
            _services.Log.Info("[Move] move inject uninstalled");
        }
    }

    private bool TryPatch(MethodInfo mi, string postfixName)
    {
        try { _harmony!.Patch(mi, postfix: new HarmonyMethod(typeof(PlayerMoveDriver), postfixName)); return true; }
        catch (Exception ex) { _services.Log.Warning($"[Move] patch {mi.Name} failed: {ex.Message}"); return false; }
    }

    private static MethodInfo? FindNoArg(Type t, string name, BindingFlags flags)
    {
        foreach (var m in t.GetMethods(flags))
            if (m.Name == name && m.GetParameters().Length == 0) return m;
        return null;
    }

    // ── Move facade (PlayerInputController.Move — GetInputDir fallback only) ──
    private PropertyInfo? _piPicInstance;   // PlayerInputController.Instance (singleton)
    private MethodInfo?   _miMove;          // PlayerInputController.Move(Vector2)

    private void ResolveMoveFacade()
    {
        try
        {
            var picType = StellarInterop.FindType("Panda.ZInput.PlayerInputController");
            _piPicInstance = picType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _miMove        = picType?.GetMethod("Move", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Vector2) }, null);
        }
        catch { }
    }

    private bool _loggedMoveErr;
    private void TryMove(Vector2 stick)
    {
        if (_miMove == null || _piPicInstance == null) return;
        try { var pic = _piPicInstance.GetValue(null); if (pic != null) _miMove.Invoke(pic, new object[] { stick }); }
        catch (Exception ex)
        {
            if (_loggedMoveErr) return;
            _loggedMoveErr = true;
            _services.Log.Warning("[Move] Move fallback: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }
}
