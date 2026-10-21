using System.Numerics;
using Meitou.Content;
using Meitou.Data.Havok;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>How an edge of the original's navmesh ends: shared with a face of its tile, joined to a neighbouring tile, or the mesh's end.</summary>
public enum OriginalEdge : byte { Inner, TileBorder, End }

/// <summary>A zone's exterior navmesh as the original game built it, in world coordinates: polygons (vertex indices) and, per polygon edge, how it ends.</summary>
public sealed record OriginalNavMesh(ZoneCoordinate Zone, Vector3[] Vertices, int[][] Polygons, OriginalEdge[][] Edges);

/// <summary>
/// The original game's cached navmesh tiles (<c>data/newland/land/navtiles/tileX.Y.hkt</c>, Havok tagfiles; docs/game/pathfinding.md "Original tiles"): its
/// <c>hkaiNavMesh</c> faces, edges and vertices, the vertices zone-local at 0.1 scale. Only the exterior mesh (the tile's first) is read; the interior groups that follow
/// it are in a frame of their own (not decoded).
/// </summary>
public static class OriginalNavTiles
{
    /// <summary>Havok units per world unit.</summary>
    const float Scale = 10;

    public static string PathOf(GameInstall install, ZoneCoordinate zone) =>
        Path.Combine(install.DataDirectory, TerrainMaps.LandDirectory, "navtiles", $"tile{zone.X}.{zone.Y}.hkt");

    /// <summary>The zone's exterior mesh (null when the install has no tile for it).</summary>
    public static OriginalNavMesh? LoadExterior(GameInstall install, ZoneCoordinate zone)
    {
        string path = GameFiles.Resolve(PathOf(install, zone));
        return File.Exists(path) ? Exterior(HavokTagfile.ReadFile(path), zone) : null;
    }

    public static OriginalNavMesh? Exterior(HavokTagfile file, ZoneCoordinate zone)
    {
        if (file.Variant("hkaiNavMesh") is not { } mesh || mesh["faces"] is not HkObject[] faces || mesh["edges"] is not HkObject[] edges || mesh["vertices"] is not object[] raw)
            return null;
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var vertices = new Vector3[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            if (raw[i] is float[] v) vertices[i] = new Vector3((float)ox + v[0] * Scale, v[1] * Scale, (float)oz + v[2] * Scale);
        // Edges joined to the neighbouring tiles: the streaming sets' connections (face, edge).
        var joined = new HashSet<(long, long)>();
        if (mesh["streamingSets"] is HkObject[] sets)
            foreach (var set in sets)
                if (set["meshConnections"] is HkObject[] connections)
                    foreach (var c in connections) joined.Add((Int(c["faceIndex"]), Int(c["edgeIndex"])));
        var polygons = new int[faces.Length][];
        var kinds = new OriginalEdge[faces.Length][];
        for (int f = 0; f < faces.Length; f++)
        {
            long start = Int(faces[f]["startEdgeIndex"]), count = Int(faces[f]["numEdges"]);
            var poly = new int[count];
            var kind = new OriginalEdge[count];
            for (int k = 0; k < count; k++)
            {
                var e = edges[start + k];
                poly[k] = (int)Int(e["a"]);
                kind[k] = Int(e["oppositeFace"]) >= 0 ? OriginalEdge.Inner : joined.Contains((f, start + k)) ? OriginalEdge.TileBorder : OriginalEdge.End;
            }
            polygons[f] = poly;
            kinds[f] = kind;
        }
        return new OriginalNavMesh(zone, vertices, polygons, kinds);
    }

    static long Int(object? v) => v is long l ? l : 0;
}
