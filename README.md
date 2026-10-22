# Deadfire Stutter Fix

A small BepInEx plugin that removes the combat stutters in **Pillars of Eternity II: Deadfire**. It doesn't change graphics, gameplay or save files. It fixes four places where the game does a big chunk of work in a single frame, mostly in the middle of a fight. Each cause was found by measuring, fixed, and then measured again.

> **Built with Claude.** The profiling, the root-cause hunting, the fixes and these docs were done by Claude, Anthropic's AI model, working with Claude Code. SSS set the goal and the rules, made every decision and played every test run.
>
> **Tested on one PC.** It's been tested on one PC with the Steam version (game build v5.0.0.0040). Reports from other setups are very welcome, see [Help improve it](#help-improve-it).

[Results](#results) · [Check it on your PC](#check-it-on-your-pc-before--after) · [Install](#install) · [What it does](#what-it-does) · [Settings](#settings) · [Uninstall](#uninstall) · [Is it safe?](#is-it-safe) · [Known limits](#known-limits) · [FAQ](#faq) · [Credits](#credits-and-licence)

<!-- VIDEO: add the YouTube link here after upload -->

## Results

All numbers come from SSS's PC (setup below). A **stutter** here means a frame that took longer than **20 ms** to reach the screen, read from RivaTuner's frametime data. Every number below is a measurement. Anything that wasn't measured is marked as such.

| Test | Without the fix | With the fix |
|---|---|---|
| **Ship boarding fight**, same save, same 51 s of play (video) | **22** stutters (stock game, DX11) | **0** stutters (DXVK + this fix) |
| Original test fight, stutters per fight | **8.4** on average (stock DX11) | **0** in fights 2–10 |
| First fight after launching the game: icon-atlas freeze | one **~100 ms** frame, ship and land fights | **gone** (built behind a loading screen instead) |
| Ship battle reloaded over and over in one session | climbing: 5, 2, 0, 1, 1, 4, 0, 1, 5, 6, **18** stutters per attempt (without the tooltip cleanup) | attempts 2–17: **3 frames over 20 ms in total**, none over 30 ms |
| Leaked combat tooltips after 11 reloads | 19 → **188** live tooltips, all updated every frame | **flat**, cleaned at every load |

<sub>Test PC: Ryzen 7 9800X3D (SMT off) · Radeon RX 9070 XT · 16 GB DDR5-6000 · 3840×2160, VRR, frame rate capped at 60 fps with RTSS · BepInEx 5.4.23.5 · DXVK 3.1.1 · Steam build v5.0.0.0040. All tests ran at the 60 fps cap; there's no uncapped data. The video and "original test fight" comparisons are stock DX11 versus DXVK + fix, so DXVK's own share in them isn't separated out (see [Known limits](#known-limits)).</sub>

The full test log is in **[docs/MEASUREMENTS.md](docs/MEASUREMENTS.md)**. For every fix it has what the frame was doing (Harmony timings of the game's own methods, plus one PIX capture), which run showed it, and the before/after numbers.

## Check it on your PC (before / after)

You don't have to take our numbers on trust. The plugin can be switched off without uninstalling it.

1. **Show frame times.** In RivaTuner Statistics Server (comes with MSI Afterburner), turn on the frametime graph. [CapFrameX](https://www.capframex.com/) or PresentMon also work if you want a log you can count.
2. **Pick one fight and one save.** Save right before a fight. Ship boarding fights are a good stress test.
3. **Fix OFF:** open `doorstop_config.ini` in the game folder and set `enabled = false` (this switches off BepInEx and every plugin). Launch the game, load the save, play the fight and count the frames over 20 ms. Do 3 runs, **restarting the game each time**, because some of these stutters only happen once per launch.
4. **Fix ON:** set `enabled = true` and do the same 3 runs.
5. **Reload test:** with the fix on and off, load the same save 10+ times in one session and fight a little each time. Without the tooltip cleanup, stutters climb as reloads pile up.

To test one fix at a time, switch the others off in the config file (see [Settings](#settings)).

## Install

1. **BepInEx 5** (the Mono version for Windows x64): download the `win_x64` zip from the [BepInEx 5.4.23.5 release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5). Extract it into the game folder, next to `PillarsOfEternityII.exe`. Steam: right-click the game → Manage → Browse local files.
2. **Start the game once** and quit at the main menu. BepInEx creates its folders.
3. Download `DeadfireStutterFix.dll` from [Releases](../../releases) and put it in `BepInEx\plugins\`.
4. Play. `BepInEx\LogOutput.log` should contain a line starting with `Deadfire Stutter Fix 1.0.0`.

**Optional, what the test PC uses: DXVK.** [DXVK](https://github.com/doitsujin/dxvk/releases) runs the game's DirectX 11 on Vulkan. To use it, copy `d3d11.dll` and `dxgi.dll` from the `x64` folder of the DXVK release into the game folder. To undo, delete those two files. DXVK needs a GPU and driver with Vulkan support. The fix works without DXVK, but the DX11 + fix combination **hasn't been measured** (see [Known limits](#known-limits)).

**Updating:** replace the dll. Your settings stay in the config file.

## What it does

The game runs on Unity 5.6 with an old Mono runtime. Every stutter this plugin fixes follows the same pattern: a big piece of work is done in a single frame, at the worst moment. Each fix moves that work behind a loading screen or past the end of combat. The game's own code still does the work. Only the timing changes.

| Fix | What the game does | What the fix does |
|---|---|---|
| **Garbage collector** | Unity 5.6's Mono collector freezes the game for a full collection. With this game's heap, a collection takes **~100–170 ms**, and it can start in the middle of a fight. | Grows the managed heap once at startup (+1 GB, the "large heap" approach from Unity's own manual) so collections are rare. Forces one collection right after every area load (behind the loading screen) and right after every save. |
| **Corpse colliders** | When an enemy's death animation ends, the game bakes the body's mesh and builds the collider that makes the corpse clickable for looting: **22–44 ms per corpse**, in the middle of the fight. | Corpses can't be looted during combat anyway, so the fix waits until combat is over, then builds them one at a time (one per 10 frames, starting 30 frames after combat ends). |
| **Combat tooltip cleanup** | On every reload, the floating combat tooltips of the old characters (**~19 per reload**) are never given back. They stay active and are updated every frame, so each reload makes fights a bit heavier. | After each area load, gives every tooltip whose character no longer exists back to the game's own tooltip pool, through the game's own `Remove()`. It runs inside the loading screen (15–18 ms there). |
| **Icon atlas prewarm** | The first time in a launch that a character's action icon shows an ability, the game builds a circle-clipped copy of the whole spell icon atlas: one **~70–100 ms** freeze in your first fight. The first item use does the same with the item icon atlas. | Makes that same call once, behind a loading screen. The game caches the result for the whole launch, so the fight never pays for it. |

Collector timing details: the forced collection runs **0.05 s** after the last scene load of an area. An earlier version waited 0.5 s, and on quick transitions inside a map (for example going up a floor in a building) the loading screen was already gone by then, so the collection landed in gameplay.

## Settings

`BepInEx\config\alex.deadfirestutterfix.cfg` is created on first launch. The defaults are what was tested.

| Section | Key | Default | Meaning |
|---|---|---|---|
| Heap | `PreallocateMB` | 1024 | Heap growth at startup. 0 = off. |
| Heap | `CollectOnLoad` | true | Collect right after an area load. |
| Heap | `CollectDelaySeconds` | 0.05 | Wait after the last scene load before collecting. |
| Heap | `CollectOnSave` | true | Collect 0.2 s after a save is written. |
| Corpses | `DeferColliders` | true | Build corpse colliders only after combat. |
| Corpses | `SpacingFrames` | 10 | Frames between two corpse builds. |
| Corpses | `DelayAfterCombatFrames` | 30 | Frames after combat before the first one. |
| Fixes | `CleanCombatTooltips` | true | The reload tooltip cleanup. |
| Fixes | `PrewarmIconAtlases` | true | The first-fight icon atlas fix. |
| Fixes | `PrewarmAtlasNames` | SpellAbilityIcons,ItemIcons | Which atlases to prewarm. |

## Uninstall

Delete `BepInEx\plugins\DeadfireStutterFix.dll` (and `BepInEx\config\alex.deadfirestutterfix.cfg` if you like). To remove BepInEx entirely, delete the `BepInEx` folder, `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version` from the game folder. Nothing in the game's own files or your saves is changed.

## Is it safe?

- **Changes are in memory only.** Harmony hooks two game methods (the save writer and the corpse-collider coroutine) while the game runs. Nothing on disk is patched.
- **It writes nothing besides BepInEx's config file and log.** It makes no network connections.
- **Saves are untouched.** Nothing the plugin does is stored in a save, and removing it leaves your saves exactly as they would be without it.
- **It fails safe.** If a game method it needs isn't found (for example after a game update), that one fix switches itself off and writes a warning to `LogOutput.log`. The game then runs as stock.
- **Source:** the whole plugin is one readable file, [src/DeadfireStutterFix.cs](src/DeadfireStutterFix.cs).

## Known limits

Here's what is not fixed and what we don't know:

- **One PC.** Every result is from one PC, Steam build v5.0.0.0040, Windows. The GOG version, the Steam Deck and Linux/Proton are untested.
- **DXVK's share is unmeasured.** The headline comparisons are stock DX11 against DXVK + fix. DX11 + fix wasn't measured, and DXVK alone wasn't measured on the ship fight either. Our best guess is that DXVK lowers CPU overhead per frame. That guess is unmeasured. DX11 and DXVK both compile each shader once, and once DXVK's cache is warm, that cost is gone too.
- **Small first-fight stutters remain**, about once per launch. Here's what was seen in logs (plugin-side timings, which read about 10–30 ms higher than what reaches the screen):
  - a weather change (`WeatherManager`, 30–64 ms);
  - the first AI decision of a fight (24–47 ms);
  - a ~53 ms frame outside the game's scripts (engine/render side, not identified).

  They were judged not worth chasing. In the one test of a second fight in the same launch (after a reload), that fight was clean.
- **First menu open:** one ~40 ms frame the first time a game menu opens, once per launch. Not fixed.
- **Memory:** the game uses about 1 GB more RAM because the heap is pre-grown, and Mono doesn't give that memory back. Keep the Windows page file on.
- **Not fixed and unrelated:** the "pure virtual function call" (R6025) crash some players see in ship fights happened before any mod too.

## FAQ

**Does it change gameplay?** No. Corpses can't be looted during combat in the stock game, and they become lootable shortly after combat ends. Tooltips of living characters are never touched.

**Do I need DXVK?** No, but every "with fix" number above was measured with DXVK.

**Why is there a short hitch while the main menu loads?** The icon prewarm runs at the first load where the game's icon manager exists. On the test PC that is the main menu load, which takes ~65 ms once, during loading.

**Does it help the "FPS drops after a few hours" reports?** If you reload a lot, likely yes, because that is exactly the tooltip pile-up. For long sessions without reloads it's unknown: without reloads, nothing leaked in our tests.

**I had the development build (`GcProbe.dll`) installed.** Delete it. Both contain the same fixes, and BepInEx won't load this plugin while `GcProbe.dll` is present.

## Help improve it

Open an issue with:
- your CPU, GPU, resolution and frame cap;
- DX11 or DXVK;
- what you tested and the counts with the fix on and off;
- `BepInEx\LogOutput.log`.

**Build from source:** install the .NET SDK, install BepInEx into the game, then:

```
cd src
dotnet build -c Release
```

If the game isn't in the default Steam folder, add `-p:GameDir="<your game folder>"`. The dll lands in `src\bin\Release\net35\`.

**The investigation tool:** [tools/gc-probe](tools/gc-probe) is the development build that produced the measurements: the same fixes plus CSV logging and Harmony profilers (frame spikes, GC counts, per-method timings, object census). It's meant for digging, not for playing, because it slows startup with the profilers on. Its log format is described at the top of the file.

## Credits and licence

- Researched and built by **Claude** (Anthropic), directed and tested by **SSS**.
- Inspired by **LTSharpe**'s [Oblivion Remastered Performance Fix](https://github.com/LTSharpe/OblivionRemasteredPerformanceFix), another fix built with Claude Code, which showed the format.
- Uses [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX). Optional [DXVK](https://github.com/doitsujin/dxvk).
- MIT licence, see [LICENSE](LICENSE).

Not affiliated with or endorsed by Obsidian Entertainment or Unity Technologies.
