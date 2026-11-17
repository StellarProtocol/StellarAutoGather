using System;

namespace Stellar.AutoGather;

// Focus limit setting (approved spec option A: a slider). 0..2000 Focus in steps of 20 (one Focus gather costs ~20), and
// one extra stop at the far right = "No limit" (stored as -1). Default 200. A manual setting (Rule 12) — never learned.
// The controller re-reads it on every seek, so a change while running applies at once.
//
// The slider works in STEP INDICES (0..FocusLimitSteps + 1) so it always lands on a whole step; the config value is the
// Focus amount. A drag fires Set many times a second, so the config FILE write is debounced (saved once the value has
// rested for FocusLimitSaveDelay; flushed on close / logout / dispose).
// Raising the max (1000 → 2000) needs no config migration: the value is stored as Focus, not as an index, so an old
// saved 0..1000 / -1 still normalizes to the same limit and lands on the right (rescaled) slider position.
public sealed partial class Plugin
{
    private const int    DefaultFocusLimit   = 200;
    private const int    FocusLimitStep      = 20;
    private const int    FocusLimitMax       = 2000;
    private const int    FocusLimitSteps     = FocusLimitMax / FocusLimitStep;   // 100 → index 101 = No limit
    private const int    NoLimitIndex        = FocusLimitSteps + 1;
    private const double FocusLimitSaveDelay = 0.5;

    private double _focusLimitSaveAt = -1;   // >= 0 = a config save is pending (debounced slider drag)

    // Any stored value → a valid limit: negative = no limit, else clamped to 0..2000 and snapped to the 20 step.
    private static int NormalizeFocusLimit(int v)
    {
        if (v < 0) return AutoGatherController.NoFocusLimit;
        int steps = (int)MathF.Round(Math.Min(v, FocusLimitMax) / (float)FocusLimitStep);
        return steps * FocusLimitStep;
    }

    private static float FocusLimitToIndex(int limit) => limit < 0 ? NoLimitIndex : limit / FocusLimitStep;

    private static int IndexToFocusLimit(float index)
    {
        int i = (int)MathF.Round(Math.Clamp(index, 0f, NoLimitIndex));
        return i >= NoLimitIndex ? AutoGatherController.NoFocusLimit : i * FocusLimitStep;
    }

    // Slider Get: a plain field read (also a Prebuild build-time callback — must not touch game state).
    private float FocusLimitSliderGet() => FocusLimitToIndex(_autoGatherController.FocusLimit);

    private void FocusLimitSliderSet(float index)
    {
        int limit = IndexToFocusLimit(index);
        if (limit == _autoGatherController.FocusLimit) return;
        _autoGatherController.FocusLimit = limit;   // live: the next seek uses it
        _cfg.Set<int>("autogather_focus_limit", limit);
        _focusLimitSaveAt = _services.Framework.TimeNow + FocusLimitSaveDelay;
    }

    // Polled from the slider's value Func (window refresh, ≤10 Hz while shown): writes the file once the drag rests.
    private void TickFocusLimitSave()
    {
        if (_focusLimitSaveAt >= 0 && _services.Framework.TimeNow >= _focusLimitSaveAt) FlushFocusLimitSave();
    }

    private void FlushFocusLimitSave()
    {
        if (_focusLimitSaveAt < 0) return;
        _focusLimitSaveAt = -1;
        _cfg.Save();
    }
}
