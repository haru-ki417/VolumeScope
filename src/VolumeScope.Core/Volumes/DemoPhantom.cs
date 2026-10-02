using VolumeScope.Core.Geometry;

namespace VolumeScope.Core.Volumes;

/// <summary>
/// 見本の CT（人工的に作った胸部の模型）。実際の患者データがなくても操作を試せるようにするため。
/// 体（軟部組織 40 HU）・左右の肺（-850 HU）・背骨（椎体 + 皮質骨）・肋骨・造影された大動脈（250 HU）・
/// 肺の小さな結節（直径 12 mm）を含む。値には少し雑音を加えている。
/// </summary>
public static class DemoPhantom
{
    public const double NoduleDiameterMm = 12;

    /// <summary>結節の中心（患者座標 mm）。計測の練習用</summary>
    public static readonly Vec3 NoduleCenter = new(45, 5, 25);

    /// <param name="spacing">ボクセルの大きさ（mm）。小さいほど細かく、重くなる</param>
    public static Volume Create(double spacing = 1.0, int seed = 7)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacing);
        // 患者座標で x: -110〜110（右→左）, y: -90〜90（前→後ろ）, z: -100〜100（足→頭）
        int w = (int)Math.Round(220 / spacing), h = (int)Math.Round(180 / spacing), d = (int)Math.Round(200 / spacing);
        var origin = new Vec3(-110 + spacing / 2, -90 + spacing / 2, -100 + spacing / 2);
        var geometry = new VolumeGeometry(origin, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, spacing, spacing, spacing);
        var field = new float[(long)w * h * d];
        Parallel.For(0, d, z =>
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    field[(long)z * w * h + y * w + x] = (float)Value(geometry.IndexToPatient(new Vec3(x, y, z)));
        });

        // 実際の CT のように境目を少しぼかす（部分容積効果）。これがないと、面に階段状の模様が出る
        for (int pass = 0; pass < 2; pass++)
        {
            Blur(field, w, h, d, 1);
            Blur(field, w, h, d, w);
            Blur(field, w, h, d, w * h);
        }

        var data = new short[field.Length];
        Parallel.For(0, d, z =>
        {
            var random = new Random(seed * 7919 + z);
            for (long i = (long)z * w * h; i < (long)(z + 1) * w * h; i++)
            {
                double hu = field[i];
                if (hu > -950) hu += (random.NextDouble() - 0.5) * 24; // 雑音（空気の外側には加えない）
                data[i] = (short)Math.Round(hu);
            }
        });

        return new Volume(w, h, d, data, geometry, new VolumeInfo
        {
            PatientName = "DEMO PHANTOM",
            PatientId = "DEMO-0001",
            StudyDate = "20261002",
            Modality = "CT",
            SeriesDescription = "見本の模型（人工データ）",
            Manufacturer = "VolumeScope",
            SliceThickness = spacing,
            Warnings = ["これは人工的に作った見本のデータです。実際の患者の画像ではありません。"],
        });
    }

    /// <summary>患者座標 p の CT 値</summary>
    internal static double Value(Vec3 p)
    {
        // 体は上下に長い樽の形（胴体）。境目は約 1.5 mm でなめらかに変わる（実際の CT と同じように）
        double gxB = 2 * p.X / (95 * 95), gyB = 2 * p.Y / (75 * 75), gzB = 6 * Math.Pow(Math.Abs(p.Z), 5) / Math.Pow(96, 6);
        double fBody = Sq(p.X / 95) + Sq(p.Y / 75) + Math.Pow(Math.Abs(p.Z) / 96, 6);
        double gradBody = Math.Sqrt(Sq(gxB) + Sq(gyB) + Sq(gzB)) + 1e-9;
        double inBody = Inside((fBody - 1) / gradBody);
        if (inBody <= 0) return -1000;

        // 皮下脂肪（体の外側 8 mm ほど）
        double inMuscle = Inside((fBody - 0.85) / gradBody);
        double hu = 40 * inMuscle + -90 * (1 - inMuscle);

        // 左右の肺
        foreach (double side in new[] { -1.0, 1.0 })
        {
            var c = new Vec3(side * 45, 0, 15);
            double a = 32, b = 42, cc = 65;
            double f = Ellipsoid(p, c, a, b, cc);
            double grad = 2 * Math.Sqrt(Sq((p.X - c.X) / (a * a)) + Sq((p.Y - c.Y) / (b * b)) + Sq((p.Z - c.Z) / (cc * cc))) + 1e-9;
            double inLung = Inside((f - 1) / grad);
            hu = -850 * inLung + hu * (1 - inLung);
        }

        // 肺の結節（左肺）
        if (Vec3.Distance(p, NoduleCenter) < NoduleDiameterMm / 2) hu = 60;

        // 背骨: 椎体（14 mm）と椎間板（6 mm）が交互に並ぶ
        var spine = new Vec3(0, 45, 0);
        double rSpine = Math.Sqrt(Sq(p.X - spine.X) + Sq(p.Y - spine.Y));
        double phase = ((p.Z + 1000) % 20 + 20) % 20;
        if (rSpine < 14 && Math.Abs(p.Z) < 95)
            hu = phase < 14 ? (rSpine > 11.5 || phase < 1.5 || phase > 12.5 ? 1000 : 300) : 80;
        // 棘突起（背中側への突起）
        if (phase < 12 && Math.Abs(p.X) < 4 && p.Y > 58 && p.Y < 75 && Math.Abs(p.Z) < 95) hu = 700;

        // 肋骨: 体の周りを回る細い管（左右 6 本ずつ）
        for (int i = 0; i < 6; i++)
        {
            double zc = 55 - i * 22 + 0.25 * (p.Y - 45); // 背中から前へ下がる
            double ring = Ellipsoid(new Vec3(p.X, p.Y, 0), new Vec3(0, 5, 0), 85, 66, 1e9);
            double dr = (Math.Sqrt(ring) - 1) * 72; // 楕円の線からのおおよその距離（mm）
            // 背骨とのあいだには関節のすき間を残す（背骨の両脇 22 mm より外側だけ）
            if (Math.Sqrt(Sq(dr) + Sq(p.Z - zc)) < 4.5 && p.Y > -45 && Math.Abs(p.X) > 22) hu = 850;
        }

        // 造影された大動脈（背骨の前、上で弓状に曲がる）
        var aorta = new Vec3(12, 22, 0);
        if (p.Z < 55 && Math.Sqrt(Sq(p.X - aorta.X) + Sq(p.Y - aorta.Y)) < 11) hu = 250;
        double arch = Math.Sqrt(Sq(Math.Sqrt(Sq(p.Y - 2) + Sq(p.Z - 55)) - 20) + Sq(p.X - 12));
        if (p.Z >= 55 && arch < 11) hu = 250;
        // 枝（頸部へ向かう細い血管 2 本）
        foreach (double bx in new[] { 0.0, 22.0 })
            if (p.Z > 70 && p.Z < 95 && Math.Sqrt(Sq(p.X - bx) + Sq(p.Y + 10)) < 4) hu = 250;

        // 体の外（空気）となめらかにつなぐ
        return hu * inBody + -1000 * (1 - inBody);
    }

    /// <summary>境目からの距離 d（mm、内側が負）→ 内側である割合（0〜1）。約 1.5 mm でなめらかに変わる</summary>
    private static double Inside(double d)
    {
        double t = Math.Clamp(0.5 - d / 1.5, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>[1 2 1] / 4 の平滑化を、stride ごとに並んだ向きに 1 回かける</summary>
    private static void Blur(float[] f, int w, int h, int d, long stride)
    {
        var src = (float[])f.Clone();
        int size = stride == 1 ? w : stride == w ? h : d;
        Parallel.For(0, d, z =>
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    long i = (long)z * w * h + (long)y * w + x;
                    int pos = stride == 1 ? x : stride == w ? y : z;
                    float a = pos > 0 ? src[i - stride] : src[i];
                    float b = pos < size - 1 ? src[i + stride] : src[i];
                    f[i] = (a + 2 * src[i] + b) / 4;
                }
        });
    }

    private static double Ellipsoid(Vec3 p, Vec3 c, double a, double b, double cc) =>
        Sq((p.X - c.X) / a) + Sq((p.Y - c.Y) / b) + Sq((p.Z - c.Z) / cc);

    private static double Sq(double v) => v * v;
}
