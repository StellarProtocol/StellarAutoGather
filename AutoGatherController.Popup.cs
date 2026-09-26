namespace Stellar.AutoGather;

// Auto-dismiss the daily monthly-card / reward popup so a long AFK gathering session isn't interrupted. Direct
// port of StellarAutoFishingPlugin's Plugin.Fishing.cs CheckMonthlyCardInterrupt (proven in-game). Unlike fishing
// (which toggles its automation off), this is an INTERNAL pause that auto-resumes — the feature stays enabled.
// ⚠️ The Lua sentinel global is '_ag_popup' (NOT fishing's '_pf_monthly') so the two plugins don't collide in the
// same client. All Lua pcall-wrapped, main thread.
internal sealed partial class AutoGatherController
{
    // Opt-in (default true), set from the menu. Mirrors fishing's _monthlyCardInterrupt gate.
    public bool DismissMonthlyCard { get; set; } = true;

    private double _popupCheckAt;   // last poll time (TimeNow-throttle, ~0.5s)
    private double _popupResumeAt;   // resume driving/gathering only after this time (lets the UI settle)

    // Called from OnUpdate (before the combat gate). Returns true when a popup was detected + closed → caller
    // returns for this frame. Throttled to ~0.5s; logs only on the detect/close transition (not every poll).
    private bool CheckMonthlyCardPopup()
    {
        double now = _services.Framework.TimeNow;
        if (now - _popupCheckAt < 0.5) return false;
        _popupCheckAt = now;
        if (!_services.Lua.Ready) return false;

        // Store the sentinel as a real Lua boolean (true/nil) so TryReadGlobalBool (type-strict) reads it.
        _services.Lua.DoString("pcall(function() local a=(Z.UIMgr):IsActive('monthly_reward_card_window') local b=(Z.UIMgr):IsActive('com_rewards_window') rawset(_G,'_ag_popup',(a or b) and true or nil) end)");
        if (!(_services.Lua.TryReadGlobalBool("_ag_popup", out var active) && active)) return false;

        _services.Log.Info("[Gather] monthly card / reward window detected — pausing");

        // Pause the loop safely: stop the channel/movement/route and drop back to Seeking.
        AbortActiveChannel("monthly card popup");
        _moveDriver.Stop(this);
        ClearPath();
        _state = GState.Seeking;
        _targetUuid = 0;

        _services.Lua.DoString("pcall(function() if (Z.UIMgr):IsActive('monthly_reward_card_window') then (Z.UIMgr):CloseView('monthly_reward_card_window') end if (Z.UIMgr):IsActive('com_rewards_window') then (Z.UIMgr):CloseView('com_rewards_window') end end)");

        _popupResumeAt = now + 1.5;
        SetStatus(_loc.T("ag.status.popupPaused"), "popup");
        _services.Log.Info("[Gather] popup closed — resuming in 1.5s");
        return true;
    }
}
