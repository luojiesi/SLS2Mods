# SpeedUp Mod — Research Notes

## Animation Speed Architecture

STS2 has no single global speed multiplier. Speed is controlled through multiple systems:

### 1. FastModeType Enum (Built-in Speed Setting)
- `None → Normal → Fast → Instant`
- Stored in `SaveManager.Instance.PrefsSave.FastMode`
- Checked throughout the code with hardcoded durations per mode
- `Instant` skips animations entirely

### 2. Cmd.Wait / Cmd.CustomScaledWait — Core Delay System (line 390835–390890)
- `Cmd.Wait(float seconds)` — respects FastModeType
- `Cmd.CustomScaledWait(fastSeconds, standardSeconds)` — different durations for Fast vs Normal
- These are the delays between combat actions (card plays, attacks, damage)

### 3. Engine.TimeScale — Global Time Scale (line 46983–46988)
- Currently only used by HitStop system (slows to 0.1f on hit, eases back to 1.0f)
- Debug function allows 0.1–4.0 range
- Easiest lever: setting to 2.0 speeds up all tweens, timers, animations globally

### 4. Per-Creature Animation Delays
- `AttackAnimDelay`, `CastAnimDelay` — abstract properties per character (line 286377)
- Individual attack commands set delays (e.g., 0.5f for Killer, 0.35f for Axebot)

### 5. Spine Animation Playback (line 395510–395563)
- `MegaAnimationState.SetTimeScale(float)` — controls Spine animation speed per creature
- `MegaTrackEntry.SetTimeScale()` — per-track control

### 6. Tween Durations
- Scattered everywhere for UI transitions, card movements, map animations
- Map transition: Fast=1.5s, Normal=3.0s (line 145998)
- Act banner: Fast=0.5s, Normal=2.0s (line 45378)

### 7. HitStop System (line 88700–88835)
- Short=0.15s, Normal=0.3s, Long=0.6s, Forever=2.0s
- Temporarily sets Engine.TimeScale to 0.1f, eases back to 1.0f

## Recommended Approach

Two-pronged strategy:
1. **Patch `Engine.TimeScale`** to user-configurable multiplier (2x, 3x) — speeds up tweens, Spine, timers globally
2. **Patch `Cmd.Wait` / `Cmd.CustomScaledWait`** to divide delays by same multiplier — these use real-time awaits that may not respect TimeScale

### Key Decompiled Source References (sts2.decompiled.cs)
- Engine.TimeScale debug: line 46983
- FastModeType enum: line 10789
- Cmd utility: line 390835
- AttackCommand: line 392815
- NHitStop: line 88700
- MegaAnimationState: line 395510
- Map anim durations: line 145998
