using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

// Fast completion via a Harmony postfix on ZInteractionMgr.InteractionUIProgressEnd(long uuid, int cfgId)
// (dump 239228) — fires the INSTANT the gather bar fills (KB §4.5). This is the PRIMARY completion signal:
// it's immediate and inherently tracks the real gather speed (life-skill upgrades gather faster than
// CollectionTable.PickTime — so never assume a fixed duration). The attr-poll (stage 3→0 / collect-counter++)
// stays as the fallback because a fast/upgraded gather can open+close the channel between poll frames.
//
// The method fires once per gather (NOT a hot per-frame tick) → safe per the trampoline-crash KB. Installed on
// Auto-Gather ENABLE, uninstalled on DISABLE/Dispose via its own Harmony host (same lifecycle discipline as the
// move driver). The static postfix is branch-only (an assignment) so it can't throw.
internal sealed partial class AutoGatherController
{
    // Set by the static postfix to the uuid whose progress bar just completed; consumed + cleared in TickGather.
    private static long ProgressEndedUuid;

    private static void ProgressEndPostfix(long __0) => ProgressEndedUuid = __0;

    private HarmonyLib.Harmony? _progressHarmony;
    private bool _progressPatched;

    private void InstallProgressHook()
    {
        if (_progressPatched) return;
        ProgressEndedUuid = 0;
        try
        {
            _progressHarmony ??= _services.Harmony.Create("autogatherprogress");
            var t = StellarInterop.FindType("Panda.ZGame.ZInteractionMgr");
            MethodInfo? mi = null;
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "InteractionUIProgressEnd") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(long) && ps[1].ParameterType == typeof(int)) { mi = m; break; }
                }
            if (mi == null) { _services.Log.Warning("[Gather] InteractionUIProgressEnd not found — completion falls back to attr-poll"); return; }
            _progressHarmony.Patch(mi, postfix: new HarmonyMethod(typeof(AutoGatherController), nameof(ProgressEndPostfix)));
            _progressPatched = true;
            _services.Log.Info("[Gather] progress-end hook installed (primary completion signal)");
        }
        catch (Exception ex) { _services.Log.Warning($"[Gather] progress hook install failed: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    private void UninstallProgressHook()
    {
        ProgressEndedUuid = 0;
        if (!_progressPatched) return;
        try { _progressHarmony?.UnpatchSelf(); } catch { }
        _progressPatched = false;
        _services.Log.Info("[Gather] progress-end hook uninstalled");
    }
}
