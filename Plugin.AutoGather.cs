using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

// "Auto-Gather" menu — continuously harvest nearby life-resource nodes of ONE locked resource TYPE, picked by its
// real name from a live nearby-node list. Mirrors Plugin.Follow.cs / Plugin.Follow.Roster.cs: master enable toggle,
// a Focus/Normal mode toggle, a live status line, the locked-type nearby count, and a scrollable click-to-select
// VirtualList of the distinct resource types nearby (name ×count nearest-dist), sorted nearest-first.
//
// NOTE: DotRecast is ILRepack-merged INTO this plugin DLL (see Stellar.AutoGather.csproj) — the types resolve from the
// plugin assembly itself, so no plugin-folder AppDomain.AssemblyResolve handler is needed.
public sealed partial class Plugin
{
    private const int   GatherPoolSize   = 8;     // ≈ visible rows + margin
    private const float GatherRowHeight  = 22f;
    private const float GatherListHeight = 154f;  // ~7 visible rows

    private bool _autoGatherEnabled     = false;
    private bool _autoGatherFocus       = false;  // Focus vs Normal gather mode (KB §5b)
    private bool _autoGatherDismissCard = true;   // auto-close the daily monthly-card / reward popup
    private int  _gatherRosterFirst;              // top visible index, tracked from VirtualListElement.OnWindow

    private HudElement BuildAutoGatherRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("ag.title"), Emphasis: true),

        // Master Start/Stop toggle button (mirrors AutoFishing's master control). Start is gated on a resource type
        // being locked — the gather analog of fishing gating Start on being at a fishing spot.
        new ConditionalElement(
            () => _autoGatherEnabled,
            new ButtonElement(
                Label:   () => _loc.T("ag.stop"),
                OnClick: ToggleAutoGather,
                Style:   MenuButtonStyle.Filled,
                Width:   260f)),
        new ConditionalElement(
            () => !_autoGatherEnabled,
            new ButtonElement(
                Label:   () => _loc.T("ag.start"),
                OnClick: ToggleAutoGather,
                Enabled: () => _autoGatherController.LockedName.Length > 0,
                Width:   260f)),

        // Focus / Normal mode (Focus spends ~20 Focus for a rarer award; STRICT — stops the loop when Focus is unavailable).
        new RowElement(new HudElement[]
        {
            new ToggleElement(
                Label: () => "",
                Get:   () => _autoGatherFocus,
                Set:   v  => SetAutoGatherFocus(v)),
            new TextElement(() => _loc.T("ag.focusMode")),
        }, Gap: 6f),

        // Auto-close the daily monthly-card / reward popup so a long AFK session isn't interrupted.
        new RowElement(new HudElement[]
        {
            new ToggleElement(
                Label: () => "",
                Get:   () => _autoGatherDismissCard,
                Set:   v  => SetAutoGatherDismissCard(v)),
            new TextElement(() => _loc.T("ag.autoClosePopups")),
        }, Gap: 6f),

        // Live status + locked-type nearby count.
        new SeparatorElement(),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("ag.label.status"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 52f),
            new TextElement(() => _autoGatherController.Status),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("ag.label.locked"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 52f),
            new TextElement(() =>
            {
                string name = _autoGatherController.LockedName;
                return name.Length == 0
                    ? _loc.T("ag.locked.none")
                    : _loc.TFormat("ag.locked.summary", name, _autoGatherController.LockedTypeCount);
            }),
        }, Gap: 6f),

        // Click-to-select picker: one row per distinct resource type nearby.
        BuildNearbyNodesSection(),
    }, Gap: 8f);

    // Master button click → flip enable through the single SetAutoGatherEnabled path (field + controller + config).
    private void ToggleAutoGather() => SetAutoGatherEnabled(!_autoGatherEnabled);

    private void SetAutoGatherFocus(bool v)
    {
        _autoGatherFocus = v;
        _autoGatherController.FocusMode = v;
        _cfg.Set<bool>("autogather_focus", v);
        _cfg.Save();
    }

    private void SetAutoGatherDismissCard(bool v)
    {
        _autoGatherDismissCard = v;
        _autoGatherController.DismissMonthlyCard = v;
        _cfg.Set<bool>("autogather_dismiss_card", v);
        _cfg.Save();
    }

    // Nearby-type picker: a VirtualList whose Count lambda re-evaluates each render apply (~10 Hz) while the window
    // is open, refreshing the roster on its ~1s throttle even when Auto-Gather is OFF. Clicking a row locks that
    // resource type; the loop then farms all nearby nodes of it. The ▶ marker + accent tint shows the locked type.
    private HudElement BuildNearbyNodesSection() => new ColumnElement(new HudElement[]
    {
        new SeparatorElement(),
        new TextElement(() => _loc.T("ag.section.nearbyTypes"), Emphasis: true),
        new VirtualListElement(
            Count:     () => _autoGatherController.RosterDisplayCount,
            RowHeight: GatherRowHeight,
            Pool:      BuildNearbyNodesPool(),
            OnWindow:  first => _gatherRosterFirst = first,
            Height:    GatherListHeight),
    }, Gap: 6f);

    private HudElement[] BuildNearbyNodesPool()
    {
        var pool = new HudElement[GatherPoolSize];
        for (int s = 0; s < GatherPoolSize; s++)
        {
            int slot = s;
            pool[s] = new SelectableElement(
                new TextElement(() => _autoGatherController.RosterRowLabel(_gatherRosterFirst + slot)),
                OnClick:  () => _autoGatherController.SelectRosterRow(_gatherRosterFirst + slot),
                Selected: () => _autoGatherController.IsRosterRowSelected(_gatherRosterFirst + slot));
        }
        return pool;
    }
}
