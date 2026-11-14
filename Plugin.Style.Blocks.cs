using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

// More house-kit building blocks (Stellar Design Language §4; reference StellarPositionPlugin Plugin.Style.Blocks.cs):
// the accent icon tile + ToolHeader, the status dot, raised list tiles (26 px + 2 px gap) with per-slot hover, the
// segmented mode chip, and the fixed-height single line.
public sealed partial class Plugin
{
    // ── icon tile + tool header ───────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with the white glyph PNG centred (art can't follow the theme, the tile
    // does). The fixed-width Cell pins the column (Cell force-expands a child's width — the icon-squish gotcha) and the
    // inner Row keeps the image square. Radius ≈ 22 % of the size (9 at 40 px).
    private const float ToolIconSize  = 40f;
    private const int   HeaderGlyphPx = 24;

    private HudElement IconBox(Func<byte[]?> png, float size, int glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(png, glyph, glyph) }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fill(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // Window title block: [40 px icon] [bold title over a live status line]. The text column sits in a weighted Cell so a
    // long status ellipsizes instead of widening the window.
    private HudElement ToolHeader(Func<byte[]?> icon, Func<string> title, HudElement status) => new RowElement(new HudElement[]
    {
        IconBox(icon, ToolIconSize, HeaderGlyphPx),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
            status,
        }, Gap: 2f), Weight: 1f),
    }, Gap: 10f);

    // ── status dot ────────────────────────────────────────────────────────────────────────────────────────────────
    // An 8 px round bubble (radius 3 = RoundRadius(8), R1) whose fill is the live state colour. Drawn as a bubble, not a
    // "●" glyph, so its size doesn't depend on the font fallback.
    private const float DotSize = 8f;

    private HudElement StatusDot(Func<ColorRgba> color) => new CellElement(new BubbleElement(new SpacerElement(DotSize, DotSize))
    {
        FillColorFn = () => Fill(color()), CornerRadius = RoundRadius(DotSize), PaddingX = 0f, PaddingY = 0f,
        MaxWidth = DotSize,
    }, Width: DotSize);

    // ── list tiles ────────────────────────────────────────────────────────────────────────────────────────────────
    // ⚠️ A VirtualList slot STRETCHES its one child to the full RowHeight, so a 26 px tile in a 28 px slot filled all 28
    // and the rows touched (Maestro, in-game 2026-11-06). Each pooled row is ListSlot(tile) = [26 px tile, 2 px spacer].
    private const float ListTileHeight = 26f;
    private const float ListRowGap     = 2f;
    private const float ListRowPitch   = ListTileHeight + ListRowGap;   // the VirtualList RowHeight
    private const float TileRadius     = 6f;

    private static HudElement ListSlot(HudElement tile) => new ColumnElement(new HudElement[]
    {
        tile,
        new SpacerElement(0f, ListRowGap),
    }, Gap: 0.01f);

    // Viewport height that shows exactly `rows` rows: the last row's 2 px gap falls outside, so the list ends flush.
    private static float ListHeight(int rows) => rows * ListRowPitch - ListRowGap;

    private ColorRgba HoverTileColor() => Over(Menu.MenuText with { A = 0.05f }, TileColor());

    // Per pool-SLOT hover, keyed by the row's identity (config id) and reset when the slot rebinds to a different item —
    // a click that re-sorts the list must not leave the hover wash on the row that slides under the pointer (SDL §5).
    private sealed class HoverSlots
    {
        private readonly bool[] _over;
        private readonly int[]  _id;
        public HoverSlots(int n) { _over = new bool[n]; _id = new int[n]; }

        public bool Get(int slot, int id)
        {
            if (id != _id[slot]) { _id[slot] = id; _over[slot] = false; }
            return _over[slot];
        }

        public void Set(int slot, int id, bool over) { _over[slot] = over; _id[slot] = id; }
    }

    // ── segmented chip (GATHER MODE Normal | Focus) ───────────────────────────────────────────────────────────────
    // 24 px round chip, fixed width. Active = bold on the accent wash; inactive = muted on the muted wash. Bold can't
    // toggle live, so the two looks are complete chips swapped by a Conditional around the WHOLE chip in one fixed Cell.
    // Disabled (while running) = RECESSED (rule 22): sunken fill, faint label; the active one keeps a faint accent tint +
    // bold so you can still read which mode the run uses. Clicks are swallowed while disabled.
    private HudElement SegmentChip(Func<string> label, Func<bool> active, Func<bool> enabled, Action onClick, float width)
    {
        var hover = new HoverFlag();
        void Click() { if (enabled()) onClick(); }
        ColorRgba ActiveFill() => !enabled() ? Over(AccentColor() with { A = 0.18f }, DisabledFill(PanelColor()))
            : hover.On ? HoverFill(SelectedFill()) : SelectedFill();
        ColorRgba IdleFill() => !enabled() ? DisabledFill(PanelColor())
            : Over(MutedColor() with { A = ChipOffAlpha + (hover.On ? ChipHoverAlpha : 0f) }, PanelColor());
        return new CellElement(new ConditionalElement(active,
            ChipShell(DropBold(new TextElement(label,
                    Color: () => enabled() ? EnsureContrast(TextColor(), SelectedFill(), TintedMinContrast) : DisabledLabel(PanelColor()),
                    Width: width, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true }),
                ActiveFill, hover, Click, width),
            ChipShell(new TextElement(label, Color: () => enabled() ? MutedColor() : DisabledLabel(PanelColor()), Width: width,
                    Align: TextAlign.Center, NoWrap: true),
                IdleFill, hover, Click, width)),
            Width: width);
    }

    // ONE always-present line at a fixed height: a 0-width height strut pins the row so bold / empty / changing text
    // never changes the panel height; the content sits in a weighted cell so it ellipsizes instead of widening.
    private static HudElement FixedLine(HudElement content, float height = PillHeight) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, height),
        new CellElement(content, Weight: 1f),
    }, Gap: 0.01f);
}
