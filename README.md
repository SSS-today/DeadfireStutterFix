# Deadfire Stutter Fix

A small BepInEx plugin that removes the combat stutters in **Pillars of Eternity II: Deadfire**. It doesn't change graphics, gameplay or save files. It fixes four places where the game does a big chunk of work in a single frame, mostly in the middle of a fight. Each cause was found by measuring, fixed, and then measured again.

> **Built with Claude.** The profiling, the root-cause hunting, the fixes and these docs were done by Claude, Anthropic's AI model, working with Claude Code. SSS set the goal and the rules, made every decision and played every test run.
>
> **Tested on one PC.** It's been tested on one PC with the Steam version (game build v5.0.0.0040). Reports from other setups are very welcome, see [Help improve it](#help-improve-it).

[Results](#results) · [Install](#install) · [What it does](#what-it-does) · [Settings](#settings) · [Uninstall](#uninstall) · [Is it safe?](#is-it-safe) · [FAQ](#faq) · [Credits](#credits-and-licence)

**Videos:** stock game vs DXVK + Stutter Fix, same fight: [full-frame version](https://youtu.be/9mcYuJI-kjk) · [split-screen version](https://youtu.be/HP_sYt9FiNM) (YouTube)

## Results

All numbers come from SSS's PC (setup below). A **stutter** here means a frame that took longer than **20 ms** to reach the screen, read from RivaTuner's frametime data. Every number below is a measurement.

| Test | Without the fix | With the fix |
|---|---|---|
| **Ship boarding fight**, same save, same 51 s of play (video) | **22** stutters (stock game, DX11) | **0** stutters (DXVK + this fix) |
| First fight after launching the game: icon-atlas freeze | one **~100 ms** frame, ship and land fights | **gone** (built behind a loading screen instead) |
| Same ship battle, reloaded again and again in one session | **38** stutters in 10 reloads, rising to **18** per fight | **3** stutters in 16 reloads |
| Combat tooltips left running after 11 reloads | **188** (19 more with every reload) | **0** (cleaned at every load) |

<sub>Test PC: Ryzen 7 9800X3D (SMT off) · Radeon RX 9070 XT · 16 GB DDR5-6000 · 3840×2160, VRR, frame rate capped at 60 fps with RTSS · BepInEx 5.4.23.5 · DXVK 3.1.1 · Steam build v5.0.0.0040. All tests ran at a 60 fps cap. The video comparison is stock DX11 versus DXVK + fix. What DXVK does on its own is shown under [Why DXVK is required](#why-dxvk-is-required).</sub>

The full test log is in **[docs/MEASUREMENTS.md](docs/MEASUREMENTS.md)**. For every fix it has what the frame was doing (Harmony timings of the game's own methods, plus one PIX capture), which run showed it, and the before/after numbers.

## Install

1. **BepInEx 5** (the Mono version for Windows x64): download the `win_x64` zip from the [BepInEx 5.4.23.5 release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5). Extract it into the game folder, next to `PillarsOfEternityII.exe`. 
2. **DXVK** (required, see below): download it from the [DXVK releases](https://github.com/doitsujin/dxvk/releases) and copy `d3d11.dll` and `dxgi.dll` from its `x64` folder into the game folder.
3. **Start the game once** and quit at the main menu. BepInEx creates its folders.
4. Download `DeadfireStutterFix.dll` from [Releases](../../releases) and put it in `BepInEx\plugins\`.
5. Play.

**Is it working?** Load a save: the combat log shows **"Deadfire Stutter Fix 1.0.2 active."** once per launch. If that line doesn't appear, the plugin isn't loaded: check that BepInEx is installed and the dll is in `BepInEx\plugins\`. DXVK is active when the game folder contains `PillarsOfEternityII_d3d11.log` after a launch.

### Why DXVK is required

DXVK runs the game's DirectX 11 on Vulkan. DXVK and this fix solve different problems, and you need both. Measured in the same battle, before the fix existed:

| Same battle, no fix | Stock DX11 | DXVK |
|---|---|---|
| Stutters over 20 ms | **42** | **17** |
| Freezes over 150 ms | 0 | **4** |

<sub>MSI Afterburner, 5 recordings per API, one reading per second.</sub>

- **DXVK** removed about 60% of the hitches.
- **The 4 big freezes** left on DXVK (153–159 ms) were the garbage collector, which this fix removes.
- **DXVK + fix** together: **0** stutters in the video fight. The fix on plain DX11 was also tried and fights were still not smooth.

DXVK needs a GPU and driver with Vulkan support. To undo it, delete `d3d11.dll` and `dxgi.dll` from the game folder.

**Updating:** replace the dll. Your settings stay in the config file.

## What it does

The game runs on Unity 5.6 with an old Mono runtime. Every stutter this plugin fixes follows the same pattern: a big piece of work is done in a single frame, at the worst moment. Each fix moves that work behind a loading screen or past the end of combat. The game's own code still does the work. Only the timing changes.

| Fix | What the game does | What the fix does |
|---|---|---|
| **Garbage collector** | Unity 5.6's Mono collector freezes the game for a full collection. With this game's heap, a collection takes **~100–170 ms**, and it can start in the middle of a fight. | Grows the managed heap once at startup (+1 GB, the "large heap" approach from Unity's own manual) so collections are rare. Forces one collection right after every area load (behind the loading screen) and inside every save's own frame. |
| **Corpse colliders** | When an enemy's death animation ends, the game bakes the body's mesh and builds the collider that makes the corpse clickable for looting: **22–44 ms per corpse**, in the middle of the fight. | Corpses can't be looted during combat anyway, so the fix waits until combat is over, then builds them one at a time (one per 10 frames, starting 30 frames after combat ends). |
| **Combat tooltip cleanup** | On every reload, the floating combat tooltips of the old characters (**~19 per reload**) are never given back. They stay active and are updated every frame, so each reload makes fights a bit heavier. | After each area load, gives every tooltip whose character no longer exists back to the game's own tooltip pool, through the game's own `Remove()`. It runs inside the loading screen (15–18 ms there). |
| **Icon atlas prewarm** | The first time in a launch that a character's action icon shows an ability, the game builds a circle-clipped copy of the whole spell icon atlas: one **~70–100 ms** freeze in your first fight. The first item use does the same with the item icon atlas. | Makes that same call once, behind a loading screen. The game caches the result for the whole launch, so the fight never pays for it. |

Collector timing details: the forced collection runs **0.05 s** after the last scene load of an area. An earlier version waited 0.5 s, and on quick transitions inside a map (for example going up a floor in a building) the loading screen was already gone by then, so the collection landed in gameplay.

Saves (autosaves on every area transition, quicksaves, manual saves) already cost one slow frame: the game does its save work on the main thread in a single frame, then zips the files on a background thread. 1.0.0 collected 0.2 s after the file was written, which made a second hitch after each save. Since 1.0.1 the collection runs inside the save frame itself (`CollectInSaveFrame`), so a save gives one hitch instead of two.

## Settings

`BepInEx\config\alex.deadfirestutterfix.cfg` is created on first launch. The defaults are what was tested.

| Section | Key | Default | Meaning |
|---|---|---|---|
| Heap | `PreallocateMB` | 1024 | Heap growth at startup. 0 = off. |
| Heap | `CollectOnLoad` | true | Collect right after an area load. |
| Heap | `CollectDelaySeconds` | 0.05 | Wait after the last scene load before collecting. |
| Heap | `CollectOnSave` | true | Collect after every save. |
| Heap | `CollectInSaveFrame` | true | Collect inside the save's own frame (one hitch per save). false = 0.2 s after the file is written, as in 1.0.0. |
| Corpses | `DeferColliders` | true | Build corpse colliders only after combat. |
| Corpses | `SpacingFrames` | 10 | Frames between two corpse builds. |
| Corpses | `DelayAfterCombatFrames` | 30 | Frames after combat before the first one. |
| Fixes | `CleanCombatTooltips` | true | The reload tooltip cleanup. |
| Fixes | `PrewarmIconAtlases` | true | The first-fight icon atlas fix. |
| Fixes | `PrewarmAtlasNames` | SpellAbilityIcons,ItemIcons | Which atlases to prewarm. |
| General | `ShowActiveMessage` | true | The "active" line in the combat log after the first load. |

## Uninstall

Delete `BepInEx\plugins\DeadfireStutterFix.dll` (and `BepInEx\config\alex.deadfirestutterfix.cfg` if you like). To remove BepInEx entirely, delete the `BepInEx` folder, `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version` from the game folder. Nothing in the game's own files or your saves is changed.

## Is it safe?

- **Changes are in memory only.** Harmony hooks two game methods (`SaveLoadManager.SaveGame` and the corpse-collider coroutine) while the game runs. Nothing on disk is patched.
- **It writes nothing besides BepInEx's config file and log.** It makes no network connections.
- **Saves are untouched.** Nothing the plugin does is stored in a save, and removing it leaves your saves exactly as they would be without it.
- **It fails safe.** If a game method it needs isn't found (for example after a game update), that one fix switches itself off and writes a warning to `LogOutput.log`. The game then runs as stock.
- **Source:** the whole plugin is one readable file, [src/DeadfireStutterFix.cs](src/DeadfireStutterFix.cs).

## FAQ

**Does it change gameplay?** No. Corpses can't be looted during combat in the stock game, and they become lootable shortly after combat ends. Tooltips of living characters are never touched.

**Do I need DXVK?** Yes. DXVK removes most of the small hitches and the fix removes the big freezes; see [Why DXVK is required](#why-dxvk-is-required).

**Why is there a short hitch while the main menu loads?** The icon prewarm runs at the first load where the game's icon manager exists. On the test PC that is the main menu load, which takes ~65 ms once, during loading.

**Does it help the "FPS drops after a few hours" reports?** If you reload a lot, yes: that is exactly the tooltip pile-up. Without reloads, nothing leaked in our tests.

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
- Uses [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX). [DXVK](https://github.com/doitsujin/dxvk).
- MIT licence, see [LICENSE](LICENSE).

Not affiliated with or endorsed by Obsidian Entertainment or Unity Technologies.
