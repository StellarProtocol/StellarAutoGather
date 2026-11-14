using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

// Core: config load, window / launcher registration, logout + dispose, and the persisted toggles. The window body is
// Plugin.Window*.cs (house style, approved spec _scratch\prototypes\autogather-redesign-APPROVED.md), the help popover
// Plugin.Help.cs, the house-style kit Plugin.Style*.cs, the Focus limit setting Plugin.FocusLimit.cs. The gather loop
// itself is AutoGatherController*.cs.
//
// NOTE: DotRecast is ILRepack-merged INTO this plugin DLL (see Stellar.AutoGather.csproj) — the types resolve from the
// plugin assembly itself, so no plugin-folder AppDomain.AssemblyResolve handler is needed.
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "Auto-Gather";

    private readonly IPluginServices      _services;
    private readonly ILocalization        _loc;
    private readonly IConfigSection       _cfg;
    private readonly IWindowControl       _window;
    private readonly IDisposable          _launcherEntry;
    private readonly PlayerMoveDriver     _moveDriver;
    private readonly AutoGatherController _autoGatherController;

    private bool _autoGatherEnabled;
    private bool _autoGatherFocus;              // Focus vs Normal gather mode (KB §5b)
    private bool _autoGatherDismissCard = true; // auto-close the daily monthly-card / reward popup

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc      = services.Localization;
        _cfg      = _services.Config.GetSection("settings");

        _autoGatherEnabled     = _cfg.Get<bool>("autogather_enabled",      false);
        _autoGatherFocus       = _cfg.Get<bool>("autogather_focus",        false);
        _autoGatherDismissCard = _cfg.Get<bool>("autogather_dismiss_card", true);

        _moveDriver          = new PlayerMoveDriver(_services);
        _autoGatherController = new AutoGatherController(_services, _moveDriver, _loc);
        _autoGatherController.FocusMode         = _autoGatherFocus;
        _autoGatherController.DismissMonthlyCard = _autoGatherDismissCard;
        // Focus limit: stop a Focus run after spending this much Focus (-1 = no limit). Manual setting, default 200.
        _autoGatherController.FocusLimit = NormalizeFocusLimit(_cfg.Get<int>("autogather_focus_limit", DefaultFocusLimit));
        _autoGatherController.UseNavmesh      = true;   // forced ON (walk around obstacles) — toggle removed
        _autoGatherController.NavmeshFallback = false;  // forced: always skip unreachable nodes — toggle removed

        // The loop stopped itself (out of Focus / Focus limit reached) → clear the enabled flag so the footer pill flips
        // back to Start and it doesn't auto-resume. Invoked from the Update path (main thread), so config is safe here.
        _autoGatherController.OnAutoStopped = () =>
        {
            _autoGatherEnabled = false;
            _cfg.Set<bool>("autogather_enabled", false);
            _cfg.Save();
        };

        // The help popover first: the "?" fills read _tipWindow, and they may be polled during the main build.
        _tipWindow = RegisterTipWindow();

        _window = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "autogather.main",
                Title:       _loc.T("ag.title"),
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - WindowWidth - 20f, 20f, WindowWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              TitleProvider = () => _loc.T("ag.title"),
              // Built ahead of the first open (Plugin-Performance-Rules.md). Build-time callbacks (slider Get) are plain
              // field reads, and the roster / Focus balance report nothing while hidden (Plugin.Window*.cs).
              Prebuild = true,
              // Gameplay tool: only makes sense while in-world, never over a loading screen.
              ShouldRender = InWorld },
            Root: BuildRoot(),
            OnClose: () => { CloseTip(); FlushFocusLimitSave(); _window!.SetVisible(false); }));

        // The phase stays World through the logout transition, so ShouldRender alone would leave the windows up on the
        // loading screen; close them synchronously on Logout (house rule, Stellar-Design-Language.md §5).
        _services.ClientState.Logout += OnLogoutHideWindows;

        // Title stays the fixed literal "Auto-Gather" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so a pinned tile survives a language change; TitleProvider carries the live-localized display.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "Auto-Gather",
            IconPng: Icon(128),
            IconKey: null,
            // Opens from hidden → remounts on top of its category; no BringToFront needed (CLAUDE.md Rule 5).
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile DISPLAY on a language change; Title above never changes (pin identity).
          TitleProvider = () => _loc.T("ag.title"),
          // Gameplay tool: only surface its launcher tile while in-world.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        _autoGatherController.SetEnabled(_autoGatherEnabled);
        _services.Log.Info("[AutoGather] constructed");
    }

    private bool InWorld() => _services.ClientState.Phase == GamePhase.World
                              && (_services.ClientState.UiState & GameUIState.Loading) == 0;

    private void OnLogoutHideWindows()
    {
        CloseTip();
        FlushFocusLimitSave();
        _window.SetVisible(false);
    }

    // ── persisted toggles ─────────────────────────────────────────────────────────────────────────────────────────
    // Footer Start/Stop → flip enable through the single SetAutoGatherEnabled path (field + controller + config).
    private void ToggleAutoGather() => SetAutoGatherEnabled(!_autoGatherEnabled);

    // Master on/off. No Follow mutual-exclusion here (this plugin has no Follow feature).
    private void SetAutoGatherEnabled(bool v)
    {
        _autoGatherEnabled = v;
        _autoGatherController.SetEnabled(v);
        _cfg.Set<bool>("autogather_enabled", v);
        _cfg.Save();
    }

    private void SetAutoGatherFocus(bool v)
    {
        if (v == _autoGatherFocus) return;
        _autoGatherFocus = v;
        _autoGatherController.FocusMode = v;
        _cfg.Set<bool>("autogather_focus", v);
        _cfg.Save();
    }

    private void ToggleDismissCard()
    {
        _autoGatherDismissCard = !_autoGatherDismissCard;
        _autoGatherController.DismissMonthlyCard = _autoGatherDismissCard;
        _cfg.Set<bool>("autogather_dismiss_card", _autoGatherDismissCard);
        _cfg.Save();
    }

    public void Dispose()
    {
        try { _services.ClientState.Logout -= OnLogoutHideWindows; } catch { }
        FlushFocusLimitSave();
        _launcherEntry.Dispose();
        _tipWindow.Remove();
        _window.Remove();
        _autoGatherController?.Dispose();
    }

    // Launcher tile = the 128 px art; window header = the exact HeaderGlyphPx bitmap (Plugin.Style.Blocks.cs).
    // Cached per size (null results too, so a missing resource isn't re-probed every frame by the image element).
    private static readonly System.Collections.Generic.Dictionary<int, byte[]?> _iconPngs = new();
    private static byte[]? Icon(int px)
    {
        if (!_iconPngs.TryGetValue(px, out var png)) _iconPngs[px] = png = LoadIconPng(px);
        return png;
    }

    // Loads the pre-drawn bitmap for exactly <px> (12/16/24/32/40/64/128) — each size is hinted to its own pixel grid, so
    // callers ask for the size they draw at instead of scaling one master (Stellar-Design-Language.md "Icon art rule").
    private static byte[]? LoadIconPng(int px)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream($"Stellar.AutoGather.autogather-{px}.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
