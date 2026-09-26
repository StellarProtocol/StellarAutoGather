# StellarAutoGatherPlugin

A [Stellar Framework](https://github.com/) plugin for **Blue Protocol: Star Resonance** that automatically
harvests life-resource nodes. Pick a resource type from the nearby list, press **Start**, and it walks to and
gathers every node of that type around you — herbs, ore, wood, gems, and more.

## Features

- **Nearby-type picker** — a live list of the distinct gatherable resource types around you, each with a count
  and nearest distance. Click one to lock it; the loop then farms all nearby nodes of that type.
- **Automatic navigation** — routes around obstacles using an offline-baked navmesh, with a straight-line
  fallback and stuck/no-progress watchdogs. Nodes it genuinely can't reach are skipped automatically.
- **Focus mode** — optionally spend **Focus** for the rarer, higher-EXP gather. Strict: when you run out of
  Focus it **stops** cleanly rather than silently doing a normal gather.
- **Start / Stop button** — one master control; Start is enabled once you've picked a resource type.
- **Auto-close reward popups** — dismisses the daily monthly-card / reward popup so a long session isn't
  interrupted.
- **Live counts** — the picker and status line show only currently-available nodes (skipped/just-gathered
  nodes are excluded).
- **Localized UI** — English, 日本語, ไทย, Bahasa Indonesia, Filipino (switches live with the framework's
  language setting).

## Requirements

- [Stellar Framework](https://github.com/) installed in the game directory.

## Installation

Copy `Stellar.AutoGather.dll` into:

```
<GameDir>\stellar\plugins\autogather\
```

Then open the plugin from the in-game launcher (the **Auto-Gather** tile).

## Usage

1. Open the **Auto-Gather** window from the launcher.
2. Stand near the resources you want to farm — the **Nearby Resource Types** list populates automatically.
3. Click a type to lock it (a ▶ marks the locked type).
4. (Optional) Toggle **Focus mode** and **Auto-close reward popups**.
5. Press **Start**. Press **Stop** any time to end the loop.

Auto-gather runs only while you're in the world, alive, and out of combat.

## Configuration

Settings persist to `stellar.autogather.config.json` in the game directory. There's no need to edit it by
hand — everything is controlled from the window.

## Building

```
dotnet build -c Release
```

`Local.props` (gitignored) sets `GameInstallDir` for the auto-deploy step; copy `Local.props.example` to
`Local.props` and point it at your install. DotRecast is ILRepack-merged into the plugin DLL, so it ships as a
single self-contained assembly.

## License

Licensed under the **GNU Affero General Public License v3.0** — see [LICENSE](LICENSE).
