using VolumeScope.Core.Mpr;

namespace VolumeScope.Core.Surface;

/// <summary>組織の種類（面を作る閾値・表示の色・ボリュームレンダリングの見え方）</summary>
public sealed record TissuePreset(
    string Key,
    string Name,
    double ThresholdHu,
    uint Color,
    WindowLevel SuggestedWindow,
    string Hint);

public static class TissuePresets
{
    public static TissuePreset Bone { get; } = new("bone", "骨", 250, 0xFFF2EAD8, new WindowLevel(400, 1800),
        "皮質骨・海綿骨。骨折や変形の確認、3D プリント用の模型に。");

    public static TissuePreset Skin { get; } = new("skin", "皮膚（体表）", -400, 0xFFE0AC92, new WindowLevel(40, 400),
        "空気と体の境目。体表の形や、手術の皮切位置の確認に。");

    public static TissuePreset Vessels { get; } = new("vessels", "造影血管", 150, 0xFFD4554B, new WindowLevel(150, 600),
        "造影剤で白くなった血管（骨も一緒に出ます）。造影 CT で使います。");

    public static TissuePreset Lung { get; } = new("lung", "肺・気道（空気）", -500, 0xFF8DB7D6, new WindowLevel(-600, 1500),
        "空気を含む部分の境目（肺の表面・気道）。体の外の空気も含みます。");

    public static IReadOnlyList<TissuePreset> All { get; } = [Bone, Skin, Vessels, Lung];
}
