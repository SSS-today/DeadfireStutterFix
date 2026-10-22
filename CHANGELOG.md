# Changelog

## 1.0.0 (first public release)

This release contains the fixes from development build v1.14, without the profilers and logging.

**Garbage collector**
- The heap is pre-grown by 1 GB at startup.
- One forced collection runs 0.05 s after each area load, and one 0.2 s after each save.

**Corpse colliders**
- Corpse colliders are built only after combat ends, one every 10 frames, starting 30 frames after combat ends.

**Tooltips**
- The combat tooltips of characters that no longer exist are cleaned up after every load (the reload pile-up).

**Icon atlases**
- The circle-clipped `SpellAbilityIcons` and `ItemIcons` atlases are prewarmed once per launch, behind a loading screen.

**Development build**
- The plugin refuses to load next to the development build (`alex.gcprobe`).

## Development history (GC Probe builds, not released)

**Fixes**

| Build | Change |
|---|---|
| v1.2 | Heap pre-grow and forced GC on load/save. |
| v1.4 | Corpse colliders deferred until after combat. |
| v1.5 | 30-frame delay after combat for the first corpse. |
| v1.7 | Combat tooltip cleanup. |
| v1.12 | Icon atlas prewarm. |
| v1.14 | Forced GC 0.05 s after load (was 0.5 s). |

**Measurement-only builds**

| Build | What it measured or tried |
|---|---|
| v1.3 | Death and loot timing. |
| v1.6 | Object census. |
| v1.8 | Load profiler. |
| v1.9 | Texture hooks. These were dropped: the ocean vanished in 2 tries, cause unresolved. |
| v1.10 | Entry-point profiler. |
| v1.11 | Icon/atlas profiler. |
| v1.13 | Pre-compile experiment. Inconclusive, so not in the release. |
