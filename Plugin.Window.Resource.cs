using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

// RESOURCE panel: the distinct resource types nearby, nearest first, one raised tile per type —
//   [✓ slot] [name ……] [×N] [12 m]
// Clicking a row locks that type (the loop farms all nearby nodes of it). The locked row = accent wash + bold + ✓.
// A VirtualList with 5 rows of height RESERVED (FixedHeight), so the window never grows or shrinks with the roster; the
// empty state is a faint box of the same height with a centred hint (house rule 14).
//
// The Count lambda re-evaluates on each window refresh while the window is open, which refreshes the roster on the
// controller's ~1 s throttle even when Auto-Gather is OFF. While hidden it reports 0, so a Prebuild fill never walks
// the entity list (Plugin-Performance-Rules.md, "Lists backed by eagerly loaded data should report 0 rows while hidden").
public sealed partial class Plugin
{
    private const int   RosterVisibleRows = 5;
    private const int   RosterPoolSize    = RosterVisibleRows + 1;   // visible rows + one partially scrolled in
    private const float CheckColW         = 14f;
    private const float CountColW         = 34f;   // "×12"
    private const float DistColW          = 44f;   // "125 m"
    private static readonly float RosterHeight = ListHeight(RosterVisibleRows);

    private int _rosterFirst;   // top visible index, tracked from VirtualListElement.OnWindow
    private readonly HoverSlots _rowHover = new(RosterPoolSize);

    private int RosterCount() => Shown ? _autoGatherController.RosterDisplayCount : 0;

    private HudElement BuildResourcePanel() => SectionPanel(() => _loc.T("ag.section.resource"), () => _loc.T("ag.hint.resource"),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, RosterHeight),   // reserves the 5-row height in both branches
            new CellElement(new ConditionalElement(() => RosterCount() > 0,
                new VirtualListElement(
                    Count:     RosterCount,
                    RowHeight: ListRowPitch,
                    Pool:      BuildRosterPool(),
                    OnWindow:  first => _rosterFirst = first,
                    Height:    RosterHeight) { FixedHeight = true },
                BuildRosterEmpty()), Weight: 1f),
        }, Gap: 0.01f));

    // "No resources nearby. / Walk closer to plants or ore." centred in a faint 1 px ring box (the toolkit has no
    // dashed outline — KB WindowBuilder-Patterns, EntityInspector empty state).
    private HudElement BuildRosterEmpty() => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(0f, RosterHeight - 2f),   // −2: the ring is drawn over the edge, the box keeps the list height
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("ag.empty.line1"), Color: () => MutedColor(), Align: TextAlign.Center, NoWrap: true),
            new TextElement(() => _loc.T("ag.empty.line2"), Color: () => MutedColor(), Align: TextAlign.Center, NoWrap: true),
        }, Gap: 2f), Weight: 1f),
    }, Gap: 0.01f))
    {
        FillColorFn = () => Transparent, OutlineColorFn = () => Fill(TextColor() with { A = 0.10f }),
        CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 1f,
    };

    private HudElement[] BuildRosterPool()
    {
        var pool = new HudElement[RosterPoolSize];
        for (int s = 0; s < RosterPoolSize; s++)
        {
            int slot = s;
            // Bold can't toggle live → the locked look and the plain look are two complete tiles swapped by a Conditional
            // around the WHOLE tile (never a Conditional inside a bubble's measured child — SDL §6).
            pool[s] = ListSlot(new ConditionalElement(
                () => _autoGatherController.IsRosterRowSelected(_rosterFirst + slot),
                RosterTile(slot, locked: true),
                RosterTile(slot, locked: false)));
        }
        return pool;
    }

    private HudElement RosterTile(int slot, bool locked)
    {
        int Index() => _rosterFirst + slot;
        int Id() => _autoGatherController.RowConfigId(Index());
        ColorRgba RowText() => EnsureContrast(TextColor(), SelectedFill(), TintedMinContrast);

        HudElement content = locked
            ? new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ListTileHeight),
                // "✓" in the REGULAR face: bold ✓/✕ can draw blank (SDL §6).
                new CellElement(new TextElement(() => "✓", Color: () => RowText(), Width: CheckColW, NoWrap: true), Width: CheckColW),
                new CellElement(DropBold(new TextElement(() => _autoGatherController.RowName(Index()), Color: () => RowText(),
                    NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }), Weight: 1f),
                new CellElement(DropBold(new TextElement(() => _autoGatherController.RowCountText(Index()), Color: () => RowText(),
                    Width: CountColW, NoWrap: true, FontSize: 12) { Bold = true }), Width: CountColW),
                new CellElement(DropBold(new TextElement(() => _autoGatherController.RowDistText(Index()), Color: () => RowText(),
                    Width: DistColW, Align: TextAlign.Right, NoWrap: true, FontSize: 12) { Bold = true }), Width: DistColW),
            }, Gap: 8f)
            : new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ListTileHeight),
                new CellElement(new SpacerElement(CheckColW, 1f), Width: CheckColW),
                new CellElement(new TextElement(() => _autoGatherController.RowName(Index()), Color: () => TextColor(),
                    NoWrap: true) { Ellipsize = true }, Weight: 1f),
                new CellElement(new TextElement(() => _autoGatherController.RowCountText(Index()), Color: () => MutedColor(),
                    Width: CountColW, NoWrap: true), Width: CountColW),
                new CellElement(new TextElement(() => _autoGatherController.RowDistText(Index()), Color: () => MutedColor(),
                    Width: DistColW, Align: TextAlign.Right, NoWrap: true), Width: DistColW),
            }, Gap: 8f);

        return new BubbleElement(new SelectableElement(content,
            OnClick: () => _autoGatherController.SelectRosterRow(Index()))
            {
                Bare = true,   // the hover is the bubble fill below, so it keeps the rounded tile shape (R2)
                OnHover = over => _rowHover.Set(slot, Id(), over),
            })
        {
            FillColorFn = () => Fill(locked
                ? (_rowHover.Get(slot, Id()) ? HoverFill(SelectedFill()) : SelectedFill())
                : (_rowHover.Get(slot, Id()) ? HoverTileColor() : TileColor())),
            CornerRadius = TileRadius, PaddingX = 8f, PaddingY = 0f,
        };
    }
}
