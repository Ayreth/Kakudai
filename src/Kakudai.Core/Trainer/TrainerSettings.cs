using Kakudai.Core.Osu;

namespace Kakudai.Core.Training;

/// <remarks>
/// The *Locked flags are page state, persisted so locks survive map changes; Resolve never reads them.
/// A locked stat is carried as its manual value, a defined value: Emulate HR still applies over it, Scale does not.
/// The page turns Scale AR/OD off itself.
/// </remarks>
public sealed record TrainerSettings(
    double Factor = 1.5, bool CrushStreams = false, bool AccentAngles = false, bool KiaiOnly = false,
    double Rate = 1.0, double? LockedBpm = null,
    double? Hp = null, double? Cs = null, double? Ar = null, double? Od = null,   // manual defined values, null = automatic
    bool HpLocked = false, bool CsLocked = false, bool ArLocked = false, bool OdLocked = false,
    bool FlipMap = false, FlipMode FlipMode = FlipMode.Vertical, bool EmulateHr = false,
    bool ScaleAr = true, bool ScaleOd = true, bool ChangePitch = false);
