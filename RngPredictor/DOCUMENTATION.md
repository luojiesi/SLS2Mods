# Random Number Predictor (随机数预测) — design notes

A Slay the Spire 2 mod inspired by the STS1 Workshop mod **随机数预测大师 / RandomNumberPredictionMaster**
(workshop item 3423569652, by "FF"; a different author, hence the different name). Hover a card, potion, pile
or event option and the mod shows what its random effect will actually produce *before* you commit: the cards
Discovery will offer, the enemy Sword Boomerang will hit, the card True Grit will exhaust, the top card Havoc
will play, what a card becomes when an event transforms it, which card an event upgrades, the draw-pile order
after the next reshuffle.

## How the STS1 mod worked

The original mod (Java, ModTheSpire/BaseMod) is a set of ~40 `Predictor` classes. Every frame it:

1. reads the game's RNG streams (`AbstractDungeon.cardRandomRng`, `shuffleRng`, `miscRng`, ...), keeps a copy
   and notices when a stream advanced (`compareRandomXS128`);
2. finds the hovered card / potion (`AbstractPlayer.hoveredCard`, potion hitboxes);
3. runs the matching predictor, which re-executes the card's random logic on a *copy* of the RNG
   (e.g. `TransmutationPredictor` calls `returnTrulyRandomColorlessCardInCombat(rngCopy)` five times);
4. renders the resulting cards in a row at the top-left of the screen, and damage/target numbers over
   monsters.

It also had a "prompt screen" (top-panel button) listing, for every card in the deck, what the 1st, 2nd and
3rd transformation would turn it into, and a shuffle-order predictor with a number input.

## How the STS2 version works

STS2 makes this much simpler:

* Every RNG stream is a `MegaCrit.Sts2.Core.Random.Rng` = (seed, counter) over xoshiro256**; every draw
  consumes exactly one 64-bit step. `new Rng(seed, counter)` is therefore an exact clone (the game's own
  save/load uses this). [Sim.cs](Sim.cs) `Sim.Clone`.
* Random effects are funnelled through a handful of helpers: `CardFactory.GetDistinctForCombat` /
  `GetForCombat` (generated cards), `Rng.NextItem` (random choice), `ListExtensions.UnstableShuffle` /
  `StableShuffle` / `TakeRandom`, `PotionFactory.CreateRandomPotion*`, `OrbModel.GetRandomOrb`,
  `AttackCommand.TargetingRandomOpponents`, `CardFactory.CreateRandomCardForTransform`. [Sim.cs](Sim.cs)
  re-implements each one **without side effects** (the real ones create card instances in the combat
  state) but with the identical list construction order and number of draws.
* Streams (`RunRngSet`): `CombatCardGeneration` (Discovery, potions...), `CombatCardSelection` (True Grit,
  Seeker Strike...), `CombatTargets` (random enemies), `CombatOrbs`, `CombatPotionGeneration`,
  `CombatEnergyCosts` (Snecko), `Shuffle` (reshuffles, "play a random card from the draw pile"),
  `Niche` (relic transforms). Events use their own per-event `EventModel.Rng`; relics like Claws use
  `PlayerRngSet.Transformations`.

### Hooks ([RngPredictorMod.cs](RngPredictorMod.cs))

