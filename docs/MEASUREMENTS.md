# Measurements and root causes

This is the test log behind the [README](../README.md) results. It covers how each stutter was found, what the evidence was, and what changed after the fix. Version numbers like v1.7 are builds of the development plugin ([tools/gc-probe](../tools/gc-probe)). Release 1.0.0 contains the fixes from v1.14, without the profilers.

## Setup and method

**PC**
- Ryzen 7 9800X3D (SMT off), Radeon RX 9070 XT, 16 GB DDR5-6000
- 3840×2160 at 144 Hz with VRR, frame rate capped at 60 fps with RTSS

**Software**
- Game: Steam build v5.0.0.0040, Unity 5.6.3p3 (Mono, CLR 2.0)
- BepInEx 5.4.23.5, DXVK 3.1.1

**Rules we held ourselves to**
- **A stutter counts only if it is on the RTSS frametime graph** and SSS felt it while playing. The on-screen threshold is a frame over **20 ms**.
- **Plugin timings are not screen timings.** The probe measures frames inside Unity (`Time.unscaledDeltaTime`), and those read about 10–30 ms higher than what reaches the screen. Probe numbers below are marked *(probe)*.
- **Fresh launch for first-fight tests.** Several of these stutters happen only once per launch, so every first-fight test started from a fresh launch.

**Tools**
- RTSS frametime graph and a small counter that reads RTSS's frametime data. That counter produced the on-screen counts in the video.
- The development plugin's CSV log, which records:
  - frame spikes, GC count and heap size;
  - Harmony timings of chosen game methods;
  - a timing of every Unity entry point in the game's code (`Update`/`OnyxUpdate`/coroutine steps, about 1,988 hooks);
  - an object census after every load.
- One PIX timing capture with CPU sampling and Unity's symbol server.

## 1. Garbage collector (dev v1.2, timing fixed in v1.14)

**Cause.** Unity 5.6 uses an old Mono collector that stops the game for a full collection. The log showed collections of about **100–170 ms** each (forced ones during loads, and a natural one of 133 ms at the main menu). A collection that starts during a fight is a 100+ ms freeze.

**Fix.**
- Pre-grow the heap once at startup by 1 GB, using 1 KB blocks as in the Unity manual's "large heap" example, so collections become rare.
- Force one collection right after each area load and 0.2 s after each save.

**Result.** No GC row in any fight in any logged run since v1.2.

**v1.14 timing change.** The forced collection used to run 0.5 s after the last scene load. On quick same-map transitions (going up a floor in a building) the loading screen was already gone by then, so the collection landed after the player could move again. SSS saw it on every such transition after the GC fix and never before it. It now runs 0.05 s after the scene load. That is about 0.45 s earlier, not more: the scene load event is the earliest safe point.

## 2. Corpse colliders (dev v1.4 and v1.5)

**Cause.** After a death animation, the game runs `GameUtilities.CreateMeshForCollider` on the corpse. It bakes the skinned mesh and cooks a `MeshCollider` so the corpse can be clicked for looting. It took **22–44 ms per corpse** on the main thread, mid-fight. Corpses cannot be looted during combat.

**Fix.**
- **v1.4:** the coroutine the game starts during combat is wrapped. The game's own code runs unchanged once combat has ended, one corpse every 10 frames.
- **v1.5:** waits 30 frames after combat ends. In v1.4 the first corpse landed on the game's own end-of-combat frame.

**Result.**
- Original test fight, stock DX11: **8.4 on-screen stutters per fight** on average.
- With the fixes (v1.4/1.5): **0 in fights 2–10**.
- The late-fight stutters and the combat-end stutter are gone.

## 3. Combat tooltip pile-up on reload (dev v1.7)

**Test.** The same ship battle was reloaded many times in one session, counting on-screen frames over 20 ms per attempt.

| Build | Frames > 20 ms per attempt | Live `UICombatTooltip` | `Transform` |
|---|---|---|---|
| v1.6 (GC + corpses, no cleanup) | 5, 2, 0, 1, 1, 4, 0, 1, 5, 6, **18** (climbing) | 19 → **188** | 1,226 → 4,603 |
| v1.7 (with cleanup) | attempt 1: 5 (once-per-launch); attempts 2–17: **3 in total**, none over 30 ms | flat | flat (~905 GameObjects) |

