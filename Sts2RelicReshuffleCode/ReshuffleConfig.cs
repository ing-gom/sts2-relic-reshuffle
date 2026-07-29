namespace Sts2RelicReshuffle;

/// <summary>
/// Live settings, mirrored out of ModConfig by <see cref="MainFile"/>.
///
/// ★CO-OP: every field here feeds the deterministic re-roll (<see cref="ReshuffleService"/>), which each
/// client derives independently instead of broadcasting. Two peers with DIFFERENT settings would derive
/// DIFFERENT relic sets and desync the lockstep combat sim, so in real co-op the HOST's values are the
/// ones that count — see <see cref="HostReshuffleConfig"/>, which shadows these during a networked run.
/// Read the effective values through <c>Effective*</c> below, never the raw fields.
/// </summary>
internal static class ReshuffleConfig
{
    /// <summary>Master switch. OFF: relics are never re-rolled (the mod becomes inert).</summary>
    public static bool Enabled = true;

    /// <summary>Keep starter relics (Burning Blood etc.) fixed. Default ON — your character identity
    /// shouldn't evaporate on the first elite.</summary>
    public static bool KeepStarter = true;

    /// <summary>Keep Ancient ("고대의 존재") relics fixed. Default ON — these are run-defining picks with
    /// no same-rarity peers worth swapping between.</summary>
    public static bool KeepAncient = true;

    /// <summary>Only roll INTO relics that actually do something in a fight. Default ON — without it a
    /// rare slot can land on a shop-discount relic and read as a dead slot for the whole combat.</summary>
    public static bool CombatRelevantOnly = true;

    /// <summary>Keep relics that the sibling Sts2RelicForge mod has forged (prefix / curse) fixed.
    /// Default ON — a forge record is attached to the relic INSTANCE, so re-rolling it silently
    /// destroys work the player paid for.</summary>
    public static bool KeepForged = true;

    /// <summary>Show the combat-start readout listing what turned into what. Purely presentational, and
    /// deliberately NOT host-authoritative: a peer hiding a panel cannot desync anything, so this one
    /// stays a genuine per-player preference.</summary>
    public static bool ShowBanner = true;

    // ── Effective values ─────────────────────────────────────────────────────────────────────────
    // Co-op client with a host broadcast in hand → the host's value. Host / single-player → our own.
    // Every derivation input MUST be read through these; a raw field read is a latent desync.

    public static bool EffectiveKeepStarter => HostReshuffleConfig.UseHost ? HostReshuffleConfig.KeepStarter : KeepStarter;
    public static bool EffectiveKeepAncient => HostReshuffleConfig.UseHost ? HostReshuffleConfig.KeepAncient : KeepAncient;
    public static bool EffectiveCombatRelevantOnly => HostReshuffleConfig.UseHost ? HostReshuffleConfig.CombatRelevantOnly : CombatRelevantOnly;
    public static bool EffectiveKeepForged => HostReshuffleConfig.UseHost ? HostReshuffleConfig.KeepForged : KeepForged;

    /// <summary>The master switch is host-authoritative too, and it has to be. Relic effects are
    /// simulated in lockstep on every peer, so if one client re-rolls and another doesn't, the two
    /// machines are running different fights — a guaranteed desync, not a preference. Host ON means
    /// everyone gets re-rolls; a client's local toggle only decides their own single-player runs.
    /// (A peer without the mod installed at all can't be helped from here — same rule as every other
    /// gameplay-affecting sister mod: both players install it, or neither does.)</summary>
    public static bool EffectiveEnabled => HostReshuffleConfig.UseHost ? HostReshuffleConfig.Enabled : Enabled;
}