| Patch | Purpose |
|-------|---------|
| `NCardHolder.OnFocus` / `OnUnfocus` **and** the `NHandCardHolder` / `NGridCardHolder` overrides (postfix) | every card holder that gains focus: hand cards in combat, grid cards on the transform screen. The base bodies are tiny, so once an override is hot the JIT inlines the `base.OnFocus()` call and a patch on the base alone is bypassed (seen with the user's mod set, where BaseLib also patches `NGridCardHolder.OnFocus`); patching the overrides too makes the hook reliable. Handlers are idempotent so double calls are harmless. |
| `HoveredModelTracker.OnLocalPotionHovered` / `Unhovered` | potion belt hover |
| `NCombatCardPile.OnFocus` / `OnUnfocus` | draw / discard pile buttons → shuffle-order prediction |
| `NDeckTransformSelectScreen.ShowScreen` (postfix) | remember the transform selection screen and its `CardSelectorPrefs.MaxSelect` |
| `NGame._Input` (prefix) | **F8** toggles the mod |

[PredictionManager.cs](PredictionManager.cs) keeps the current hover source, re-runs the predictor every
250 ms while it stays hovered (cheap: a few hundred pool cards filtered and shuffled), and only re-renders
when the result changes. A `SceneTree.ProcessFrame` subscription drives it; nothing is registered as a
Godot script class.

### Predictors ([Predictors.cs](Predictors.cs))

Keyed by the model's C# type name (v0.107.1):

* **Generated cards** (`CombatCardGeneration`): Discovery, Infernal Blade, Distraction, White Noise, Bundle of
  Joy, Jack of All Trades, Quasar, Manifest Authority, Metamorphosis, Jackpot, Stoke, Splash; potions Attack /
  Skill / Power / Colorless Potion, Cosmic Concoction, Orobic Acid.
* **Random card selection** (`CombatCardSelection`): True Grit (unupgraded), Cinder, Thrash, Anointed, Drain
  Power, Seeker Strike, Hidden Gem. When a card resolves it has already left the hand, so the hovered card is
  excluded from "random card in hand" pools.
* **Random plays from piles** (`Shuffle` + `CombatTargets`): Beat Down, Catastrophe, Uproar — shows the cards
  and, for single-target attacks, the enemy each will hit.
* **Random targets** (`CombatTargets`, [DamageSim.cs](DamageSim.cs)): Sword Boomerang, Ricochet, Rip and Tear, Sweeping
  Gaze (Osty), Stardust (hits = stars), Volley (hits = energy), Flak Cannon (hits = statuses), Bouncing Flask
  (poison, `HittableEnemies`). `DamageSim.RandomAttack` mirrors `AttackCommand.Execute` with
  `TargetingRandomOpponents`: the hit count goes through `Hook.ModifyAttackHitCount` on a throw-away builder, then
  per hit one `CombatTargets.NextItem` over the attacker's living opponents minus the ones earlier hits killed,
  and the damage pipeline of `CreatureCmd.Damage` (`Hook.ModifyDamage` → block → `Hook.ModifyHpLost` →
  `LoseHpInternal`) on a predicted HP / block ledger. Rendered as a "被打 ×N (damage absorbed)" marker over each
  enemy (plus 击杀 when a hit kills) and the hit order with per-hit damage.
* **Orbs / potions**: Chaos (orb names), Alchemize, Entropic Brew (one potion per open slot), Snecko Oil
  (simulates the 7 draws including a reshuffle, then the cost of every card in hand).
* **Defect orb queue** ([OrbSim.cs](OrbSim.cs), `CombatTargets` + `CombatOrbGeneration`): a side-effect-free mirror
  of `OrbCmd.Channel` / `EvokeNext` and `OrbQueue.BeforeTurnEnd` seeded from the player's real orb list and slot
  count. Channelling into full slots evokes the front orb first; a Lightning evoke or end-of-turn passive is one
  `CombatTargets.NextItem` over the hittable opponents (values come from the real orb instances, or detached
  `ToMutable()` copies owned by the player for new orbs, so Focus counts). Lightning hits run through the same
  `DamageSim` ledger as attacks (Unpowered), so an enemy a hit would kill is dropped from later draws, exactly as the
  game's hittable list shrinks.
  Hand cards: Zap, Ball Lightning, Tempest, Voltaic, Rainbow, Dualcast, Multi-Cast, Quadcast, Shatter, Darkness /
  Null / Shadow Shield, Consuming Shadow, Coolheaded / Cold Snap / Chill, Glacier, Ice Lance, Refract, Glasswork /
  Spinner, Fusion / Ignition, Meteor Strike, Chaos (`CombatOrbGeneration` for the random orb). The End Turn button
  (`NEndTurnButton.OnFocus`) shows this turn's passives. Rendered as a "电球 ×N (total)" marker over each enemy
  plus the channel / evoke / passive sequence.
* **Transformations**: on the "choose a card to transform" screen, hovering a deck card shows what it becomes
  as the 1st … Nth transformed card (each transformation is one `NextItem` draw, so the k-th selected card gets
  the k-th result). The stream depends on who opened the screen: events use their own `EventModel.Rng`;
  New Leaf and Astrolabe (relics, also the Neow options) use `RunState.Rng.Niche` — prefixes on their
  `AfterObtained` set a "relic transform context" so the screen is predicted with the right stream even
  though it is opened inside the Neow event room. Screens with a fixed replacement (Claws → Maul) are left to
  the game's own preview.
* **Neow / event relic options** (`NEventOptionButton.OnFocus`): hovering an option whose relic transforms
  deck cards shows the result for the whole deck *before* choosing — New Leaf (every transformable card),
  Astrolabe (every card as the 1st pick, upgraded), Pandora's Box (every basic Strike/Defend, in deck order)
  and Leafy Poultice (first Strike + first Defend, `PlayerRngSet.Transformations`). Neow's option buttons only
  obtain that one relic, so nothing else consumes the stream in between.
* **Neow relics that generate reward cards / potions**: Arcane Scroll, Hefty Tablet (uniform odds, rare filter,
  no upgrade roll), Lead Paperweight, Lava Rock (colorless pool, regular-encounter odds, upgrade rolls) go through
  `Sim.CreateForReward`, a side-effect-free mirror of `CardFactory.CreateForReward`: per card, one Rewards draw for
  the rarity roll (skipped for Uniform; `RollWithBaseOdds` for relics, the pity offset only applies to encounter
  rewards), `NextItem` on the pool minus already-picked cards, then one draw for the upgrade roll unless
  `NoUpgradeRoll`. `Hook.ModifyCardRewardCreationOptions` / `ModifyCardRewardUpgradeOdds` are applied like the
  game does. A shadow patch on `CreateForReward` verifies this on every real card reward too. Phial Holster and
  Cursed Pearl reuse the potion simulation.
* **"Obtain a random relic"** (`Sim.PeekRelicsFromFront`): `RelicFactory.PullNextRelicFromFront` rolls the rarity
  with one Rewards draw (<0.5 common, <0.83 uncommon, else rare) and takes the first allowed relic of that
  rarity's deque in the player's `RelicGrabBag`, which was shuffled once at run start; the mod peeks at
  allowed-only copies of the private deques (via Harmony field access) with the same fall-through
  (common → uncommon → rare → multiplayer fallback → Circlet). Used for This or That, Ranwid the Elder, Unrest
  Site, Luminous Choir, the Trial's merchant and Neow's Large Capsule. An empty deque that the game would refill
  is reported as unpredictable instead of guessed.
* **Event options with random outcomes** (`Predictors.ForEventOption`, keyed by event type + option text key):
  Endless Conveyor "Observe the chef" / "Spicy Snappy" (random upgrade: `ev.Rng.NextItem(deck.Where(IsUpgradable))`
  — with a fully upgraded deck the game upgrades nothing and the overlay says so), "Suspicious Condiment"
  (random potion from the character + shared pools via `PlayerRng.Rewards`; The Legends Were True's "Slowly find an
  exit" draws its potion the same way, after an HP loss that does not touch the stream), "Jelly Liver" and the transform
  options of Aroma of Chaos, Whispering Hollow, Symbiote, Morphic Grove and Trial (whole-deck "pick which →
  becomes what" from the event's `Rng`; multi-card options show the 1st pick). Since 1.5.0 every option whose
  outcome is fixed the moment it is clicked is covered: random relics (Punch-Off nab, Round Tea Party fight,
  Wongo's bargain bin = first common shop-allowed relic without a rarity roll, Trash Heap dive = event Rng over
  its five fixed relics, Doll Room random = event Rng over the three dolls), random cards added to the deck
  (Infested Automaton study / touch the core and Fried Eel via `Sim.CreateForReward` with the event's
  `CardCreationOptions`, Trash Heap grab = event Rng over ten fixed cards), random potions (Potion Courier
  ransack = uncommon only, Wellspring bottle), random upgrades / downgrades (Reflections: 2 downgrades then 4
  upgrades where the downgraded cards are candidates again; Tablet of Truth: one random upgradable card, all of
  them on the 5th decipher; Doors of Light and Dark: StableShuffle then Take N; Wongo's leave: one random
  upgraded card downgraded) and Slippery Bridge's next requested card (same exclusion rules as the event).
  Options that end in a "pick one of N" screen (Brain Leech, Room Full of Cheese, Colorful Philosophers, The
  Future of Potions, Tinker Time) and rewards that come after a fight (Battleworn Dummy, Punch-Off fight) are
  deliberately not predicted. Private lists are read with Harmony `AccessTools` (`_dolls`, `Relics`, `Cards`,
  `_decipherCount`, `RandomCardToLose`, `SkippedRemovals`).
* **Shuffle order**: hovering the draw or discard pile shows the draw pile after the next reshuffle (top
  first) both "if it happened now" (discard + draw pile, which is exactly what `CardPileCmd.Shuffle` shuffles)
  and "after the hand is discarded at end of turn". `StableShuffle` sorts first, so only the *set* matters.

### Overlay ([Overlay.cs](Overlay.cs))

A `CanvasLayer` (layer 95) with a dark panel at the top-left: title, rows of real `NCard` nodes from the
game's node pool (each inside a scaled wrapper `Control`, the same way `NCardHolder` shrinks cards; scaling
the `NCard` itself breaks its internal layout), text lines, and floating labels glued to enemy hitboxes
(`NCreature.Hitbox.GetGlobalTransformWithCanvas()`). Display cards are detached mutable copies of the
canonical card (`CanonicalInstance.ToMutable()`, upgraded to the shown level) — exactly what the game's card
hover tips do — never the live card, because `NCard` subscribes to its model.

## Accuracy and limitations

* A prediction is only exact if nothing consumes the same RNG stream between the hover and the moment the
  effect resolves. Relics/powers that trigger on card play and draw from the same stream first (rare) shift
  the result; the mod recomputes as soon as the stream advances, so the display is right again immediately
  after.
* Kills are predicted from the damage hooks and the current HP / block; effects that fire on damage (thorns,
  on-hit healing, minion death rules) are not simulated, so a sequence that depends on them can differ.
* Shuffle prediction ignores `Hook.ModifyShuffleOrder` (relics that reorder the pile).
* Singleplayer oriented: predictions use the hovered card's owner.

## Verification

* **Shadow verification** ([Verify.cs](Verify.cs)): prefixes on `CardFactory.GetDistinctForCombat`,
  `GetForCombat`, `CardFactory.CreateForReward`, `PotionFactory.CreateRandomPotion`, `CardPileCmd.Shuffle`,
  `CardCmd.Transform` and `LightningOrb.ApplyLightningDamage` (random target, with the `CombatTargets` counter
  before / after) compute the prediction from a clone of the RNG right before the game runs the real thing, and postfixes compare.
  Mismatches are always written to `%APPDATA%\SlayTheSpire2\logs\RngPredictor.log`; matches too while
  `logs\RngPredictor.verify` exists. `GetDistinctForCombat`'s lazy result is materialised once in the postfix
  so the game never enumerates (and creates cards) twice. `CreateForReward` mirrors `Hook.TryModifyCardRewardOptions`
  on detached copies (egg relics upgrade rewards); a result that differs only in upgrade flags is logged as OK.
* **Self-test** ([SelfTest.cs](SelfTest.cs)): create `logs\RngPredictor.selftest` and start the game. It
  starts an unsaved Ironclad run (seed `RNGTEST`, or `logs\RngPredictor.selftest.seed`), enters Aroma of
  Chaos, hovers a deck card on the transform screen (screenshot), transforms it and compares; then jumps into
  `EXOSKELETONS_WEAK` (3 enemies; override with `logs\RngPredictor.selftest.encounter`, `MAP` travels to the
  first monster node instead), adds Havoc / Discovery / Infernal Blade / Sword Boomerang / True Grit / Metamorphosis,
  Sword Boomerang with 3 Strength (one hit lethal), Zap + Dualcast (Lightning evokes, one of them lethal) and an Attack Potion + Snecko Oil, focuses every hand card through the real focus path (screenshots in
  `logs\rngpredictor_selftest\`), then plays each card / potion and checks the outcome against the
  prediction made a moment earlier. Logs `SELFTEST RESULT: PASS|FAIL` and quits. Never touches saved runs.
