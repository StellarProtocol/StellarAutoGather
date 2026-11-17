using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

// The main window, house style (approved spec _scratch\prototypes\autogather-redesign-APPROVED.md, mockup state ①).
// One column, FIXED size in every state — every row is pinned by struts / fixed cells, so nothing grows or shrinks with
// the status, the roster, the mode or the run:
//   ToolHeader   [accent icon] Auto-Gather / ● live status ("Ready · type · N nearby", green "Gathering · type · N left",
//                amber "Stopped · Focus limit reached", red "Stopped · Not enough Focus", muted "Paused · In combat")
//   RESOURCE     5-row click-to-lock list (Plugin.Window.Resource.cs)
//   GATHER MODE  Normal | Focus chips, Focus limit slider, info / meter line (Plugin.Window.Mode.cs)
//   OPTIONS      Auto-close reward popups chip (Plugin.Window.Mode.cs)
//   footer       muted activity line + Start (Primary) / Stop (Neutral)
//
// Performance (Plugin-Performance-Rules.md §7): every Func is a field read or a cached string — strings are re-made only
// when what they show changes (state / count / language), never per refresh. Game reads (roster walk, Focus balance)
// happen only while the window is SHOWN, on the controller's ~1 s throttles.
public sealed partial class Plugin
{
    private const float WindowWidth      = 460f;   // widest locale strings fit without cramming the slider (SDL R18)
    private const float StatusLineHeight = 20f;
    private const float FooterPillLabelW = 72f;   // "Start" / "Stop" in every locale (longest: id "Berhenti")
    private const float FooterPillW      = FooterPillLabelW + 2f * PillPadX;

    private bool Shown => _window is { IsShown: true };

    private HudElement BuildRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(() => Icon(HeaderGlyphPx),() => _loc.T("ag.title"), BuildHeaderStatus()),
        BuildResourcePanel(),
        BuildModePanel(),
        BuildOptionsPanel(),
        BuildFooter(),
    }, Gap: 8f);

    // ── header status ─────────────────────────────────────────────────────────────────────────────────────────────
    private enum HeadKind { PickType, Ready, Gathering, Paused, StoppedLimit, StoppedFocus }

    private HeadKind CurrentHead()
    {
        var c = _autoGatherController;
        if (c.Enabled) return c.Pause != AutoGatherController.PauseKind.None ? HeadKind.Paused : HeadKind.Gathering;
        if (c.LastStop == AutoGatherController.StopReason.FocusLimit) return HeadKind.StoppedLimit;
        if (c.LastStop == AutoGatherController.StopReason.OutOfFocus) return HeadKind.StoppedFocus;
        return c.LockedConfigId == 0 ? HeadKind.PickType : HeadKind.Ready;
    }

    // [dot] [state word, coloured] [" · rest", muted, ellipsized] in one 20 px line. All plain text (no bold), so the
    // colours are Funcs and nothing needs a Conditional swap.
    private HudElement BuildHeaderStatus() => FixedLine(new RowElement(new HudElement[]
    {
        StatusDot(HeadDotColor),
        new SpacerElement(5f, 1f),
        new TextElement(HeadWord, Color: () => HeadWordColor(), NoWrap: true),
        new CellElement(new TextElement(HeadRest, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
    }, Gap: 0.01f), StatusLineHeight);

    private ColorRgba HeadDotColor() => CurrentHead() switch
    {
        HeadKind.Gathering    => SuccessColor(),
        HeadKind.StoppedLimit => CautionColor(),
        HeadKind.StoppedFocus => DangerColor(),
        _                     => MutedColor(),
    };

    private ColorRgba HeadWordColor() => CurrentHead() switch
    {
        HeadKind.Gathering    => Readable(SuccessColor(), Menu.MenuBackground),
        HeadKind.StoppedLimit => Readable(CautionColor(), Menu.MenuBackground),
        HeadKind.StoppedFocus => Readable(ProblemText, Menu.MenuBackground),
        _                     => MutedColor(),
    };

    private string HeadWord() => CurrentHead() switch
    {
        HeadKind.PickType     => _loc.T("ag.status.pickType"),
        HeadKind.Ready        => _loc.T("ag.head.ready"),
        HeadKind.Gathering    => _loc.T("ag.head.gathering"),
        HeadKind.Paused       => _loc.T("ag.head.paused"),
        _                     => _loc.T("ag.head.stopped"),
    };

    // The rest of the line, re-formatted only when its inputs change (kind, locked type, count, pause, language).
    private int     _restKind = -1, _restId, _restCount, _restPause;
    private string? _restLang;
    private string  _restText = "";

    private string HeadRest()
    {
        var c = _autoGatherController;
        var kind = CurrentHead();
        int id = c.LockedConfigId;
        // The roster walk runs only while the window is shown (a hidden Prebuild fill must not scan the world).
        int count = (kind == HeadKind.Ready || kind == HeadKind.Gathering) && Shown ? c.LockedTypeCount : 0;
        int pause = (int)c.Pause;
        string lang = _loc.Language;
        if ((int)kind == _restKind && id == _restId && count == _restCount && pause == _restPause
            && ReferenceEquals(lang, _restLang)) return _restText;
        _restKind = (int)kind; _restId = id; _restCount = count; _restPause = pause; _restLang = lang;
        _restText = kind switch
        {
            HeadKind.Ready        => " · " + _loc.TFormat("ag.head.readyRest", c.LockedName, count),
            HeadKind.Gathering    => " · " + _loc.TFormat("ag.head.runRest", c.LockedName, count),
            HeadKind.Paused       => " · " + PauseText(c.Pause),
            HeadKind.StoppedLimit => " · " + _loc.T("ag.status.focusLimit"),
            HeadKind.StoppedFocus => " · " + _loc.T("ag.status.outOfFocus"),
            _                     => "",
        };
        return _restText;
    }

    private string PauseText(AutoGatherController.PauseKind p) => p switch
    {
        AutoGatherController.PauseKind.Combat => _loc.T("ag.status.inCombat"),
        AutoGatherController.PauseKind.Dead   => _loc.T("ag.status.dead"),
        _                                     => _loc.T("ag.status.popupPaused"),
    };

    // ── footer ────────────────────────────────────────────────────────────────────────────────────────────────────
    // [muted activity line ……] [Start | Stop]. Start = Primary, recessed until a type is locked; Stop = Neutral. The two
    // pills are complete variants in one fixed-width cell, so the swap never moves the activity line.
    private HudElement BuildFooter() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, PillHeight),
        new CellElement(new TextElement(FooterActivity, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
            Weight: 1f),
        new CellElement(new ConditionalElement(() => _autoGatherEnabled,
            NeutralButton(() => _loc.T("ag.stop"), ToggleAutoGather, FooterPillLabelW),
            PrimaryButton(() => _loc.T("ag.start"), ToggleAutoGather, FooterPillLabelW,
                enabled: () => _autoGatherController.LockedConfigId != 0)),
            Width: FooterPillW),
    }, Gap: 8f);

    // The controller's fine-grained activity (already a cached string) while running; empty otherwise.
    private string FooterActivity() => _autoGatherController.Enabled ? _autoGatherController.Status : "";
}