**Cause.** The object census after every load showed that each reload left about 19 combat tooltips active (one per character in the fight). Their characters were gone, but the tooltips were never returned to `UICombatTooltipManager`'s pool, and they were updated every frame.

**Fix.** After each load, every tooltip whose character no longer exists is handed to the manager's own `Remove()`, then verified by instance ID.

**Result.**
- **16–20 dead tooltips cleaned per reload**, all through the game's own method.
- The pool stays stable at 23.
- The cleanup costs 15–18 ms, inside the loading screen.

## 4. First-fight icon atlas (dev v1.10 to v1.12)

**Symptom.** One ~100 ms frame in the first fight after launching the game, about 11–12 s into a ship battle, every launch. It never happened in later fights.

**What it was not.**
- A load-call profiler (v1.8) timed the game's load, instantiate and sound-bank calls: only 0.63 ms of hooked load calls were in that frame.
- A PIX capture showed the main thread uploading a large texture (`Texture2D::UploadTexture`, called from C#), with the render thread waiting ~90 ms behind it. DXVK's thread waited behind both, so it was a knock-on effect, not the cause.
- Hooks on the texture upload wrappers never fired, most likely because Mono inlines them.

**Finding it.** v1.10 timed every Unity entry point in the game's code.

Run AD (ship): **SPIKE 99.0 ms** *(probe)* at +11.39 s after load:

```
UICharacterActionIcon.OnyxUpdate  71.78 ms
```

v1.11 timed the icon and atlas path inside it. Run AE (ship): **SPIKE 99.2 ms** *(probe)* at +12.06 s:

```
UICharacterActionIcon.UpdateIcon                                          72.0 ms
  SetAbilityIcon (AuraAbility: Zealous_Charge)                            71.9 ms
    UIAtlasManager.GetCircleClippedAtlas (UIAtlas: SpellAbilityIcons)     71.8 ms
      UIAtlas.set_spriteMaterial "SpellAbilityIconsCircleClipped (Derived)"  70.9 ms
```

Run AF, a land fight (slums district), showed the same path through `UICombatTooltipManager`, with a different ability (Vile Thorns) and the same atlas:

```
SPIKE 105.8 ms (probe)
  GetCircleClippedAtlas  65.4 ms
```

**Cause.** The game builds a circle-clipped copy of the whole spell icon atlas the first time an icon shows an ability. It caches the copy per atlas for the rest of the launch, which is why the stutter happens only once.

**Fix (v1.12).** Make the same call (`GetCircleClippedAtlas`) once, behind a loading screen.

**Result.**
- Run AG (ship): the ~100 ms frame was gone. The same bug then showed up for the **item** atlas, at 39.8 ms on the first potion use, so `ItemIcons` was added.
- Run AH (ship, both atlases prewarmed, 64.7 ms at the main menu load): no icon stutter in fight 1 or fight 2.
- Run AL (land): no icon stutter.

## 5. The video comparison

The fight is a ship boarding fight. Both takes used the same save and the same actions, and each was a fresh launch.

| Take | Stutters over 20 ms in 51 s | Whole take |
|---|---|---|
| Stock (DX11, BepInEx off) | **22** | 28 |
| DXVK + fix | **0** | 0 |

**Recording setup.** Both takes were recorded with OBS (Game Capture, AMD HEVC hardware encoder, 4K60). With OBS recording, the fixed take stayed at 0, so the recording itself added no stutters.

## Not fixed (measured, then judged not worth it)

Probe timings, once per launch, first fight only:

| What | Where | Size *(probe)* |
|---|---|---|
| Weather condition change | `WeatherManager.OnyxUpdate` | 30–64 ms (5 of 5 long first fights) |
| First AI decision | `AIController.OnyxUpdate` | 24–47 ms |
| Ghost appearing | `Ghost.OnEnable` | ~18 ms (+ unexplained native time) |
| Unidentified engine-side frame | outside the game's scripts | ~53 ms (land) |
| First menu open | `UIWindowManager.OnyxUpdate` | ~40 ms |

**Pre-compiling tested and dropped.** Pre-compiling the AI and weather code at the main menu was tried in v1.13. The results were inconclusive, so it isn't in the release.
