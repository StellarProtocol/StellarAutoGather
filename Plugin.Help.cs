using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

// Per-setting help (house help popover; reference StellarPositionPlugin Plugin.Help.cs ← Maestro Plugin.Help*.cs).
// The window framework has no hover tooltip, so each setting row ends in a round "?" that opens ONE shared floating
// popover anchored under it:
//   [32 px accent tile with a white bold "?"] [bold setting name / muted "SECTION · HELP"] … [round ✕]
//   then the help text in one raised tile, 360 px wide.
// No framework title bar (the header carries the title and the close); with ShowTitleBar = false the whole frame is the
// drag handle. Re-clicking the open "?" closes it. Entries hold LOCALIZATION KEYS, resolved at draw time, so a language
// switch re-localizes an open popover.
public sealed partial class Plugin
{
    private readonly Dictionary<string, (string TitleKey, string BodyKey, string SectionKey)> _help = new();
    private readonly Dictionary<string, bool> _helpHover = new();

    private readonly IWindowControl _tipWindow;
    private string _tipKey = "";   // which help entry the popover shows; "" = closed
    private const float TipWidth    = 360f;
    private const float TipIconSize = 32f;
    private const float HelpSize    = 24f;   // the round "?" — exactly 24 × 24 (RoundRadius, Plugin.Style.cs)
    private WindowRect _tipRect;     // where the popover should sit (re-applied for a few frames after opening)
    private int _tipRepositionLeft;

    // Registers help content for `key` and returns its "?". Append it as the LAST child of a row so the "?"s line up in
    // the panel's right-edge column (house rule 17).
    private HudElement HelpDot(string key, string sectionKey, string titleKey, string bodyKey)
    {
        _help[key] = (titleKey, bodyKey, sectionKey);
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                // Height strut = the side; the label box sets the width. NOTHING taller than 24 may sit inside, or the
                // circle turns oval (R4). CenterGlyphs centres the drawn "?" in its box.
                new SpacerElement(0f, HelpSize),
                new TextElement(() => "?", Color: () => NeutralText(), Width: HelpSize, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })                       // real work is in OnClickWithRect (needs the button's rect)
            {
                Bare = true,
                OnHover = over => _helpHover[key] = over,
                OnClickWithRect = r => ToggleTip(key, r),
            })
        {
            FillColorFn = () => Fill(_tipKey == key && _tipWindow.IsShown ? Over(AccentColor() with { A = 0.35f }, NeutralFill())
                              : _helpHover.TryGetValue(key, out var h) && h ? HoverFill(NeutralFill())
                              : NeutralFill()),
            CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private void ToggleTip(string key, WindowRect r)
    {
        if (_tipKey == key && _tipWindow.IsShown) { CloseTip(); return; }
        _tipKey = key;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the popover drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        float k = _services.Framework.ScreenWidth > 0 && _services.Framework.CanvasWidth > 1f
            ? _services.Framework.CanvasWidth / _services.Framework.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = _services.Framework.CanvasHeight > 1f ? _services.Framework.CanvasHeight : _services.Framework.ScreenHeight;
        // Below the "?", right-aligned to it, so it opens to the LEFT of the "?" column instead of covering the setting.
        float x = Math.Max(10f, r.X + r.Width - TipWidth);
        float y = Math.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _tipRect = new WindowRect(x, y, TipWidth, 0f);
        _tipWindow.SetVisible(true);
        _tipWindow.SetRect(_tipRect);
        // Switching between "?"s: the popover is ALREADY visible, so it doesn't remount and keeps its old z position —
        // front it so it stays above the main window (CLAUDE.md Rule 5 exception: re-opening a visible child).
        _tipWindow.BringToFront();
        _tipWindow.MarkDirty();      // re-pull title / body for the new key this frame
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-assert the anchored
        // rect for the next few frames (one-shot Posts, not a standing per-frame subscription).
        _tipRepositionLeft = 4;
        _services.Framework.Post(TipReposition);
    }

    private void TipReposition()
    {
        if (_tipRepositionLeft <= 0) return;
        _tipRepositionLeft--;
        if (_tipWindow.IsShown) _tipWindow.SetRect(_tipRect);
        if (_tipRepositionLeft > 0) _services.Framework.Post(TipReposition);
    }

    private void CloseTip()
    {
        _tipKey = "";
        _tipRepositionLeft = 0;
        try { _tipWindow?.SetVisible(false); } catch { /* never throw from a close path */ }
    }

    private string TipTitle() => _help.TryGetValue(_tipKey, out var e) ? _loc.T(e.TitleKey) : "";
    private string TipBody()  => _help.TryGetValue(_tipKey, out var e) ? _loc.T(e.BodyKey)  : "";

    // "GATHER MODE · HELP" — formatted + upper-cased only when the entry or the language changes.
    private string? _tipSubKey, _tipSubLang;
    private string  _tipSubText = "";
    private string TipSubtitle()
    {
        string lang = _loc.Language;
        if (!ReferenceEquals(_tipKey, _tipSubKey) || !ReferenceEquals(lang, _tipSubLang))
        {
            _tipSubKey = _tipKey; _tipSubLang = lang;
            _tipSubText = _help.TryGetValue(_tipKey, out var e)
                ? _loc.TFormat("ag.help.subtitle", _loc.T(e.SectionKey)).ToUpper(System.Globalization.CultureInfo.InvariantCulture)
                : "";
        }
        return _tipSubText;
    }

    private IWindowControl RegisterTipWindow()
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "autogather.tip",
                Title:       "Help",   // fallback only (no title bar is drawn); localized below
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - TipWidth - 20f, 20f, TipWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false, ShowTitleBar = false,
              TitleProvider = () => _loc.T("ag.help.label"),
              ShouldRender = InWorld },
            Root: BuildTipRoot(),
            OnClose: () => { _tipKey = ""; w!.SetVisible(false); }));
        return w;
    }

    private readonly HoverFlag _tipCloseHover = new();

    private HudElement BuildTipRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            // 32 px accent tile with a white bold "?" (the IconBox look, drawn with text instead of a PNG).
            new CellElement(new BubbleElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, TipIconSize),
                    new TextElement(() => "?", Color: () => SolidLabel(AccentColor()), Width: TipIconSize,
                        Align: TextAlign.Center, NoWrap: true, FontSize: 18) { Bold = true, CenterGlyphs = true },
                }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = () => Fill(AccentColor()), CornerRadius = MathF.Round(TipIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
            }, Width: TipIconSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(TipTitle, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(TipSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            // Icon-only ✕ (plain text — bold ✕ can draw blank): a round 24 px hover wash around a Bare selectable.
            new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, 24f),
                    new TextElement(() => "✕", Color: () => _tipCloseHover.On ? TextColor() : MutedColor(), Width: 24f,
                        Align: TextAlign.Center, NoWrap: true),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                OnClick: CloseTip) { Bare = true, OnHover = over => _tipCloseHover.On = over })
            {
                FillColorFn = () => _tipCloseHover.On ? Fill(HoverFill(Menu.MenuBackground)) : Transparent,
                CornerRadius = RoundRadius(24f), PaddingX = 0f, PaddingY = 0f,
            },
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(TipBody, Color: () => TextColor()))
        {
            FillColorFn = () => Fill(TileColor()), CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);
}
