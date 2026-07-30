# Relic Reshuffle

**English** · [한국어](README.ko.md)

A [Slay the Spire 2](https://store.steampowered.com/app/2868840/) mod that **re-rolls your entire relic collection every time you enter a fight**.

Rarity is preserved: a common becomes a different common, a rare becomes a different rare. Your relic count and your overall power level never move — only *which* relics you are holding. The rolled relics stay with you until the next fight.

## What it changes

| | |
|---|---|
| **When** | On entering any combat room |
| **What** | Every eligible relic, replaced 1:1 with a different relic of the same rarity |
| **How long** | Until the next fight re-rolls them |
| **Pinned** | Starter relics, relics you paid to re-forge or cleanse, and — unless you opt them in — Ancient and event relics |

A **⇄ button in the top bar** (next to map / deck / pause) appears **during a fight** and shows what this fight's reshuffle changed — which relic became which, with both icons and names. The button pulses gold when a new reshuffle lands, so you notice without anything covering the board, and disappears again when the fight ends — on the map or in a shop it would only be offering a list that no longer applies.

An earlier version popped the list up automatically and faded it after a few seconds, anchored under the relic bar. That was dropped for two reasons: a message that disappears is no use if you looked away, and the relic bar *grows* through a run — it eventually wraps — so there is no stable place to anchor to. Opening it on demand has neither problem.

## Why rarity-preserving

Free-for-all randomization hands one player a pair of rares in act 1 and another a pair of commons before the act 3 boss. Neither is a decision — it is the seed deciding the run. Preserving rarity keeps the swing on *identity* rather than *power*, so every fight is a new puzzle at the same difficulty.

It also makes the rules self-describing: your relic count and rarity spread are invariants, so there is nothing to reconcile after a save/load and no hidden "real" loadout to keep track of.

## What it will never do

- **Re-fire one-time rewards.** Relics whose whole payload is dispensed on pickup (Strawberry, Mango, Pandora's Box, …) are never handed out by a re-roll, and relics are swapped through the game's silent inventory path, so `AfterObtained` cannot fire. Your gold, max HP and potion slots are untouched by a reshuffle.
- **Eat your stacks.** If a stackable relic is ever swapped, the incoming relic inherits the count, so progress you built up outside combat is never lost.
- **Delete another mod's relics.** Both sides of the swap are restricted to base-game relics.
- **Hand you a relic you couldn't have earned.** The pool is your own — the shared pool plus your character's pool, filtered by your unlock state, exactly as the game builds it for real relic rewards. Relics that only come from the Ancient One or from events are never handed out, and neither are rarity-less relics such as Circlet.
- **Curate the pool for you.** Every relic of the matching rarity is a candidate. Earlier builds withheld 26 of the 150 pooled-rarity relics — ones with no combat hook, and ones whose effect fires at pickup — and both exclusions were mistakes: a reshuffled relic is *kept until the next fight*, so shop, campfire and map relics work in the meantime. The only exclusions left are Circlet, starter relics, and Ancient / event relics unless you opt them in.
- **Fire a pickup effect it handed you.** Strawberry can now come up in a reshuffle, and it grants nothing when it does — firing those payloads would mint permanent max HP every fight, and several of them open a card-selection screen mid-combat-entry. The relic is inert and its tooltip says so, in the game's own words.
- **Take a relic it cannot give back.** A relic outside your character's relic pool is left alone, whoever added it. Without that rule a relic another mod grants through its own event could be swapped away every fight and never returned — a one-way door that strips you of exactly the content you installed. Relics added by other mods are out of the shuffle entirely, at both ends: they never appear in the random pool, and one you have already earned is never taken. Earn it from the mod that added it and it stays yours.
- **Switch itself off when you restart a fight.** The re-entry guard remembers the inventory it left you holding, not merely "this floor is done". A restart or undo mod that puts your pre-combat relics back is therefore recognised, and the same reshuffle is re-derived — identical result, because it was derived and not shuffled in the first place. Plain re-entry still changes nothing.
- **Give you two of the same relic.** A re-roll only ever picks something you don't already hold, and once you own an entire rarity it rotates those relics between your slots instead — so you keep reshuffling, still without duplicates. Shops, treasures and rewards also skip anything you are currently holding.
- **Shrink your pool as the run goes.** Vanilla drops a relic from the draw pool the moment you obtain it, which is correct when obtaining means keeping — but here relics come and go. A relic the reshuffle takes away goes back into the pool and can be bought or won again, whatever it is. Owning something once never costs you the rest of the run.
- **Pay a pickup effect twice.** Strawberry and friends return to the pool like everything else, so you *can* be offered one again — but any effect that fires when you pick a relic up fires at most once per run. That covers more than the relics the game flags as one-time rewards: Gnarled Hammer (sold in shops) enchants a card on pickup without carrying that flag, and re-buying it used to enchant another. Where the pickup effect is all the relic does, the tooltip carries the game's own `This relic was all used up.` line so the offer is never a silent gold trap.
- **Perturb the run seed.** The re-roll derives from its own RNG stream, so rewards, shops and the map are bit-identical to a vanilla run of the same seed.

## Settings

Two, both **off** by default — the mod starts at its narrowest, most predictable pool. In co-op the **host's** settings apply to everyone, because relic effects are simulated on both machines and the two sides must derive from the same rules.

| Setting | Default | What it does |
|---|---|---|
| Include Ancient relics | Off | Ancient relics can be replaced, and can be handed out. Most of them are rewards from the Ancient One rather than pool relics, so switching this on also opens up the game's event relic pool for them. |
| Include event relics | Off | Relics that only come from events join the pool. Off by default because handing them out at random gives away content the run never earned. |

Everything else is fixed, on purpose: a setting whose other position is simply worse is noise in a settings screen.

- Starter relics are always pinned — your starter *is* your character.
- Relics you paid to re-forge or cleanse (Relic Forge) are always pinned.
- The pool always excludes relics that do nothing in a fight, so a slot is never dead for a whole combat.
- The reshuffle log is always reachable from the top bar — nothing to switch on.

There is also deliberately **no "how much gets re-rolled" slider**. "Some of your relics changed" makes you audit your own inventory every fight to work out which; "all of them changed" is a rule you read once.

## Languages

Relic names in the log come from the game's own localization, so they appear in whatever language you play in — all of them. The panel heading and the two settings ship English, Korean and Chinese, and fall back to English elsewhere (the same policy as the sister mods).

## Multiplayer

Supported. Relic effects are simulated in lockstep on both machines, so both peers must reach the same answer — and they do it **without exchanging any packets**: each peer derives the same reshuffle from values both already agree on (run seed, floor, player NetId, slot, source relic). The host broadcasts its settings so the two sides derive from identical rules.

Verified with a two-instance convergence test: both peers agreed on both players' relic lists before and after the reshuffle, rarity was preserved on every swap, and no state divergence or checksum mismatch was logged.

## Save/load and combat resets

- **Quitting and reloading** returns the same relics. The game never saves mid-combat, so a reload replays the same derivation from the same inputs.
- **Re-entering the same fight** (a combat-reset or undo mod) does **not** re-roll. The guard is keyed on the floor, not the room object, precisely so rewinding a fight does not shuffle your relics again.

## Compatibility

- **[Relic Forge](https://github.com/ing-gom/sts2-relic-forge)** — relics you paid to re-forge or cleanse are pinned, and hidden companion relics are never touched. Note that a relic which merely picked up a prefix on obtain is *not* pinned: Relic Forge attaches one to almost everything, so treating that as "invested" froze whole inventories.
- Relics added by other mods are left alone entirely.

## Building

The project expects `Sts2.ModKit` as a sibling directory (`..\Sts2.ModKit\build\Sts2.ModKit.props`) and a local `Directory.Build.props` supplying `GodotPath`. Build `Release` for a deploy — the self-test files are stripped from Release builds, so a stale test flag can never affect a real run.

## License

MIT — see [LICENSE](LICENSE).
