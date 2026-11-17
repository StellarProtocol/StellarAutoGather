using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

// House style (Stellar Design Language, Knowledge Base\Stellar-Design-Language.md) — the palette, colour maths and pill
// / chip / panel shells the Auto-Gather window builds from. Ported from StellarPositionPlugin Plugin.Style.cs (itself
// Maestro + MinimalNameplate + the fw 2.30.0 kit rules: the SOLID-fill label rule and the RECESSED disabled look).
// Status colours come from the theme (IThemeStatusColors, SDK 2.30.0) instead of hard-coded #5fd38d / #ffcc73 / #ff4d4f.
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) honours it.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • Bold can't be toggled live → a bold/regular variant is two complete elements swapped by a ConditionalElement
//     wrapping the WHOLE control, in the same fixed-width cell, so the swap never moves its neighbours.
//
// Performance (Plugin-Performance-Rules.md §7): every colour here is pure struct maths on the live theme — no
// allocation inside the per-refresh Funcs.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    private static readonly ColorRgba PanelShade = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private const float TileTextMix      = 0.08f;   // raised tile = panel + 8% MenuText …
    private const float TileAccentMix    = 0.06f;   // … + a 6% accent tint (teal on teal, pink on pink)
    private const float NeutralMix       = 0.22f;   // neutral pill = panel + 22% MenuMuted …
    private const float NeutralAccentMix = 0.08f;   // … + an 8% accent tint, like the tiles
    private const float TintedMinContrast = 4.5f;   // labels on TINTED fills (Neutral, chips)
    private const float SolidMinContrast  = 3.0f;   // white labels on SOLID fills (Primary) — R7
    private const float ChipWashAlpha    = 0.22f;   // "on" chip = colour @22% over the panel
    private const float ChipOffAlpha     = 0.15f;   // "off" chip = MenuMuted @15%
    private const float ChipHoverAlpha   = 0.12f;   // hover adds this to a chip's wash (keeps the chip's shape)
    private const float HoverMix         = 0.10f;   // hover = MenuText@0.10 over the fill
    private const float SelectedWashMix  = 0.35f;   // locked resource row / active mode chip = accent @35% (approved spec)

    private static readonly ColorRgba ProblemText = Rgb(0xff8a8b);   // house error red for TEXT (readable on dark)
    private static readonly ColorRgba White       = new(1f, 1f, 1f);
    private static readonly ColorRgba DarkLabel   = new(0.12f, 0.12f, 0.12f, 1f);   // #1F1F1F — solid-fill fallback label
    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);

    private IThemeMenuColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()   => Menu.MenuText;
    private ColorRgba MutedColor()  => Menu.MenuMuted;
    private ColorRgba AccentColor() => Menu.MenuAccent;
    private ColorRgba PanelColor()  => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TileColor()   => Over(Menu.MenuAccent with { A = TileAccentMix },
                                            Over(Menu.MenuText with { A = TileTextMix }, PanelColor()));
    private ColorRgba NeutralFill() => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                            Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor()));
    private ColorRgba NeutralText() => EnsureContrast(TextColor(), NeutralFill(), TintedMinContrast);
    private ColorRgba PrimaryFill() => Menu.MenuAccent;
    private ColorRgba PrimaryText() => SolidLabel(PrimaryFill());
    // Selected / active wash: accent over the panel (locked resource row, active Normal|Focus chip).
    private ColorRgba SelectedFill() => Over(Menu.MenuAccent with { A = SelectedWashMix }, PanelColor());

    // Theme status colours (fw 2.30.0; dark presets = the old #5FD38D / #FF4D4F / #FFCC73, Light darker).
    private ColorRgba SuccessColor() => _services.Theme.Colors.Success;
    private ColorRgba CautionColor() => _services.Theme.Colors.Caution;
    private ColorRgba DangerColor()  => _services.Theme.Colors.Danger;
    // Status text kept readable on the surface it sits on (darkens on the Light theme).
    private ColorRgba Readable(ColorRgba c, ColorRgba surface) => EnsureContrast(c, surface, TintedMinContrast);

    // ── window opacity: every self-drawn fill fades with the frame (SDL §5) ───────────────────────────────────────
    // Themes → Window Opacity fades ONLY the GlassMenu frame Image; our fills are opaque pre-blended colours, so every
    // FillColorFn goes through Fill (compose OPAQUE first, then scale the alpha). Text and pictures stay solid.
    private float WindowOpacity()
    {
        try { return Math.Clamp((_services.NamedTheme as IChromeStyle)?.WindowOpacity ?? 1f, 0.3f, 1f); }
        catch { return 1f; }
    }

    private ColorRgba Fill(ColorRgba c) => c with { A = c.A * WindowOpacity() };

    // ── rule 22: pressable ≠ disabled ─────────────────────────────────────────────────────────────────────────────
    // Disabled = RECESSED: black @28 % over the surface it sits on, same size and radius, faint label (Text @32 %,
    // raised until 2:1). No outline ring (user rejected it, 2026-11-09).
    private static readonly ColorRgba DisabledShade = new(0f, 0f, 0f, 0.28f);

    private static ColorRgba DisabledFill(ColorRgba surface) => Over(DisabledShade, surface);

    private ColorRgba DisabledLabel(ColorRgba surface)
    {
        var bg = DisabledFill(surface);
        float lb = Luminance(bg), a = 0.32f;
        while (a < 1f && ContrastRatio(Luminance(Over(TextColor() with { A = a }, bg)), lb) < 2f) a = MathF.Min(1f, a + 0.04f);
        return TextColor() with { A = a };
    }

    // The label on a SOLID fill (R7, fw 8df7f29): white while white reaches 3:1 against the fill AS DRAWN (window opacity
    // included), else near-black at 4.5:1 — never a mid grey that reads as disabled.
    private ColorRgba SolidLabel(ColorRgba fill)
    {
        var seen = Over(fill with { A = WindowOpacity() }, PanelColor());
        if (ContrastRatio(1f, Luminance(seen)) >= SolidMinContrast) return White;
        return EnsureContrast(DarkLabel, seen, TintedMinContrast);
    }

    // ── colour maths (ChatTools Plugin.Tabs.cs → MultiTool / Maestro / Position Plugin.Style.cs; pure struct math) ──
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        var target = lb < 0.179f ? new ColorRgba(1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb) => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    // a → b by t (0 = a, 1 = b), opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);
    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);
    private static ColorRgba Rgb(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, a);

    // ── hover that follows the shape ──────────────────────────────────────────────────────────────────────────────
    // A normal SelectableElement paints a SQUARE hover wash. Every clickable here is a Bare selectable whose OnHover
    // flips a flag, and the surrounding Bubble's FillColorFn shows the hover — so it keeps the round shape (R2).
    private sealed class HoverFlag { public bool On; }

    private ColorRgba HoverFill(ColorRgba fill) => Over(Menu.MenuText with { A = HoverMix }, fill);

    // The corner radius for a ROUND bubble of height `side`: side / 2 − 1, NOT side / 2 (R1) — the 9-slice background's
    // min size is 2r+2, so side / 2 made every round control 2 px taller than its content.
    private static float RoundRadius(float side) => side / 2f - 1f;

    // Bold (TMP) labels sit ~1.5–2 px HIGH when centred; a 2 px spacer above moves the text down 1 px (Maestro measured).
    private static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f),
        label,
    }, Gap: 0.01f);

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE build for every labelled button: Bubble (fill + hover) → Bare Selectable → Row[strut, bold-13 label, strut].
    // 28 px tall = the struts; the label has a FIXED width (`labelW`, the longest label in any locale) so a label change
    // never resizes it (R10). `enabled` (polled): disabled = recessed fill + faint label, same geometry, click swallowed.
    private const float PillHeight = 28f;
    private const float PillPadX   = 12f;

    private HudElement PillButton(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        float labelW, Func<bool>? enabled = null)
    {
        var hover = new HoverFlag();
        bool En() => enabled == null || enabled();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(PillPadX, PillHeight),
                DropBold(new TextElement(label, Color: () => En() ? text() : DisabledLabel(PanelColor()),
                    Width: labelW, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true }),
                new SpacerElement(PillPadX, PillHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fill(!En() ? DisabledFill(PanelColor()) : hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(PillHeight), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement PrimaryButton(Func<string> label, Action onClick, float labelW, Func<bool>? enabled = null) =>
        PillButton(label, PrimaryFill, PrimaryText, onClick, labelW, enabled);
    private HudElement NeutralButton(Func<string> label, Action onClick, float labelW, Func<bool>? enabled = null) =>
        PillButton(label, NeutralFill, NeutralText, onClick, labelW, enabled);

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): bold 12 UPPERCASE caption + right-aligned muted hint, then the
    // children. Directly in a Column it FILLS the width (a Bubble only hugs inside a Row). The caption is a Func so it
    // re-localizes live; ToUpper runs only when the language changes (cached), never per refresh. ⚠️ A trailing-control
    // variant must get its OWN NAME — a second params overload silently swallows the first child (KB SDL §4).
    private const float CaptionHeight = 20f;   // pins the caption row: bold 12 caption vs plain 14 hint differ in height

    private HudElement SectionPanel(Func<string> caption, Func<string> hint, params HudElement[] children)
    {
        string? capSrc = null, capUpper = "";
        string Cap()
        {
            string s = caption();
            if (!ReferenceEquals(s, capSrc)) { capSrc = s; capUpper = s.ToUpperInvariant(); }
            return capUpper;
        }
        var items = new List<HudElement>(children.Length + 1)
        {
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, CaptionHeight),
                new TextElement(Cap, Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true },
                new SpacerElement(0f),   // flexible: pushes the hint to the right edge
                new TextElement(hint, Color: () => MutedColor(), NoWrap: true),
            }, Gap: 6f),
        };
        items.AddRange(children);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => Fill(PanelColor()), CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // ── clickable on/off chip (MinimalNameplate 2.3.0, measured 64 × 24 in both states) ───────────────────────────
    // on = bold Success on a 22% wash, off = regular MenuMuted on a muted wash. Two complete chips swapped by a
    // Conditional wrapping the WHOLE chip, inside one fixed-width cell. MaxWidth pins the width: a MaxWidth-0 bubble
    // under a Conditional caps itself at the 100 px default (KB WindowBuilder-Patterns). Both states share one HoverFlag.
    // Labels are localized ("ag.chip.on"/"ag.chip.off"); 64 px fits the longest in every shipped locale.
    private const float ChipWidth  = 64f;
    private const float ChipHeight = 24f;

    private HudElement ToggleChip(Func<bool> on, Action toggle)
    {
        var hover = new HoverFlag();
        float Wash(float a) => a + (hover.On ? ChipHoverAlpha : 0f);
        return new CellElement(new ConditionalElement(on,
            ChipShell(DropBold(new TextElement(() => _loc.T("ag.chip.on"), Color: () => Readable(SuccessColor(), PanelColor()),
                    Width: ChipWidth, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true }),
                () => Over(SuccessColor() with { A = Wash(ChipWashAlpha) }, PanelColor()), hover, toggle, ChipWidth),
            ChipShell(new TextElement(() => _loc.T("ag.chip.off"), Color: () => MutedColor(), Width: ChipWidth,
                    Align: TextAlign.Center, NoWrap: true),
                () => Over(MutedColor() with { A = Wash(ChipOffAlpha) }, PanelColor()), hover, toggle, ChipWidth)),
            Width: ChipWidth);
    }

    private HudElement ChipShell(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick, float width)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),   // width 0 + height > 0 = a non-flexible height strut
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fill(fill()), CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f,
            MaxWidth = width,
        };
}
