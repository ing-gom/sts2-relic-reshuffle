namespace Sts2RelicReshuffle;

/// <summary>
/// Live settings, mirrored out of ModConfig by <see cref="MainFile"/>.
///
/// ★DELIBERATELY TWO TOGGLES. Everything else the mod used to expose (pin starters, combat-useful only,
/// pin re-forged relics, show the readout) had exactly one sensible value, and a setting whose other
/// position is simply worse is noise in a settings screen. What genuinely differs between players is
/// only how wide they want the random pool to be, so that is all that is adjustable — and both default
/// OFF, i.e. the mod starts at its narrowest, most predictable pool.
///
/// ★CO-OP: both fields feed the deterministic derivation, so in a networked run the HOST's values are
/// the ones that count — see <see cref="HostReshuffleConfig"/>. Always read through <c>Effective*</c>;
/// a raw field read is a latent desync.
/// </summary>
internal static class ReshuffleConfig
{
    /// <summary>Let Ancient ("고대의 존재") relics take part — both as something that can be replaced and
    /// as something a re-roll can hand out. Default OFF: they are run-defining picks, and most of them
    /// live in the game's EventRelicPool, which the mod does not otherwise read.</summary>
    public static bool IncludeAncient = false;

    /// <summary>Let event-only relics take part. Default OFF: they are rewards for specific events, so
    /// handing them out at random gives away content the run never earned.</summary>
    public static bool IncludeEvent = false;

    public static bool EffectiveIncludeAncient
        => HostReshuffleConfig.UseHost ? HostReshuffleConfig.IncludeAncient : IncludeAncient;

    public static bool EffectiveIncludeEvent
        => HostReshuffleConfig.UseHost ? HostReshuffleConfig.IncludeEvent : IncludeEvent;
}
