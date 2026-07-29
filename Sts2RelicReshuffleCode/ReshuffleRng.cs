using System;

namespace Sts2RelicReshuffle;

/// <summary>
/// A tiny deterministic RNG for the per-combat re-roll.
///
/// ★WHY NOT the run's own RNG: consuming <c>RunState.Rng</c> would shift every downstream draw (rewards,
/// shop stock, map), so a Reshuffle run could never be compared against a vanilla seed and the mod would
/// silently perturb everything it touches. Instead we derive an INDEPENDENT stream from values every
/// peer already agrees on — the run seed, the floor, the player's NetId — and never touch the run RNG.
///
/// ★WHY splitmix32 rather than <c>System.Random</c>: the derivation feeds nearly-identical seeds
/// (consecutive floors, adjacent slot indices) and we need them decorrelated. System.Random's
/// initialization leaks that structure straight into the first draws — the exact defect
/// [[reference_sts2_rng_correlation]] documents in the game's own seeding. splitmix32's avalanche makes
/// neighbouring seeds independent, which is what keeps slot 0 and slot 1 from moving in lockstep.
/// </summary>
internal struct ReshuffleRng
{
    private uint _state;

    private ReshuffleRng(uint seed) => _state = seed;

    /// <summary>Build a stream from an ordered list of ints. Order matters; every peer must pass the
    /// same values in the same order (that is the whole co-op contract).</summary>
    public static ReshuffleRng From(params int[] parts)
    {
        // FNV-1a over the parts, then one avalanche round so the initial state is already well mixed.
        uint h = 2166136261u;
        foreach (int p in parts)
        {
            unchecked
            {
                h ^= (uint)p;
                h *= 16777619u;
            }
        }
        return new ReshuffleRng(Mix(h));
    }

    /// <summary>Fold a string into the seed material (relic entries are canonical UPPER_SNAKE ASCII,
    /// identical on every peer — unlike <c>string.GetHashCode</c>, which is randomized per process).</summary>
    public static int Hash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return (int)h;
        }
    }

    /// <summary>splitmix32 step.</summary>
    private static uint Mix(uint z)
    {
        unchecked
        {
            z += 0x9E3779B9u;
            z = (z ^ (z >> 16)) * 0x21F0AAADu;
            z = (z ^ (z >> 15)) * 0x735A2D97u;
            return z ^ (z >> 15);
        }
    }

    public uint NextUInt()
    {
        unchecked
        {
            _state += 0x9E3779B9u;
            uint z = _state;
            z = (z ^ (z >> 16)) * 0x21F0AAADu;
            z = (z ^ (z >> 15)) * 0x735A2D97u;
            return z ^ (z >> 15);
        }
    }

    /// <summary>Uniform in [0, exclusiveMax). Rejection-sampled, so no modulo bias — with pools of
    /// 15–41 relics a biased modulo would visibly favour the alphabetically-first entries.</summary>
    public int Next(int exclusiveMax)
    {
        if (exclusiveMax <= 1) return 0;
        uint bound = (uint)exclusiveMax;
        uint limit = uint.MaxValue - (uint.MaxValue % bound) - 1;
        uint r;
        do { r = NextUInt(); } while (r > limit);
        return (int)(r % bound);
    }
}
