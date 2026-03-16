# STS2 Mods

A collection of quality-of-life mods for **Slay the Spire 2** (Godot 4.5.1 / C# / .NET 9.0), using HarmonyX 2.4.2 for runtime method patching.

## Mods

### UndoAndRedo

Full combat undo/redo system. Snapshots the entire combat state before each player action and restores it on demand.

| Key | Action |
|-----|--------|
| **Left Arrow** | Undo |
| **Right Arrow** | Redo |

**Features:**
- Captures all combat state: creature HP/block/powers (including internal power data like VoidForm counters), card piles, energy, stars, orbs, pets/summoned creatures, potions, relics, monster RNG & move states, combat history, run RNG
- Bounded undo stack (50 snapshots max)
- Failed-snapshot sentinel (gracefully skips corrupt snapshots instead of crashing)
- Full visual refresh after restore: hand cards, power icons, health bars, orb positions, potion slots, pile counts, card descriptions, monster intents, end-turn button state
- Summoned creature cleanup: removes pet visuals (e.g., King's Sword) when undoing the card that summoned them
- Animation snapping: cancels all in-progress tweens and snaps visuals to their final positions instantly
- Guard conditions prevent undo during enemy turns, mid-animation, or screen transitions
- Logs to `<Godot user data>/logs/UndoAndRedo.log` for debugging

**Limitations:**
- Single-player only
- Cannot undo mid-animation (action queue must be idle)
- Some cosmetic-only event subscriptions may become stale after undo (e.g., power VFX flashes)

See [UndoAndRedo/DOCUMENTATION.md](UndoAndRedo/DOCUMENTATION.md) for full technical documentation.

---

### QuickRestart

Press **F5** to instantly restart the current combat room from the last auto-save, without returning to the main menu.

**Features:**
- Reloads run state from the auto-save file
- Preserves map route drawings
- Preserves enemy positions on screen
- Smooth fade-out/fade-in transition
- Only active during a run (disabled on game over or during screen transitions)

---

### UnifiedSavePath

Forces the game to use the same save directory regardless of whether mods are loaded. STS2 normally separates modded saves into a different profile directory, which means your vanilla save progress is invisible when playing with mods and vice versa.

**How it works:** Patches `UserDataPathProvider.IsRunningModded` to always return `false`, and patches `GetProfileDir` directly as a safety net against JIT inlining.

---

### UpgradeAllCards

Automatically grants all three egg relics (Frozen Egg, Molten Egg, Toxic Egg) at the start of every run, upgrades the starting deck, and removes the eggs from the relic pool so they won't appear again mid-run.

**How it works:**
- Postfix on `Player.PopulateStartingRelics` to add the eggs
- Postfix on `Player.PopulateStartingDeck` to upgrade every card
- Postfix on `RelicGrabBag.Populate` to remove eggs from the reward pool

---

## Build & Deploy

All mods target .NET 9.0 and reference `sts2.dll`, `0Harmony.dll`, and `GodotSharp.dll` from the game directory.

### Build

```bash
# Build a specific mod (e.g., UndoAndRedo)
dotnet build UndoAndRedo/UndoAndRedo.csproj -c Release \
  '-p:STS2GameDir=D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2'
```

Replace the `STS2GameDir` path with your game install location.

### Deploy

Each mod ships **three files** to `<game>/mods/` for compatibility with both stable and beta (0.99+) branches:

| File | Purpose |
|------|---------|
| `ModName.dll` | Compiled mod assembly |
| `ModName.pck` | Godot resource pack (required by stable branch) |
| `ModName.json` | External manifest (required by 0.99+ beta branch) |

The `.json` manifest declares `has_pck` and `has_dll` so the new mod loader knows what to load. The `.pck` is still needed for the stable branch which discovers mods by scanning for `.pck` files.

```bash
# Copy all three files (game must be closed)
cp UndoAndRedo/bin/Release/net9.0/UndoAndRedo.dll \
  "D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/mods/UndoAndRedo.dll"
cp UndoAndRedo/UndoAndRedo.json \
  "D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/mods/UndoAndRedo.json"
cp UndoAndRedo/UndoAndRedo.pck \
  "D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/mods/UndoAndRedo.pck"
```

To create a `.pck` file for code-only mods, use PCK Explorer or `create_pck.py` to bundle `mod_manifest.json` into a minimal resource pack.

### Project Structure

```
STS2Mods/
  UndoAndRedo/          Combat undo/redo (Left/Right Arrow)
    UndoAndRedoMod.cs     Entry point, input handling, visual refresh
    CombatSnapshot.cs     State capture & restore
    UndoAndRedo.json      External manifest (0.99+)
    mod_manifest.json     Internal manifest (inside .pck)
    DOCUMENTATION.md      Full technical documentation
  QuickRestart/         Quick restart (F5)
    QuickRestartMod.cs    Entry point, restart logic
    QuickRestart.json     External manifest (0.99+)
    mod_manifest.json     Internal manifest (inside .pck)
  UnifiedSavePath/      Shared save directory
    UnifiedSavePathMod.cs Harmony patches
    UnifiedSavePath.json  External manifest (0.99+)
    mod_manifest.json     Internal manifest (inside .pck)
  UpgradeAllCards/      Auto-upgrade starting deck
    UpgradeAllCardsMod.cs Harmony patches
    UpgradeAllCards.json  External manifest (0.99+)
    mod_manifest.json     Internal manifest (inside .pck)
```

## Technology

- **Game:** Slay the Spire 2 (Godot 4.5.1 / C# / .NET 9.0)
- **Patching:** HarmonyX 2.4.2 (runtime method patching via `[ModInitializer]`)
- **Mod format:** Dual-compatible with stable (`.pck`-based discovery) and 0.99+ beta (`.json`-based discovery)

## Credits

UndoAndRedo is inspired by [Undo the Spire](https://github.com/filippobaroni/undo-the-spire) for Slay the Spire 1 by **filippobaroni** ([Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3354673683)). That mod used STSStateSaver under the hood. This STS2 version is a ground-up rewrite for the Godot/.NET architecture.
