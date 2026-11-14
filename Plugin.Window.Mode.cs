using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

// GATHER MODE + OPTIONS panels (approved spec items 3–4):
//   [Normal | Focus] ……………………………… [?]      chips recessed (locked) while running
//   Focus limit [──●────────] 200 [?]          recessed in Normal mode; editable while running
//   info line / amber meter + "Spent 60 / 200 · 7 gathered · You have 1,180"
//   Auto-close reward popups …… [On] [?]
// The info area reserves the meter + line height in EVERY state (Row strut), so switching mode / starting a run never
// changes the window height; the shorter one-line variants sit vertically centred in the same slot.
public sealed partial class Plugin
{
    private const float ModeChipW     = 100f;   // "Normal" / "Focus" in every locale
    private const float LimitLabelW   = 104f;   // "Focus limit" (longest: fil "Limit ng Focus", th)
    private const float LimitValueW   = 104f;   // bold 13 value; fits "No limit" in every locale (longest: fil "Walang limit" ≈ 97 px)
    private const float MeterHeight   = 6f;
    private const float InfoLineH     = 20f;
    private const float InfoGap       = 4f;
    private const float InfoSlotH     = MeterHeight + InfoGap + InfoLineH;

    private bool Running => _autoGatherEnabled;

    private HudElement BuildModePanel() => SectionPanel(() => _loc.T("ag.section.mode"),
        () => Running ? _loc.T("ag.hint.modeLocked") : _loc.T("ag.hint.mode"),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ChipHeight),
            SegmentChip(() => _loc.T("ag.mode.normal"), () => !_autoGatherFocus, () => !Running,
                () => SetAutoGatherFocus(false), ModeChipW),
            SegmentChip(() => _loc.T("ag.mode.focus"), () => _autoGatherFocus, () => !Running,
                () => SetAutoGatherFocus(true), ModeChipW),
            new SpacerElement(0f),   // flexible: pushes the "?" to the right-edge column
            HelpDot("mode", "ag.section.mode", "ag.section.mode", "ag.help.mode"),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ChipHeight),
            new CellElement(new TextElement(() => _loc.T("ag.limit.label"),
                Color: () => _autoGatherFocus ? MutedColor() : DisabledLabel(PanelColor()), NoWrap: true) { Ellipsize = true },
                Width: LimitLabelW),
            // Round knob (SquareHandle + 14) per the kit. Disabled in Normal mode: fw ≥ 2.31 paints the recessed slider
            // itself (#160); on 2.30 only the knob dims, the label and value still fade. Get is a plain field read
            // (Prebuild build-time callback).
            new CellElement(new SliderElement(FocusLimitSliderGet, FocusLimitSliderSet, 0f, NoLimitIndex, () => _autoGatherFocus)
                { SquareHandle = true, HandleSize = 14f }, Weight: 1f),
            new CellElement(DropBold(new TextElement(FocusLimitValueText,
                Color: () => _autoGatherFocus ? TextColor() : DisabledLabel(PanelColor()),
                Width: LimitValueW, Align: TextAlign.Right, NoWrap: true, FontSize: 13) { Bold = true }), Width: LimitValueW),
            HelpDot("limit", "ag.section.mode", "ag.limit.label", "ag.help.limit"),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, InfoSlotH),
            new CellElement(new ConditionalElement(ShowFocusMeter,
                new ColumnElement(new HudElement[]
                {
                    new BarElement(FocusMeterFraction, CautionColor())
                        { Height = MeterHeight, FillWidth = true, Style = BarStyle.Modern, FillColorFn = () => Fill(CautionColor()) },
                    FocusInfoLine(),
                }, Gap: InfoGap),
                FocusInfoLine()), Weight: 1f),
        }, Gap: 0.01f));

    private HudElement FocusInfoLine() => FixedLine(
        new TextElement(FocusInfoText, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }, InfoLineH);

    private HudElement BuildOptionsPanel() => SectionPanel(() => _loc.T("ag.section.options"), () => "",
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ChipHeight),
            new CellElement(new TextElement(() => _loc.T("ag.autoClosePopups"), Color: () => TextColor(), NoWrap: true)
                { Ellipsize = true }, Weight: 1f),
            ToggleChip(() => _autoGatherDismissCard, ToggleDismissCard),
            HelpDot("popups", "ag.section.options", "ag.autoClosePopups", "ag.help.popups"),
        }, Gap: 6f));

    // ── Focus limit value ("200" / "No limit") ────────────────────────────────────────────────────────────────────
    // Numbers are made once per value and reused. Also drives the debounced config save (this Func runs on every window
    // refresh while open — the only place a drag can happen).
    private static readonly Dictionary<int, string> _numberText = new();
    private static string NumberText(int n)
    {
        if (!_numberText.TryGetValue(n, out var s)) _numberText[n] = s = n.ToString("N0", CultureInfo.InvariantCulture);
        return s;
    }

    private string FocusLimitValueText()
    {
        TickFocusLimitSave();
        int limit = _autoGatherController.FocusLimit;
        return limit < 0 ? _loc.T("ag.limit.none") : NumberText(limit);
    }

    // ── info / meter line ─────────────────────────────────────────────────────────────────────────────────────────
    // A run has happened (running, or stopped after one) → counters; else the idle explanation. The meter shows only
    // with a limit set, in Focus mode, once there is a run to measure.
    private bool RunShown => Running || _autoGatherController.HasRun;

    private bool ShowFocusMeter() => _autoGatherFocus && _autoGatherController.FocusLimit >= 0 && RunShown;

    private float FocusMeterFraction()
    {
        int limit = _autoGatherController.FocusLimit;
        if (limit < 0) return 0f;
        if (limit == 0) return 1f;
        return Math.Clamp(_autoGatherController.FocusSpent / (float)limit, 0f, 1f);
    }

    // Re-formatted only when one of its inputs changes. The balance is read (≤1 Hz, Lua) only in Focus mode while the
    // window is shown — Normal mode doesn't mention it.
    private int     _infoMode = -1, _infoLimit, _infoSpent, _infoGathered, _infoBal;
    private string? _infoLang;
    private string  _infoText = "";

    private string FocusInfoText()
    {
        var c = _autoGatherController;
        int mode  = !_autoGatherFocus ? 0 : RunShown ? 2 : 1;
        int limit = c.FocusLimit, spent = c.FocusSpent, gathered = c.GatheredCount;
        int bal   = mode != 0 && Shown ? c.FocusBalance : -1;
        string lang = _loc.Language;
        if (mode == _infoMode && limit == _infoLimit && spent == _infoSpent && gathered == _infoGathered && bal == _infoBal
            && ReferenceEquals(lang, _infoLang)) return _infoText;
        _infoMode = mode; _infoLimit = limit; _infoSpent = spent; _infoGathered = gathered; _infoBal = bal; _infoLang = lang;

        string main = mode switch
        {
            0 => _loc.T("ag.focus.normal"),
            1 => limit < 0 ? _loc.T("ag.focus.noLimit") : _loc.TFormat("ag.focus.idle", NumberText(limit)),
            _ => limit < 0 ? _loc.TFormat("ag.focus.spentNoLimit", NumberText(spent), NumberText(gathered))
                           : _loc.TFormat("ag.focus.spent", NumberText(spent), NumberText(limit), NumberText(gathered)),
        };
        _infoText = mode != 0 && bal >= 0 ? main + " · " + _loc.TFormat("ag.focus.have", NumberText(bal)) : main;
        return _infoText;
    }
}
