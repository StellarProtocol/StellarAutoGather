using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Stellar.AutoGather;

// Parses the shipped scene_map.json — { "<CurrentSceneId>": {"name":"…","file":"…","resId":N}, … } (610 scenes,
// generated offline) — into CurrentSceneId → (display name, scene file). The navmesh file is keyed by `file` so each
// DISTINCT scene (even ones sharing a path prefix like fld001 / fld001_04_underground) gets its own <file>.navmesh.bin.
//
// Regex-based (dependency-free — no System.Text.Json needed at runtime): the generated file's `file`/`name` are simple
// quoted strings and `[^}]` spans the pretty-printed newlines, so a per-entry match is robust for this fixed shape.
internal static class SceneMap
{
    private static readonly Regex Entry  = new(@"""(\d+)""\s*:\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex FileRx = new(@"""file""\s*:\s*""(?<v>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex NameRx = new(@"""name""\s*:\s*""(?<v>[^""]*)""", RegexOptions.Compiled);

    // Loads the scene map from the EMBEDDED scene_map.json (merged into the plugin DLL — LogicalName
    // "Stellar.AutoGather.scene_map.json"). No sidecar file ships anymore. An optional on-disk fallback path is honored
    // only as a dev override (a file dropped next to the DLL). Returns null when neither source is available/valid.
    public static Dictionary<int, (string name, string file)>? Load(string? fallbackFile = null)
    {
        try
        {
            using var s = typeof(SceneMap).Assembly.GetManifestResourceStream("Stellar.AutoGather.scene_map.json");
            if (s != null)
            {
                using var r = new StreamReader(s);
                return Parse(r.ReadToEnd());
            }
        }
        catch { /* fall through to the on-disk dev override */ }

        try
        {
            if (fallbackFile != null && File.Exists(fallbackFile))
                return Parse(File.ReadAllText(fallbackFile));
        }
        catch { }
        return null;
    }

    public static Dictionary<int, (string name, string file)> Parse(string json)
    {
        var map = new Dictionary<int, (string, string)>();
        foreach (Match m in Entry.Matches(json))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
            string body = m.Groups["body"].Value;
            var fm = FileRx.Match(body);
            if (!fm.Success || fm.Groups["v"].Value.Length == 0) continue;   // file is the key; skip entries without one
            var nm = NameRx.Match(body);
            map[id] = (nm.Success ? nm.Groups["v"].Value : "", fm.Groups["v"].Value);
        }
        return map;
    }
}
