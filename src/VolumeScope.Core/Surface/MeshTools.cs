namespace VolumeScope.Core.Surface;

/// <summary>面の後処理（小さな破片の除去・なめらかにする）</summary>
public static class MeshTools
{
    /// <summary>
    /// つながった部分ごとに分け、小さな部分（雑音でできた破片など）を除く。
    /// minFraction: いちばん大きい部分に対する三角形の数の割合がこれ未満なら除く
    /// </summary>
    public static Mesh RemoveSmallComponents(Mesh mesh, double minFraction = 0.01, int minTriangles = 60, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.TriangleCount == 0) return mesh;
        var parent = Enumerable.Range(0, mesh.VertexCount).ToArray();
        int Find(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }
        var idx = mesh.Indices;
        for (int t = 0; t < idx.Length; t += 3)
        {
            int a = Find(idx[t]), b = Find(idx[t + 1]), c = Find(idx[t + 2]);
            parent[b] = a;
            parent[Find(c)] = a;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var count = new Dictionary<int, int>();
        for (int t = 0; t < idx.Length; t += 3)
        {
            int r = Find(idx[t]);
            count[r] = count.GetValueOrDefault(r) + 1;
        }
        int largest = count.Values.Max();
        int limit = Math.Max(minTriangles, (int)(largest * minFraction));
        var keep = count.Where(kv => kv.Value >= limit || kv.Value == largest).Select(kv => kv.Key).ToHashSet();
        var kept = new List<int>(idx.Length);
        for (int t = 0; t < idx.Length; t += 3)
            if (keep.Contains(Find(idx[t]))) kept.AddRange([idx[t], idx[t + 1], idx[t + 2]]);
        return Compact(mesh, kept.ToArray());
    }

    /// <summary>つながった部分の数</summary>
    public static int CountComponents(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var parent = Enumerable.Range(0, mesh.VertexCount).ToArray();
        int Find(int a)
        {
            while (parent[a] != a) a = parent[a] = parent[parent[a]];
            return a;
        }
        for (int t = 0; t < mesh.Indices.Length; t += 3)
        {
            int a = Find(mesh.Indices[t]);
            parent[Find(mesh.Indices[t + 1])] = a;
            parent[Find(mesh.Indices[t + 2])] = a;
        }
        return mesh.Indices.Select(Find).Distinct().Count();
    }

    /// <summary>
    /// Taubin の方法でなめらかにする（ふつうの平均化と違い、体積がほとんど縮まない）。
    /// マーチングキューブス法の小さな段差を減らし、3D プリントの見た目をよくする
    /// </summary>
    public static Mesh TaubinSmooth(Mesh mesh, int iterations = 10, float lambda = 0.5f, float mu = -0.53f, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.TriangleCount == 0 || iterations <= 0) return mesh;
        var (start, neighbors) = Adjacency(mesh);
        var p = (float[])mesh.Positions.Clone();
        var tmp = new float[p.Length];
        for (int it = 0; it < iterations; it++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Step(p, tmp, start, neighbors, lambda);
            Step(tmp, p, start, neighbors, mu);
        }
        return new Mesh(p, mesh.Indices);
    }

    private static void Step(float[] src, float[] dst, int[] start, int[] neighbors, float factor)
    {
        Parallel.For(0, start.Length - 1, v =>
        {
            int s = start[v], e = start[v + 1];
            int o = v * 3;
            if (e == s)
            {
                dst[o] = src[o];
                dst[o + 1] = src[o + 1];
                dst[o + 2] = src[o + 2];
                return;
            }
            float ax = 0, ay = 0, az = 0;
            for (int k = s; k < e; k++)
            {
                int n = neighbors[k] * 3;
                ax += src[n];
                ay += src[n + 1];
                az += src[n + 2];
            }
            float inv = 1f / (e - s);
            dst[o] = src[o] + factor * (ax * inv - src[o]);
            dst[o + 1] = src[o + 1] + factor * (ay * inv - src[o + 1]);
            dst[o + 2] = src[o + 2] + factor * (az * inv - src[o + 2]);
        });
    }

    /// <summary>
    /// 頂点ごとの隣の頂点（CSR 形式）。三角形ごとに 2 つの隣を足すので、閉じた面では同じ隣が 2 回ずつ並ぶ
    /// （平均には影響しない）。重複を除く集合を使わないので、大きな面でもメモリが少なくて済む
    /// </summary>
    private static (int[] Start, int[] Neighbors) Adjacency(Mesh mesh)
    {
        int n = mesh.VertexCount;
        var idx = mesh.Indices;
        var start = new int[n + 1];
        foreach (int v in idx) start[v + 1] += 2;
        for (int v = 0; v < n; v++) start[v + 1] += start[v];
        var fill = (int[])start.Clone();
        var neighbors = new int[start[n]];
        for (int t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            neighbors[fill[a]++] = b;
            neighbors[fill[a]++] = c;
            neighbors[fill[b]++] = c;
            neighbors[fill[b]++] = a;
            neighbors[fill[c]++] = a;
            neighbors[fill[c]++] = b;
        }
        return (start, neighbors);
    }

    /// <summary>使われていない頂点を除き、番号を詰める</summary>
    private static Mesh Compact(Mesh mesh, int[] indices)
    {
        var map = new int[mesh.VertexCount];
        Array.Fill(map, -1);
        var positions = new List<float>();
        var normals = new List<float>();
        var newIdx = new int[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            int v = indices[i];
            if (map[v] < 0)
            {
                map[v] = positions.Count / 3;
                positions.AddRange([mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]]);
                normals.AddRange([mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]]);
            }
            newIdx[i] = map[v];
        }
        return new Mesh(positions.ToArray(), newIdx, normals.ToArray());
    }
}
