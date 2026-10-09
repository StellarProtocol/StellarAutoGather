using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.AutoGather;

public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "Auto-Gather";

    private readonly IPluginServices     _services;
    private readonly ILocalization       _loc;
    private readonly IConfigSection      _cfg;
    private readonly IWindowControl      _window;
    private readonly IDisposable         _launcherEntry;
    private readonly PlayerMoveDriver    _moveDriver;
    private readonly AutoGatherController _autoGatherController;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc      = services.Localization;
        _cfg      = _services.Config.GetSection("settings");

        // Persisted toggles (fields live in Plugin.AutoGather.cs).
        _autoGatherEnabled     = _cfg.Get<bool>("autogather_enabled",      false);
        _autoGatherFocus       = _cfg.Get<bool>("autogather_focus",        false);
        _autoGatherDismissCard = _cfg.Get<bool>("autogather_dismiss_card", true);

        _moveDriver          = new PlayerMoveDriver(_services);
        _autoGatherController = new AutoGatherController(_services, _moveDriver, _loc);
        _autoGatherController.FocusMode         = _autoGatherFocus;
        _autoGatherController.DismissMonthlyCard = _autoGatherDismissCard;
        _autoGatherController.UseNavmesh      = true;   // forced ON (walk around obstacles) — toggle removed
        _autoGatherController.NavmeshFallback = false;  // forced: always skip unreachable nodes — toggle removed

        // Strict Focus mode self-stopped (out of Focus) → clear the enabled flag so the Start/Stop button flips back to
        // "Start" and it doesn't auto-resume. Invoked from the Update/Lua path (main thread), so touching config here is safe.
        _autoGatherController.OnAutoStopped = () =>
        {
            _autoGatherEnabled = false;
            _cfg.Set<bool>("autogather_enabled", false);
            _cfg.Save();
        };

        _window = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "autogather.main",
                Title:       _loc.T("ag.title"),
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - 460f, 20f, 440f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Gameplay tool: only makes sense while in-world, never over a loading screen.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: BuildAutoGatherRoot(),
            OnClose: () => _window!.SetVisible(false)));

        // Title stays the fixed literal "Auto-Gather" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so a pinned tile survives a language change; TitleProvider carries the live-localized display.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "Auto-Gather",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile DISPLAY on a language change; Title above never changes (pin identity).
          TitleProvider = () => _loc.T("ag.title"),
          // Gameplay tool: only surface its launcher tile while in-world.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        _autoGatherController.SetEnabled(_autoGatherEnabled);
        _services.Log.Info("[AutoGather] constructed");
    }

    // Master on/off. No Follow mutual-exclusion here (this plugin has no Follow feature).
    private void SetAutoGatherEnabled(bool v)
    {
        _autoGatherEnabled = v;
        _autoGatherController.SetEnabled(v);
        _cfg.Set<bool>("autogather_enabled", v);
        _cfg.Save();
    }

    public void Dispose()
    {
        _launcherEntry.Dispose();
        _window.Remove();
        _autoGatherController?.Dispose();
    }

    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.AutoGather.autogather-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
