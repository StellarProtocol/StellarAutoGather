using System.IO;
using System.IO.Compression;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace Stellar.AutoGather;

// LOADER ONLY (no bake). Reads a serialized DotRecast navmesh baked offline by the NavmeshBake toolchain — the
// Read + gzip-or-raw decode + VertsPerPoly extracted from StellarExperimentPlugin's NavmeshBaker.cs. The bake half
// (DotRecast.Recast, Build/Write/self-test) is intentionally NOT ported: Auto-Gather consumes the shipped
// <sceneFile>.navmesh.bin files — now EMBEDDED in the plugin DLL (ReadBytes), with the <GameInstallDir>\stellar\navmesh\
// copies as a fallback — it never bakes.
internal static class NavmeshLoader
{
    // MUST match the vertsPerPoly the bake used (DtMeshSetReader needs it to size the poly arrays). The offline
    // toolchain bakes at 6; a mismatch corrupts the deserialized mesh.
    private const int VertsPerPoly = 6;

    // From a file on disk (the game-folder <GameInstallDir>\stellar\navmesh\ fallback).
    public static DtNavMesh Read(string path) => ReadBytes(File.ReadAllBytes(path));

    // From an in-memory buffer (an EMBEDDED navmesh resource). Shares the SAME gzip-or-raw decode + DtMeshSetReader
    // path as Read(path) — the magic-peek lives once in Decompress, so embedded and on-disk loads behave identically.
    public static DtNavMesh ReadBytes(byte[] raw)
    {
        byte[] bytes = Decompress(raw);
        using var ms = new MemoryStream(bytes, false);
        using var br = new BinaryReader(ms);
        return new DtMeshSetReader().Read(br, VertsPerPoly);
    }

    // Transparently un-gzip if the buffer starts with the gzip magic (0x1F 0x8B); otherwise return it unchanged.
    // Backward-compatible: old RAW files and new GZIPPED offline files both load, whether from disk or embedded. A
    // corrupt/failed decompress throws → the caller (NavGatherFollower.EnsureNavMesh) catches it as a load-fail.
    private static byte[] Decompress(byte[] raw)
    {
        if (raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B)
        {
            using var ms = new MemoryStream(raw, false);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return outMs.ToArray();
        }
        return raw;
    }
}
