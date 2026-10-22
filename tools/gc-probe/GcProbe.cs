using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

// GC Probe v1.14 for Pillars of Eternity II: Deadfire (Unity 5.6, Mono).
//
// v1.4 corpse fix: [Corpses] DeferColliders. After an enemy's death animation the game runs the
// coroutine GameUtilities.CreateMeshForCollider(GameObject) on the corpse: it bakes the skinned
// mesh on the CPU and cooks a MeshCollider (what makes the corpse clickable for looting). Measured
// cost: 22-44 ms of main-thread work in one frame per corpse. v1.4 wraps that coroutine when it
// is started during combat: the game's own code still runs unchanged, but only once
// GameState.InCombat is false, and at most one deferred corpse per SpacingFrames frames.
// Calls outside combat (area-load containers, closing a loot window) are not touched.
// If a corpse object goes away while waiting, Unity stops its coroutine as usual.
// v1.5: [Corpses] DelayAfterCombatFrames (default 30) - the first deferred corpse waits this many
// frames after combat ends, so it does not stack on the game's own end-of-combat frame.
// v1.6: [Logging] Census - once per area load (0.6 s after the last scene load, right after the
// forced GC, behind the loading screen) count live cameras, lights, particle systems, audio sources,
// renderers, render textures and DontDestroyOnLoad roots, and write CENSUS rows. Leftovers that
// survive reloads show up as counts that grow from load to load. Also counts every component
// type on active objects (TYPES rows), so leftovers show up by type name.
// v1.7: [Fixes] CleanCombatTooltips - after each area load, combat tooltips whose character no
// longer exists are returned to the game's tooltip pool (UICombatTooltipManager.Remove), so they
// stop piling up with every reload. JANITOR rows log what was cleaned.
// v1.8: [Profiler] Loads - measurement only. Times the game's and Unity's load / instantiate /
// sound-bank / preload calls on the main thread. L rows: one call >= LoadMinMs, with what it loaded
// and whether that name was seen for the first time this launch. LF rows: per-frame total of all
// hooked top-level calls (also the small ones), written when >= FrameSummaryMs or on a spike.
// COMBAT rows mark combat start/end.
// v1.10 (built on v1.8; the v1.9 texture/ocean hooks are gone - with them the Ceto ocean disappeared in 2 of 2 tries):
// [Profiler] Roots - measurement only. Times every Unity entry point in the game's code (Update,
// LateUpdate, FixedUpdate, Awake, Start, OnEnable, the Onyx* versions, and coroutine steps), except
// the Ceto ocean. Unity calls these from native code, so they cannot be inlined away. R rows: one
// call >= RootMinMs on the main thread, with its class and method name.
// v1.11: v1.10 found the ~100 ms first-fight frame inside UICharacterActionIcon.OnyxUpdate (71.8 ms).
// [Profiler] Icons times the action-icon / atlas path (I rows, >= IconMinMs, with atlas/sprite names)
// to find which step is slow and which atlas it is.
// v1.12: [Fixes] PrewarmIconAtlases. v1.11 proved the first-fight ~100 ms frame is the game building the
// circle-clipped copy of the SpellAbilityIcons atlas (UIAtlasManager.GetCircleClippedAtlas ->
// UIAtlas.set_spriteMaterial, 70.9 ms) the first time an action icon shows an ability. The game caches it
// for the whole launch. The fix makes the same call after an area load, behind the loading screen.
// v1.13: [Fixes] Prejit (experiment). Run AH showed 3 more first-fight-only stutters (WeatherManager 44 ms,
// AIController 47 ms, Ghost.OnEnable 18 ms + ~55 ms unexplained). Hypothesis: Mono compiling code the first time
// it runs. Prejit compiles the methods of the listed types ahead of time at the main menu, a few ms per frame
// (MethodHandle.GetFunctionPointer = compile without running). PREJIT row: how many, how long.
// v1.14: [Heap] CollectDelaySeconds (default 0.05, was a fixed 0.5). On quick same-map transitions the loading
// screen can be gone before the 0.5 s timer ran out, so the forced GC landed in gameplay. Now it runs right after
// the scene load.
//
// Logging: writes BepInEx\gc_probe.csv with one line per interesting frame:
//   GC        - the garbage collection count went up on this frame
//   SPIKE     - the previous frame took longer than 20 ms (event text includes f=<frame number>)
//   LOAD      - a scene finished loading
//   tick      - once-per-second heartbeat (shows heap size over time)
//
// Switches in BepInEx\config\alex.gcprobe.cfg:
//   [Heap] PreallocateMB  - at startup, allocate this many MB in small blocks, drop them and
//                           collect, so the managed heap grows and has free room
//                           (the "large heap" strategy from the Unity 5.6 manual).
//   [Heap] CollectOnLoad  - force one collection shortly after scene loads finish.
//   [Heap] CollectOnSave  - force one collection 0.2 s after a save has been written to disk.
//   [Logging] Enabled     - false = no gc_probe.csv at all; the [Heap] features still work.
//   [Logging] TimeDeathCode - (v1.3) time the game's death / loot / corpse-collider methods and
//                           write one T row per call that takes at least MinCallMs.
//                           Only active when [Logging] Enabled = true.
//   [Logging] MinCallMs   - (v1.3) calls shorter than this are not written (default 0.2 ms).
//
// v1.3 timing: a Harmony prefix/postfix pair around each listed method reads the high-resolution
// timer before and after the call. Times are inclusive: HandleDeath contains the methods it calls.
// All listed methods run a few times per death, not every frame, so the cost is negligible.
// T rows: event text is "T <Type>.<Method> f=<frame> ms=<duration>", frame_ms column = duration.
// A SPIKE row with f=N describes the duration of frame N-1, so work logged with f=N-1 caused it.

[BepInPlugin("alex.gcprobe", "GC Probe", "1.14.0")]
public class GcProbe : BaseUnityPlugin
{
    // Set by the save thread, read and cleared by Update() on the main thread.
    private static volatile bool saveFinished;

    private static GcProbe instance;
    private static int mainThreadId;
    private static double tickToMs;
    private static float minCallMs;

    private StreamWriter log;
    private int lastCount;
    private float nextBeat;

    private ConfigEntry<int> preallocMb;
    private ConfigEntry<bool> collectOnLoad;
    private ConfigEntry<bool> collectOnSave;
    private ConfigEntry<float> collectDelay;
    private ConfigEntry<bool> loggingEnabled;
    private ConfigEntry<bool> timeDeathCode;
    private ConfigEntry<float> minCallMsCfg;
    private ConfigEntry<bool> deferColliders;
    private ConfigEntry<int> spacingFramesCfg;
    private ConfigEntry<int> delayAfterCombatCfg;

    // Corpse-collider deferral state (main thread only).
    private static Func<bool> inCombat;
    private static int spacingFrames;
    private static int delayAfterCombatFrames;
    private static int lastInCombatFrame = -1000000;
    private static int nextColliderFrame;
    private static int deferredCount;
    private bool collectPending;
    private float collectAt;
    private ConfigEntry<bool> censusEnabled;
    private ConfigEntry<bool> cleanTooltips;
    private bool janitorPending;
    private ConfigEntry<bool> prewarmIcons;
    private ConfigEntry<string> prewarmAtlasNames;
    private bool prewarmDone;
    private ConfigEntry<bool> prejitEnabled;
    private ConfigEntry<string> prejitTargets;
    private ConfigEntry<float> prejitBudgetMs;
    private List<MethodBase> prejitQueue;
    private int prejitIndex, prejitFailed, prejitFrames;
    private double prejitTotalMs;
    private float prejitStartT;
    private bool prejitDone;
    private float janitorAt;
    private ConfigEntry<bool> profileLoads;
    private ConfigEntry<float> loadMinMsCfg;
    private ConfigEntry<float> frameSummaryMsCfg;
    private static float loadMinMs = 1f;
    private static float frameSummaryMs = 2f;
    private static int loadDepth;
    private static long frameLoadTicks;
    private static int frameLoadCalls;
    private static double frameLoadMaxMs;
    private static string frameLoadMaxName;
    private static readonly HashSet<string> seenLoads = new HashSet<string>();
    private bool lastCombat;
    private ConfigEntry<bool> profileRoots;
    private ConfigEntry<float> rootMinMsCfg;
    private static float rootMinMs = 5f;
    private static int rootDepth;
    private ConfigEntry<bool> profileIcons;
    private ConfigEntry<float> iconMinMsCfg;
    private static float iconMinMs = 1f;

    private static readonly string[] IconTargets =
    {
        "Game.UI.UICharacterActionIcon:UpdateIcon",
        "Game.UI.UICharacterActionIcon:SetAbilityIcon",
        "Game.UI.UICharacterActionIcon:SetActionIcon",
        "Game.UI.UICharacterActionIcon:UpdateVisibility",
        "Game.UI.UIAtlasManager:GetCircleClippedAtlas",
        "Game.UI.UIAtlasManager:GetDesaturatedAtlas",
        "Game.UI.UIAtlasManager:CreateDerivedAtlas",
        "Game.UI.UIAtlasManager:GetKeyForDynamicSprite",
        "Game.UI.UIAtlasManager:GetBaseAtlasForSprite",
        "Game.UI.UIAtlasManager:TryLoadDynamicAtlas",
        "Game.SpriteKey:CopyDataToSprite",
        "Game.InGameHUD:SetWidgetIcon",
        "Game.UI.UITooltipContentTrigger:SetTooltipContent",
        "UISprite:set_atlas",
        "UISprite:set_spriteName",
        "UIAtlas:set_spriteMaterial",
        "Game.ResourceManager:LoadShader",
    };
    private bool censusPending;
    private float censusAt;
    private int censusNumber;

    // "Type:Method" for plain methods; "Type+Nested:Method" for compiler-generated coroutines.
    private static readonly string[] DeathTargets =
    {
        "Game.Health:HandleDeath",
        "Game.Health:LogKill",
        "Game.Health:PlayDeathEffects",
        "Game.Health:ApplyGrief",
        "Game.Health:SpawnGibs",
        "Game.BestiaryManager:RecordKill",
        "Game.PartyManager:AssignXPToParty",
        "Game.AnalyticsManager:KillCreature",
        "Game.AnalyticsManager:CharacterDeath",
        "Game.TeamManager:BroadcastTeamDeathEvent",
        "Game.CharacterStats:NotifyKill",
        "Game.ChatterManager:StartSoundSetChatter",
        "Game.Loot:OnDeath",
        "Game.Loot:DropAllItems",
        "Game.Loot:TryPopulateInventory",
        "Game.Loot:CreateLootOnBody",
        "Game.Loot:CreateLootBag",
        "Game.Loot:DeathAnimationComplete",
        "Game.Loot:RefreshMeshCollider",
        "Game.Loot:BeginFade",
        "Game.GameUtilities+<CreateMeshForCollider>c__Iterator2:MoveNext",
        "Game.CharacterObjectManager:DestroyCharacter",
    };

    private void Awake()
    {
        instance = this;
        mainThreadId = Thread.CurrentThread.ManagedThreadId;
        tickToMs = 1000.0 / Stopwatch.Frequency;

        preallocMb = Config.Bind("Heap", "PreallocateMB", 0,
            "MB to allocate and release at startup so the managed heap grows. 0 = off.");
        collectDelay = Config.Bind("Heap", "CollectDelaySeconds", 0.05f,
            "Seconds after a scene load before the forced GC (CollectOnLoad). Each further scene load restarts the wait. " +
            "Was a fixed 0.5 s before v1.14, which on quick transitions could land after the loading screen.");
        collectOnLoad = Config.Bind("Heap", "CollectOnLoad", false,
            "Force one garbage collection shortly after the last scene load (see CollectDelaySeconds).");
        collectOnSave = Config.Bind("Heap", "CollectOnSave", false,
            "Force one garbage collection 0.2 s after a save game has been written to disk.");
        loggingEnabled = Config.Bind("Logging", "Enabled", true,
            "Write BepInEx\\gc_probe.csv. false = no CSV file; the [Heap] options still work.");
        timeDeathCode = Config.Bind("Logging", "TimeDeathCode", true,
            "Time the game's death, loot and corpse-collider methods (T rows). Needs Enabled = true.");
        minCallMsCfg = Config.Bind("Logging", "MinCallMs", 0.2f,
            "T rows are only written for calls that take at least this many milliseconds.");
        minCallMs = minCallMsCfg.Value;
        deferColliders = Config.Bind("Corpses", "DeferColliders", true,
            "Build corpse click-colliders only after combat ends, one corpse at a time.");
        spacingFramesCfg = Config.Bind("Corpses", "SpacingFrames", 10,
            "Minimum number of frames between two corpse collider builds.");
        spacingFrames = Math.Max(1, spacingFramesCfg.Value);
        delayAfterCombatCfg = Config.Bind("Corpses", "DelayAfterCombatFrames", 30,
            "Frames to wait after combat ends before building the first deferred corpse collider.");
        delayAfterCombatFrames = Math.Max(0, delayAfterCombatCfg.Value);
        cleanTooltips = Config.Bind("Fixes", "CleanCombatTooltips", true,
            "After each area load, return combat tooltips whose character no longer exists to the " +
            "game's tooltip pool (they otherwise stay active and pile up with every reload).");
        profileLoads = Config.Bind("Profiler", "Loads", true,
            "Measurement only: time load / instantiate / sound-bank / preload calls (L, LF and COMBAT rows). Needs Logging Enabled = true.");
        loadMinMsCfg = Config.Bind("Profiler", "LoadMinMs", 1f,
            "An L row is written for every single hooked call that takes at least this many milliseconds.");
        frameSummaryMsCfg = Config.Bind("Profiler", "FrameSummaryMs", 2f,
            "An LF row (all hooked calls in one frame, small ones included) is written when they add up to at least this many ms, or when the frame is a spike.");
        profileRoots = Config.Bind("Profiler", "Roots", true,
            "Measurement only: time every Update/LateUpdate/FixedUpdate/Awake/Start/OnEnable (and Onyx versions) and coroutine step in the game's code, except the Ceto ocean (R rows). Slower startup. Needs Logging Enabled = true.");
        rootMinMsCfg = Config.Bind("Profiler", "RootMinMs", 5f,
            "An R row is written for every entry-point call that takes at least this many milliseconds.");
        profileIcons = Config.Bind("Profiler", "Icons", true,
            "Measurement only: time the character action-icon and UI atlas methods (I rows, with atlas/sprite names). Needs Logging Enabled = true.");
        iconMinMsCfg = Config.Bind("Profiler", "IconMinMs", 1f,
            "An I row is written for every icon/atlas call that takes at least this many milliseconds.");
        prewarmIcons = Config.Bind("Fixes", "PrewarmIconAtlases", true,
            "After an area load (behind the loading screen) build the circle-clipped icon atlases the game would otherwise " +
            "build in the first fight (~70-100 ms stutter once per launch). Once per launch.");
        prewarmAtlasNames = Config.Bind("Fixes", "PrewarmAtlasNames", "SpellAbilityIcons",
            "Comma-separated UIAtlas names to pre-build circle-clipped copies of.");
        prejitEnabled = Config.Bind("Fixes", "Prejit", true,
            "Experiment: compile the methods of the PrejitTargets types ahead of time at the main menu, so their first use " +
            "in a fight does not pay Mono's compile cost. Spread over frames (PrejitBudgetMs per frame).");
        prejitTargets = Config.Bind("Fixes", "PrejitTargets",
            "Game.AI.*,Game.AIController,Game.WeatherManager,Game.WeatherTemperatureNPCEffect,Game.Ghost",
            "Comma-separated full type names; an entry ending in .* means every type in that namespace (and below). " +
            "Nested types (e.g. coroutines) of matched types are included.");
        prejitBudgetMs = Config.Bind("Fixes", "PrejitBudgetMs", 6f,
            "Milliseconds of compiling per frame while the prejit runs.");
        censusEnabled = Config.Bind("Logging", "Census", true,
            "Once per area load (behind the loading screen) count cameras, lights, particles, audio, " +
            "renderers, render textures and DontDestroyOnLoad objects. Needs Enabled = true.");

        string where = "CSV logging off";
        if (loggingEnabled.Value)
        {
            string path = Path.Combine(Paths.BepInExRootPath, "gc_probe.csv");
            log = new StreamWriter(path, false);
            log.WriteLine("time_s,frame_ms,gc_count,heap_mb,event");
            where = "writing to " + path;
        }
        lastCount = GC.CollectionCount(0);
        SceneManager.sceneLoaded += OnSceneLoaded;

        Harmony harmony = new Harmony("alex.gcprobe");
        PatchSave(harmony);
        int timed = 0;
        if (loggingEnabled.Value && timeDeathCode.Value) timed = PatchDeathCode(harmony);
        string corpses = deferColliders.Value ? PatchCorpseColliders(harmony) : "off";
        int loadHooks = 0;
        if (loggingEnabled.Value && profileLoads.Value)
        {
            loadMinMs = loadMinMsCfg.Value;
            frameSummaryMs = frameSummaryMsCfg.Value;
            loadHooks = PatchLoads(harmony);
        }
        int iconHooks = 0;
        if (loggingEnabled.Value && profileIcons.Value)
        {
            iconMinMs = iconMinMsCfg.Value;
            iconHooks = PatchIcons(harmony);
        }
        int rootHooks = 0, rootFailed = 0;
        float rootPatchSec = 0f;
        if (loggingEnabled.Value && profileRoots.Value)
        {
            rootMinMs = rootMinMsCfg.Value;
            float rp0 = Time.realtimeSinceStartup;
            rootHooks = PatchRoots(harmony, out rootFailed);
            rootPatchSec = Time.realtimeSinceStartup - rp0;
        }

        Logger.LogInfo("GC Probe 1.14 running, " + where +
                       " | PreallocateMB=" + preallocMb.Value + " CollectOnLoad=" + collectOnLoad.Value +
                       " CollectOnSave=" + collectOnSave.Value + " CollectDelaySeconds=" + collectDelay.Value + " Logging=" + loggingEnabled.Value +
                       " TimedMethods=" + timed + " MinCallMs=" + minCallMs +
                       " DeferColliders=" + corpses + " SpacingFrames=" + spacingFrames + " DelayAfterCombatFrames=" + delayAfterCombatFrames + " Census=" + censusEnabled.Value + " CleanCombatTooltips=" + cleanTooltips.Value +
                       " LoadHooks=" + loadHooks + " LoadMinMs=" + loadMinMs + " FrameSummaryMs=" + frameSummaryMs +
                       " RootHooks=" + rootHooks + " RootFailed=" + rootFailed + " RootMinMs=" + rootMinMs +
                       " RootPatchSeconds=" + rootPatchSec.ToString("F1") + " IconHooks=" + iconHooks + " IconMinMs=" + iconMinMs +
                       " PrewarmIconAtlases=" + prewarmIcons.Value + " PrewarmAtlasNames=" + prewarmAtlasNames.Value +
                       " Prejit=" + prejitEnabled.Value + " PrejitTargets=" + prejitTargets.Value + " PrejitBudgetMs=" + prejitBudgetMs.Value);
    }

    private void PatchSave(Harmony harmony)
    {
        // Only needed for SAVE rows or CollectOnSave.
        if (!loggingEnabled.Value && !collectOnSave.Value) return;

        MethodInfo target = AccessTools.Method("Game.SaveLoadManager:ZipAndSaveFileToDiskThread");
        if (target == null)
        {
            Logger.LogWarning("Save method not found; SAVE rows and CollectOnSave will not work.");
            return;
        }
        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(GcProbe), "OnSaveWritten"));
        }
        catch (Exception e)
        {
            Logger.LogError("Could not hook the save method: " + e);
        }
    }

    private int PatchDeathCode(Harmony harmony)
    {
        HarmonyMethod pre = new HarmonyMethod(typeof(GcProbe), "TimePrefix");
        HarmonyMethod post = new HarmonyMethod(typeof(GcProbe), "TimePostfix");
        int count = 0;
        foreach (string spec in DeathTargets)
        {
            int colon = spec.LastIndexOf(':');
            string typePart = spec.Substring(0, colon);
            string methodName = spec.Substring(colon + 1);
            string nested = null;
            int plus = typePart.IndexOf('+');
            if (plus >= 0)
            {
                nested = typePart.Substring(plus + 1);
                typePart = typePart.Substring(0, plus);
            }

            Type type = AccessTools.TypeByName(typePart);
            if (type != null && nested != null)
                type = type.GetNestedType(nested, BindingFlags.Public | BindingFlags.NonPublic);
            if (type == null)
            {
                Logger.LogWarning("Timing: type not found for " + spec);
                continue;
            }

            int found = 0;
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
            {
                if (m.Name != methodName || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                found++;
                try
                {
                    harmony.Patch(m, prefix: pre, postfix: post);
                    count++;
                }
                catch (Exception e)
                {
                    Logger.LogWarning("Timing: could not hook " + spec + ": " + e.Message);
                }
            }
            if (found == 0) Logger.LogWarning("Timing: method not found for " + spec);
        }
        return count;
    }

    private string PatchCorpseColliders(Harmony harmony)
    {
        MethodInfo getter = AccessTools.PropertyGetter(AccessTools.TypeByName("Game.GameState"), "InCombat");
        MethodInfo target = AccessTools.Method("Game.GameUtilities:CreateMeshForCollider");
        if (getter == null || !getter.IsStatic || getter.ReturnType != typeof(bool))
        {
            Logger.LogWarning("Corpses: GameState.InCombat not found; corpse fix disabled.");
            return "off (InCombat not found)";
        }
        if (target == null || target.ReturnType != typeof(IEnumerator))
        {
            Logger.LogWarning("Corpses: CreateMeshForCollider not found; corpse fix disabled.");
            return "off (method not found)";
        }
        try
        {
            inCombat = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(GcProbe), "DeferPostfix"));
            return "on";
        }
        catch (Exception e)
        {
            Logger.LogError("Corpses: could not hook CreateMeshForCollider: " + e);
            return "off (hook failed)";
        }
    }

    // Harmony postfix on GameUtilities.CreateMeshForCollider: the game gets our wrapper instead of
    // its own coroutine object; the wrapper runs the original unchanged once it is allowed to.
    // Only calls made during combat are wrapped (corpses). Other callers - AutoLootContainer at
    // area load and Container.Close after looting - run exactly as before.
    private static void DeferPostfix(ref IEnumerator __result)
    {
        if (__result != null && InCombatSafe())
        {
            lastInCombatFrame = Time.frameCount;
            __result = DeferredCollider(__result);
        }
    }

    private static IEnumerator DeferredCollider(IEnumerator original)
    {
        bool waited = false;
        // Wait while in combat, then DelayAfterCombatFrames more frames (so the first corpse does
        // not land on the game's own end-of-combat frame), then keep SpacingFrames between corpses.
        // If a new fight starts during the delay, wait for that one to end too.
        while (true)
        {
            int now = Time.frameCount;
            if (InCombatSafe())
            {
                lastInCombatFrame = now;
                waited = true;
                yield return null;
                continue;
            }
            if (now < lastInCombatFrame + delayAfterCombatFrames || now < nextColliderFrame)
            {
                waited = true;
                yield return null;
                continue;
            }
            break;
        }
        nextColliderFrame = Time.frameCount + spacingFrames;
        if (waited)
        {
            deferredCount++;
            GcProbe p = instance;
            if (p != null && p.log != null)
                p.Write(Time.realtimeSinceStartup, 0f, GC.CollectionCount(0),
                        "CORPSE deferred #" + deferredCount + " f=" + Time.frameCount);
        }
        while (original.MoveNext()) yield return original.Current;
    }

    private static bool InCombatSafe()
    {
        try { return inCombat != null && inCombat(); }
        catch { return false; }
    }

    // Harmony postfix, runs on the game's save thread: no Unity calls here.
    private static void OnSaveWritten()
    {
        saveFinished = true;
    }

    // ---- v1.10 entry-point profiler -------------------------------------------------------------

    private static readonly HashSet<string> RootNames = new HashSet<string>
    {
        "Update", "LateUpdate", "FixedUpdate", "Awake", "Start", "OnEnable",
        "OnyxUpdate", "OnyxLateUpdate", "OnyxFixedUpdate", "OnyxAwake", "OnyxStart", "OnyxEnable",
    };

    private int PatchRoots(Harmony harmony, out int failed)
    {
        failed = 0;
        HarmonyMethod pre = new HarmonyMethod(typeof(GcProbe), "RootPrefix");
        HarmonyMethod post = new HarmonyMethod(typeof(GcProbe), "RootPostfix");
        Type mono = typeof(MonoBehaviour);
        Type onyxBase = AccessTools.TypeByName("Onyx.OnyxBehavior");
        Assembly game = onyxBase != null ? onyxBase.Assembly : null;
        if (game == null) { Logger.LogWarning("Roots: game assembly not found."); return 0; }

        Type[] types;
        try { types = game.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }

        List<MethodBase> targets = new List<MethodBase>();
        foreach (Type t in types)
        {
            if (t == null || t.ContainsGenericParameters || t == onyxBase) continue;
            string ns = t.Namespace ?? "";
            Type outer = t;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            string outerNs = outer.Namespace ?? "";
            if (ns.StartsWith("Ceto") || outerNs.StartsWith("Ceto")) continue;   // ocean vanished with Ceto hooked in v1.9; Ceto cannot be the culprit (stutter also on land)

            bool isBehaviour = mono.IsAssignableFrom(t);
            bool isCoroutine = t.Name.StartsWith("<") && typeof(IEnumerator).IsAssignableFrom(t);
            if (!isBehaviour && !isCoroutine) continue;

            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
            {
                if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters) continue;
                if (m.GetParameters().Length != 0) continue;
                bool want = isCoroutine ? m.Name == "MoveNext" : RootNames.Contains(m.Name);
                if (!want) continue;
                if (m.GetMethodBody() == null) continue;
                targets.Add(m);
            }
        }

        int count = 0;
        foreach (MethodBase m in targets)
        {
            try { harmony.Patch(m, prefix: pre, postfix: post); count++; }
            catch (Exception) { failed++; }
        }
        if (failed > 0) Logger.LogWarning("Roots: " + failed + " entry points could not be hooked (skipped).");
        return count;
    }

    private int PatchIcons(Harmony harmony)
    {
        HarmonyMethod pre = new HarmonyMethod(typeof(GcProbe), "IconPrefix");
        HarmonyMethod post = new HarmonyMethod(typeof(GcProbe), "IconPostfix");
        HarmonyMethod postRes = new HarmonyMethod(typeof(GcProbe), "IconPostfixResult");
        // Struct methods (SpriteKey.CopyDataToSprite): HarmonyX would pass the unboxed struct as "object __instance"
        // (invalid IL), so they get a postfix without __instance. A struct is never a UnityEngine.Object anyway.
        HarmonyMethod postStruct = new HarmonyMethod(typeof(GcProbe), "IconPostfixNoInstance");
        int count = 0;
        foreach (string spec in IconTargets)
        {
            int colon = spec.LastIndexOf(':');
            Type type = AccessTools.TypeByName(spec.Substring(0, colon));
            if (type == null) { Logger.LogWarning("Icons: type not found for " + spec); continue; }
            List<MethodInfo> ms = new List<MethodInfo>();
            AddMethods(ms, type, spec.Substring(colon + 1), spec);
            foreach (MethodInfo m in ms)
            {
                bool refResult = m.ReturnType != typeof(void) && !m.ReturnType.IsValueType;
                HarmonyMethod use = m.DeclaringType.IsValueType ? postStruct : (refResult ? postRes : post);
                try { harmony.Patch(m, prefix: pre, postfix: use); count++; }
                catch (Exception e) { Logger.LogWarning("Icons: could not hook " + spec + ": " + e.Message); }
            }
        }
        return count;
    }

    private static int iconDepth;

    private static void IconPrefix(out long __state)
    {
        if (Thread.CurrentThread.ManagedThreadId != mainThreadId) { __state = -1; return; }
        iconDepth++;
        __state = Stopwatch.GetTimestamp();
    }

    private static void IconPostfixNoInstance(long __state, MethodBase __originalMethod, object[] __args)
    {
        IconPostfixResult(__state, __originalMethod, __args, null, null);
    }

    private static void IconPostfix(long __state, MethodBase __originalMethod, object[] __args, object __instance)
    {
        IconPostfixResult(__state, __originalMethod, __args, __instance, null);
    }

    private static void IconPostfixResult(long __state, MethodBase __originalMethod, object[] __args, object __instance, object __result)
    {
        if (__state < 0) return;
        long ticks = Stopwatch.GetTimestamp() - __state;
        if (iconDepth > 0) iconDepth--;
        double ms = ticks * tickToMs;
        if (ms < iconMinMs) return;
        GcProbe p = instance;
        if (p == null || p.log == null) return;
        string what = Describe(__args, __result);
        string self = "";
        try
        {
            UnityEngine.Object o = __instance as UnityEngine.Object;
            if ((object)o != null && o != null) self = " on=" + CleanName(o.name);
        }
        catch (Exception) { }
        p.Write(Time.realtimeSinceStartup, (float)ms, GC.CollectionCount(0),
                "I " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " d=" + iconDepth +
                " f=" + Time.frameCount + " ms=" + ms.ToString("F2") + self + " what=" + what);
    }

    private static void RootPrefix(out long __state)
    {
        if (Thread.CurrentThread.ManagedThreadId != mainThreadId) { __state = -1; return; }
        rootDepth++;
        __state = Stopwatch.GetTimestamp();
    }

    private static void RootPostfix(long __state, MethodBase __originalMethod)
    {
        if (__state < 0) return;
        long ticks = Stopwatch.GetTimestamp() - __state;
        if (rootDepth > 0) rootDepth--;
        double ms = ticks * tickToMs;
        if (ms < rootMinMs) return;
        GcProbe p = instance;
        if (p == null || p.log == null) return;
        Type t = __originalMethod.DeclaringType;
        string owner = t.DeclaringType != null ? t.DeclaringType.Name + "+" + t.Name : t.Name;
        p.Write(Time.realtimeSinceStartup, (float)ms, GC.CollectionCount(0),
                "R " + CleanName(owner) + "." + __originalMethod.Name + " d=" + rootDepth +
                " f=" + Time.frameCount + " ms=" + ms.ToString("F2"));
    }

    // ---- v1.8 load profiler -------------------------------------------------------------------

    // "Type:Method" (all non-generic overloads). Game types by name; Unity types are added in PatchLoads.
    private static readonly string[] LoadTargets =
    {
        "Game.ResourceManager:LoadAsset",
        "Game.ResourceManager:LoadAssetFromAsyncRequest",
        "Game.ResourceManager:InstantiateObject",
        "Game.ResourceManager:LoadTexture",
        "Game.ResourceManager:LoadTextureFromPath",
        "Game.ResourceManager:LoadTextureFromPathRaw",
        "Game.ResourceManager:LoadShader",
        "Game.ResourceManager:LoadUnityShader",
        "Game.ResourceManager:GetEffectsPrefab",
        "Game.ResourceManager:GetProjectilePrefab",
        "Game.ResourceManager:GetTrapPrefab",
        "Game.ResourceManager:LoadGameDataBundle",
        "Game.ResourceManager:LoadPartialGameDataBundle",
        "AssetBundleManager:LoadAsset",
        "AssetBundleManager:LoadAllAssets",
        "AssetBundleManager:OpenAssetBundle",
        "AkBankManager:LoadBank",
        "AkSoundEngine:LoadBank",
        "AnimationAudioPlayer:LoadBank",
        "Game.GameData.AudioEventListGameData:LoadBank",
        "AbilityAudioManager:LoadData",
        "VisualEffectAudioManager:LoadData",
        "ConversationAudioManager:LoadData",
        "WeaponAudioManager:PreloadWeaponSet",
        "WeaponAudioManager:PreloadData",
        "Game.ChatterManager:StartChatter",
        "ChatterDataManager:LoadChatterBundle",
        "ChatterDataManager:GetChatterData",
        "ChatterBundle:LoadJson",
        "Game.AttackManager:InstantiateAttack",
        "Game.AbilityManager:InstantiateAbility",
        "Game.VFXSpawner:SpawnVFX",
        "Onyx.GameObjectPool:FillPool",
        "Onyx.GameObjectPool:AllocateObject",
        "Onyx.MultiTypeObjectPool:CreatePool",
        "Onyx.MultiTypeObjectPool:FillPools",
    };

    private int PatchLoads(Harmony harmony)
    {
        HarmonyMethod pre = new HarmonyMethod(typeof(GcProbe), "LoadPrefix");
        HarmonyMethod post = new HarmonyMethod(typeof(GcProbe), "LoadPostfix");
        HarmonyMethod postNoResult = new HarmonyMethod(typeof(GcProbe), "LoadPostfixNoResult");
        List<MethodInfo> targets = new List<MethodInfo>();
        foreach (string spec in LoadTargets)
        {
            int colon = spec.LastIndexOf(':');
            Type type = AccessTools.TypeByName(spec.Substring(0, colon));
            if (type == null) { Logger.LogWarning("Profiler: type not found for " + spec); continue; }
            AddMethods(targets, type, spec.Substring(colon + 1), spec);
        }
        AddMethods(targets, typeof(Resources), "Load", "UnityEngine.Resources:Load");
        AddMethods(targets, typeof(UnityEngine.Object), "Instantiate", "UnityEngine.Object:Instantiate");
        AddMethods(targets, typeof(AssetBundle), "LoadAsset", "UnityEngine.AssetBundle:LoadAsset");
        AddMethods(targets, typeof(AssetBundle), "LoadFromFile", "UnityEngine.AssetBundle:LoadFromFile");
        AddMethods(targets, typeof(Shader), "Find", "UnityEngine.Shader:Find");

        int count = 0;
        foreach (MethodInfo m in targets)
        {
            // __result is only requested for reference-type results; void and value-type results use the other postfix.
            bool refResult = m.ReturnType != typeof(void) && !m.ReturnType.IsValueType;
            try { harmony.Patch(m, prefix: pre, postfix: refResult ? post : postNoResult); count++; }
            catch (Exception e) { Logger.LogWarning("Profiler: could not hook " + m.DeclaringType.Name + "." + m.Name + ": " + e.Message); }
        }
        return count;
    }

    private void AddMethods(List<MethodInfo> targets, Type type, string name, string spec)
    {
        int found = 0;
        foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
        {
            if (m.Name != name || m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters) continue;
            if (m.GetMethodBody() == null) continue;   // extern / icall: Harmony cannot patch it
            targets.Add(m);
            found++;
        }
        if (found == 0) Logger.LogWarning("Profiler: no patchable method for " + spec);
    }

    private static void LoadPrefix(out long __state)
    {
        if (Thread.CurrentThread.ManagedThreadId != mainThreadId) { __state = -1; return; }
        loadDepth++;
        __state = Stopwatch.GetTimestamp();
    }

    private static void LoadPostfixNoResult(long __state, MethodBase __originalMethod, object[] __args)
    {
        LoadPostfix(__state, __originalMethod, __args, null);
    }

    private static void LoadPostfix(long __state, MethodBase __originalMethod, object[] __args, object __result)
    {
        if (__state < 0) return;
        long ticks = Stopwatch.GetTimestamp() - __state;
        if (loadDepth > 0) loadDepth--;
        int depth = loadDepth;               // 0 = top-level call
        if (depth == 0)
        {
            frameLoadTicks += ticks;
            frameLoadCalls++;
        }
        double ms = ticks * tickToMs;
        if (ms < loadMinMs) return;
        GcProbe p = instance;
        if (p == null || p.log == null) return;

        string what = Describe(__args, __result);
        string method = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
        bool first = seenLoads.Add(method + "|" + what);
        if (depth == 0 && ms > frameLoadMaxMs) { frameLoadMaxMs = ms; frameLoadMaxName = method + " " + what; }
        p.Write(Time.realtimeSinceStartup, (float)ms, GC.CollectionCount(0),
                "L " + method + " d=" + depth + " f=" + Time.frameCount + " ms=" + ms.ToString("F2") +
                " first=" + (first ? "Y" : "N") + " what=" + what);
    }

    // Name of what was loaded: all string arguments (joined with |), else the first Unity object argument, else the result.
    private static string Describe(object[] args, object result)
    {
        try
        {
            if (args != null)
            {
                string strs = null;
                foreach (object a in args)
                {
                    string str = a as string;
                    if (!string.IsNullOrEmpty(str)) strs = strs == null ? str : strs + "|" + str;
                }
                if (strs != null) return CleanName(strs);
                foreach (object a in args)
                {
                    UnityEngine.Object o = a as UnityEngine.Object;
                    if ((object)o != null && o != null) return CleanName(o.GetType().Name + ":" + o.name);
                }
            }
            UnityEngine.Object r = result as UnityEngine.Object;
            if ((object)r != null && r != null) return CleanName(r.GetType().Name + ":" + r.name);
            if (args != null)
                foreach (object a in args) if (a != null) return CleanName(a.GetType().Name + ":" + a.ToString());
        }
        catch (Exception) { }
        return "?";
    }

    // Like Clean() but keeps the END of long names (file names sit at the end of paths).
    private static string CleanName(string s)
    {
        if (s == null) return "null";
        s = s.Replace(',', ' ').Replace(';', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 90 ? "..." + s.Substring(s.Length - 87) : s;
    }

    private static void TimePrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void TimePostfix(long __state, MethodBase __originalMethod)
    {
        double ms = (Stopwatch.GetTimestamp() - __state) * tickToMs;
        if (ms < minCallMs) return;
        if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
        GcProbe p = instance;
        if (p == null || p.log == null) return;
        string name = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
        p.Write(Time.realtimeSinceStartup, (float)ms, GC.CollectionCount(0),
                "T " + name + " f=" + Time.frameCount + " ms=" + ms.ToString("F2"));
    }

    private void Start()
    {
        if (preallocMb.Value > 0) PreGrowHeap(preallocMb.Value);
    }

    private void PreGrowHeap(int mb)
    {
        float t0 = Time.realtimeSinceStartup;

        // Small 1 KB blocks, as in the Unity manual's example, so the GC treats them
        // like normal small objects rather than special large blocks.
        const int chunk = 1024;
        int count = (int)((long)mb * 1024 * 1024 / chunk);
        object[] hold = new object[count];
        for (int i = 0; i < count; i++) hold[i] = new byte[chunk];
        double grownMb = GC.GetTotalMemory(false) / 1048576.0;
        hold = null;
        GC.Collect();

        float took = (Time.realtimeSinceStartup - t0) * 1000f;
        lastCount = GC.CollectionCount(0);
        if (log != null)
            Write(Time.realtimeSinceStartup, took, lastCount,
                  "PREALLOC " + mb + "MB peak " + grownMb.ToString("F0") + "MB");
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (log != null)
            Write(Time.realtimeSinceStartup, 0f, GC.CollectionCount(0), "LOAD " + scene.name);
        if (collectOnLoad.Value)
        {
            // Several scenes load back to back for one area; each load pushes the collection back.
            collectPending = true;
            collectAt = Time.realtimeSinceStartup + collectDelay.Value;
        }
        if (log != null && censusEnabled.Value)
        {
            // Same idea: one census per area, 0.6 s after the last scene load (just after the forced GC).
            censusPending = true;
            censusAt = Time.realtimeSinceStartup + collectDelay.Value + 0.1f;
        }
        if (cleanTooltips.Value || (prewarmIcons.Value && !prewarmDone) || (prejitEnabled.Value && prejitQueue == null && !prejitDone))
        {
            // Tooltip janitor runs just before the census, so the census shows the cleaned state.
            janitorPending = true;
            janitorAt = Time.realtimeSinceStartup + collectDelay.Value + 0.05f;   // right after the forced GC
        }
    }

    // ---- v1.7 combat tooltip janitor ----------------------------------------------------------
    // Game.UI.UICombatTooltipManager keeps one UICombatTooltip per character in m_activeTips and
    // spare ones in m_TipPool. Census data showed ~19 new tooltips (one per character in the fight)
    // left active after every reload: their characters are gone, but the tooltips never go back
    // to the pool, stay active and are updated every frame. The janitor hands every tooltip whose
    // character no longer exists to the manager's own Remove(), which deactivates it and returns
    // it to the pool, and deactivates fully orphaned tooltips. Tooltips of living characters are
    // never touched.

    private static Type tipMgrType, tipType;
    private static FieldInfo fActiveTips, fTipPool, fTipTarget;
    private static MethodInfo mTipRemove;
    private static bool janitorInit, janitorReady;

    private void InitJanitor()
    {
        janitorInit = true;
        tipMgrType = AccessTools.TypeByName("Game.UI.UICombatTooltipManager") ?? AccessTools.TypeByName("UICombatTooltipManager");
        tipType = AccessTools.TypeByName("Game.UI.UICombatTooltip") ?? AccessTools.TypeByName("UICombatTooltip");
        if (tipMgrType != null && tipType != null)
        {
            fActiveTips = AccessTools.Field(tipMgrType, "m_activeTips");
            fTipPool = AccessTools.Field(tipMgrType, "m_TipPool");
            fTipTarget = AccessTools.Field(tipType, "m_targetGameObject");
            mTipRemove = AccessTools.Method(tipMgrType, "Remove");
        }
        janitorReady = fActiveTips != null && fTipPool != null && fTipTarget != null &&
                       mTipRemove != null && mTipRemove.GetParameters().Length == 1;
        if (!janitorReady) Logger.LogWarning("Janitor: tooltip manager members not found; tooltip cleanup disabled.");
    }

    // ---- v1.12 icon atlas prewarm --------------------------------------------------------------
    private void RunPrewarm()
    {
        Type mgrType = AccessTools.TypeByName("Game.UI.UIAtlasManager");
        Type atlasType = AccessTools.TypeByName("UIAtlas");
        if (mgrType == null || atlasType == null) { prewarmDone = true; Logger.LogWarning("Prewarm: types not found."); return; }
        MethodInfo clip = null;
        foreach (MethodInfo m in AccessTools.GetDeclaredMethods(mgrType))
            if (m.Name == "GetCircleClippedAtlas" && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.IsAssignableFrom(atlasType)) clip = m;
        if (clip == null) { prewarmDone = true; Logger.LogWarning("Prewarm: GetCircleClippedAtlas not found."); return; }

        UnityEngine.Object mgr = UnityEngine.Object.FindObjectOfType(mgrType);
        if (mgr == null) return;                         // not in game yet (e.g. main menu): try again next load

        UnityEngine.Object[] atlases = Resources.FindObjectsOfTypeAll(atlasType);
        float t0 = Time.realtimeSinceStartup;
        int built = 0;
        StringBuilder missing = new StringBuilder();
        foreach (string raw in prewarmAtlasNames.Value.Split(','))
        {
            string want = raw.Trim();
            if (want.Length == 0) continue;
            // Every loaded atlas object with that name (asset and/or instance): the game caches per object,
            // so warming all of them guarantees the one the HUD uses is warmed.
            int matches = 0;
            foreach (UnityEngine.Object a in atlases)
            {
                if (a == null || a.name != want) continue;
                matches++;
                clip.Invoke(mgr, new object[] { a });      // the game's own call; result is cached by the game
                built++;
            }
            if (matches == 0) missing.Append(want).Append(' ');
        }
        float took = (Time.realtimeSinceStartup - t0) * 1000f;
        if (missing.Length > 0 && built == 0) return;     // atlas not loaded yet: try again next load
        prewarmDone = true;
        string msg = "PREWARM atlases=" + built + " ms=" + took.ToString("F1") +
                     (missing.Length > 0 ? " missing=" + missing.ToString().Trim() : "");
        if (log != null) Write(Time.realtimeSinceStartup, took, GC.CollectionCount(0), msg);
        else Logger.LogInfo(msg);
    }

    // ---- v1.13 prejit -------------------------------------------------------------------------
    private void BuildPrejitQueue()
    {
        Type onyxBase = AccessTools.TypeByName("Onyx.OnyxBehavior");
        Assembly game = onyxBase != null ? onyxBase.Assembly : null;
        if (game == null) { prejitDone = true; Logger.LogWarning("Prejit: game assembly not found."); return; }
        List<string> exact = new List<string>();
        List<string> prefixes = new List<string>();
        foreach (string raw in prejitTargets.Value.Split(','))
        {
            string e = raw.Trim();
            if (e.Length == 0) continue;
            if (e.EndsWith(".*")) prefixes.Add(e.Substring(0, e.Length - 1));   // "Game.AI."
            else exact.Add(e);
        }
        Type[] types;
        try { types = game.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        prejitQueue = new List<MethodBase>();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type t in types)
        {
            if (t == null || t.ContainsGenericParameters || t.IsInterface) continue;
            Type outer = t;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            string full = outer.FullName ?? "";
            bool match = exact.Contains(full);
            if (!match)
                foreach (string p in prefixes) if (full.StartsWith(p)) { match = true; break; }
            if (!match) continue;
            foreach (MethodInfo m in t.GetMethods(all))
            {
                if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters) continue;
                if (m.GetMethodBody() == null) continue;
                prejitQueue.Add(m);
            }
            foreach (ConstructorInfo c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (c.GetMethodBody() != null) prejitQueue.Add(c);
        }
        prejitIndex = 0;
        prejitStartT = Time.realtimeSinceStartup;
        if (prejitQueue.Count == 0) { prejitDone = true; Logger.LogWarning("Prejit: no methods matched " + prejitTargets.Value); }
    }

    private void RunPrejitSlice()
    {
        long start = Stopwatch.GetTimestamp();
        double budget = prejitBudgetMs.Value;
        prejitFrames++;
        while (prejitIndex < prejitQueue.Count)
        {
            MethodBase m = prejitQueue[prejitIndex++];
            try { m.MethodHandle.GetFunctionPointer(); }   // Mono: compiles the method without running it
            catch (Exception) { prejitFailed++; }
            if ((Stopwatch.GetTimestamp() - start) * tickToMs >= budget) break;
        }
        prejitTotalMs += (Stopwatch.GetTimestamp() - start) * tickToMs;
        if (prejitIndex < prejitQueue.Count) return;
        prejitDone = true;
        string msg = "PREJIT methods=" + prejitQueue.Count + " failed=" + prejitFailed + " compileMs=" + prejitTotalMs.ToString("F0") +
                     " frames=" + prejitFrames + " seconds=" + (Time.realtimeSinceStartup - prejitStartT).ToString("F1");
        prejitQueue = null;
        if (log != null) Write(Time.realtimeSinceStartup, (float)prejitTotalMs, GC.CollectionCount(0), msg);
        else Logger.LogInfo(msg);
    }

    private void RunJanitor()
    {
        if (!cleanTooltips.Value) return;
        if (!janitorInit) InitJanitor();
        if (!janitorReady) return;
        float t0 = Time.realtimeSinceStartup;

        UnityEngine.Object mgr = UnityEngine.Object.FindObjectOfType(tipMgrType);
        if (mgr == null) return;
        IDictionary active = fActiveTips.GetValue(mgr) as IDictionary;
        IList pool = fTipPool.GetValue(mgr) as IList;
        if (active == null || pool == null) return;

        // Snapshot (key, tooltip) pairs first. Destroyed Unity objects all compare equal to each
        // other (Object.Equals treats them as null), so dictionary lookups by a dead key are not
        // trusted: every dead tooltip is verified afterwards by reference.
        Type keyType = mTipRemove.GetParameters()[0].ParameterType;
        List<object> deadKeys = new List<object>();
        List<Component> deadTips = new List<Component>();
        int nullTips = 0;
        foreach (DictionaryEntry de in active)
        {
            Component tip = de.Value as Component;
            if (tip == null) { nullTips++; continue; }      // tooltip object itself destroyed
            UnityEngine.Object keyObj = de.Key as UnityEngine.Object;
            bool dead = de.Key == null || ((object)keyObj != null && keyObj == null);
            UnityEngine.Object target = fTipTarget.GetValue(tip) as UnityEngine.Object;
            if ((object)target != null && target == null) dead = true;   // character destroyed (set_SelectedObject fills this field)
            if (dead) { deadKeys.Add(de.Key); deadTips.Add(tip); }
        }

        // 1) Let the game do it: Remove(key) deactivates the tooltip and returns it to the pool.
        int removeCalls = 0;
        for (int i = 0; i < deadKeys.Count; i++)
        {
            object key = deadKeys[i];
            if (key == null || !keyType.IsInstanceOfType(key)) continue;
            try { mTipRemove.Invoke(mgr, new object[] { key }); removeCalls++; }
            catch (Exception) { }
        }

        // 2) Verify by reference and fix whatever is left: drop dict entries that still point to a
        //    dead tooltip (or to a destroyed one), make sure each dead tooltip is pooled once and inactive.
        HashSet<int> deadIds = new HashSet<int>();
        foreach (Component tip in deadTips) deadIds.Add(tip.GetInstanceID());
        List<object> leftoverKeys = new List<object>();
        foreach (DictionaryEntry de in active)
        {
            Component tip = de.Value as Component;
            if (tip == null || deadIds.Contains(tip.GetInstanceID())) leftoverKeys.Add(de.Key);
        }
        int fixedUp = leftoverKeys.Count;
        foreach (object key in leftoverKeys) { try { active.Remove(key); } catch (Exception) { } }

        HashSet<int> pooled = new HashSet<int>();
        for (int i = pool.Count - 1; i >= 0; i--)
        {
            Component c = pool[i] as Component;
            if (c == null || !pooled.Add(c.GetInstanceID())) pool.RemoveAt(i);   // drop destroyed or duplicate entries
        }
        foreach (Component tip in deadTips)
        {
            if (pooled.Add(tip.GetInstanceID())) { pool.Add(tip); fixedUp++; }
            if (tip.gameObject.activeSelf) { tip.gameObject.SetActive(false); fixedUp++; }
        }

        // 3) Active tooltips that neither the active list nor the pool knows about.
        HashSet<int> known = new HashSet<int>(pooled);
        foreach (DictionaryEntry de in active) { Component c = de.Value as Component; if (c != null) known.Add(c.GetInstanceID()); }
        int orphans = 0;
        foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(tipType))
        {
            Component c = o as Component;
            if (c == null || known.Contains(c.GetInstanceID())) continue;
            if (!c.gameObject.activeSelf) continue;
            UnityEngine.Object target = fTipTarget.GetValue(c) as UnityEngine.Object;
            if (target != null) continue;                   // its character still exists: leave it alone
            c.gameObject.SetActive(false);
            if (pooled.Add(c.GetInstanceID())) pool.Add(c);
            orphans++;
        }

        float took = (Time.realtimeSinceStartup - t0) * 1000f;
        if (log != null)
            Write(Time.realtimeSinceStartup, took, GC.CollectionCount(0),
                  "JANITOR deadTips=" + deadTips.Count + " removeCalls=" + removeCalls + " fixedUp=" + fixedUp +
                  " nullTips=" + nullTips + " orphans=" + orphans +
                  " active=" + active.Count + " pool=" + pool.Count + " ms=" + took.ToString("F1"));
    }

    // ---- v1.6 object census -------------------------------------------------------------------
    // Counts live objects by type once per area load, behind the loading screen, so leftovers that
    // survive reloads show up as counts that grow from load to load. Rows start with CENSUS.

    private static string Clean(string s)
    {
        if (s == null) return "null";
        s = s.Replace(',', ' ').Replace(';', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 40 ? s.Substring(0, 40) : s;
    }

    private static string Top(Dictionary<string, int> counts, int n)
    {
        List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(counts);
        list.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < list.Count && i < n; i++)
        {
            if (i > 0) sb.Append("; ");
            sb.Append(list[i].Key).Append('=').Append(list[i].Value);
        }
        return sb.ToString();
    }

    private static void Add(Dictionary<string, int> d, string key)
    {
        int v;
        d.TryGetValue(key, out v);
        d[key] = v + 1;
    }

    private void RunCensus()
    {
        float t0 = Time.realtimeSinceStartup;
        censusNumber++;
        string tag = "CENSUS#" + censusNumber;

        // Cameras: enabled ones in detail (an extra camera = an extra render pass).
        Camera[] cams = Camera.allCameras;
        int camTotal = UnityEngine.Object.FindObjectsOfType(typeof(Camera)).Length;
        StringBuilder camList = new StringBuilder();
        foreach (Camera c in cams)
        {
            if (camList.Length > 0) camList.Append("; ");
            camList.Append(Clean(c.name)).Append(" d=").Append(c.depth.ToString("F0"));
            if (c.targetTexture != null) camList.Append(" rt=").Append(c.targetTexture.width).Append('x').Append(c.targetTexture.height);
        }

        UnityEngine.Object[] lights = UnityEngine.Object.FindObjectsOfType(typeof(Light));
        int lightOn = 0, lightShadow = 0;
        Dictionary<string, int> lightNames = new Dictionary<string, int>();
        foreach (UnityEngine.Object o in lights)
        {
            Light l = (Light)o;
            if (!l.enabled) continue;
            lightOn++;
            if (l.shadows != LightShadows.None) lightShadow++;
            Add(lightNames, Clean(l.transform.root.name));
        }

        UnityEngine.Object[] pss = UnityEngine.Object.FindObjectsOfType(typeof(ParticleSystem));
        int psPlaying = 0, psAlive = 0;
        Dictionary<string, int> psRoots = new Dictionary<string, int>();
        foreach (UnityEngine.Object o in pss)
        {
            ParticleSystem p = (ParticleSystem)o;
            if (p.isPlaying) psPlaying++;
            psAlive += p.particleCount;
            Add(psRoots, Clean(p.transform.root.name));
        }

        UnityEngine.Object[] audios = UnityEngine.Object.FindObjectsOfType(typeof(AudioSource));
        int audioPlaying = 0;
        foreach (UnityEngine.Object o in audios) if (((AudioSource)o).isPlaying) audioPlaying++;

        UnityEngine.Object[] rends = UnityEngine.Object.FindObjectsOfType(typeof(Renderer));
        int rendOn = 0;
        foreach (UnityEngine.Object o in rends) if (((Renderer)o).enabled) rendOn++;

        UnityEngine.Object[] rts = Resources.FindObjectsOfTypeAll(typeof(RenderTexture));
        int rtCreated = 0; double rtMpix = 0;
        Dictionary<string, int> rtSizes = new Dictionary<string, int>();
        foreach (UnityEngine.Object o in rts)
        {
            RenderTexture r = (RenderTexture)o;
            if (!r.IsCreated()) continue;
            rtCreated++;
            rtMpix += (double)r.width * r.height / 1e6;
            Add(rtSizes, r.width + "x" + r.height);
        }

        UnityEngine.Object[] gos = UnityEngine.Object.FindObjectsOfType(typeof(GameObject));
        int ddol = 0;
        Dictionary<string, int> ddolNames = new Dictionary<string, int>();
        foreach (UnityEngine.Object o in gos)
        {
            GameObject g = (GameObject)o;
            if (g.transform.parent != null) continue;          // root objects only
            if (g.scene.name != "DontDestroyOnLoad") continue;  // objects that survive loads
            ddol++;
            Add(ddolNames, Clean(g.name));
        }

        // Every component type on active objects (Unity's own and the game's scripts), so a leftover
        // shows up by type name even if it is not in the hand-picked list above.
        UnityEngine.Object[] comps = UnityEngine.Object.FindObjectsOfType(typeof(Component));
        Dictionary<string, int> types = new Dictionary<string, int>();
        foreach (UnityEngine.Object o in comps)
        {
            if (o == null) continue;
            Add(types, o.GetType().Name);
        }

        float took = (Time.realtimeSinceStartup - t0) * 1000f;
        int gc = GC.CollectionCount(0);
        Write(Time.realtimeSinceStartup, took, gc, tag +
              " cams=" + cams.Length + "/" + camTotal + " lights=" + lightOn + "/" + lights.Length +
              " shadowLights=" + lightShadow + " particles=" + psPlaying + "/" + pss.Length + " alive=" + psAlive +
              " audio=" + audioPlaying + "/" + audios.Length + " renderers=" + rendOn + "/" + rends.Length +
              " rt=" + rtCreated + " rtMpix=" + rtMpix.ToString("F1") + " gameObjects=" + gos.Length +
              " ddolRoots=" + ddol + " ms=" + took.ToString("F0"));
        Write(Time.realtimeSinceStartup, 0f, gc, tag + " CAMS " + camList);
        Write(Time.realtimeSinceStartup, 0f, gc, tag + " RT_SIZES " + Top(rtSizes, 12));
        Write(Time.realtimeSinceStartup, 0f, gc, tag + " DDOL " + Top(ddolNames, 15));
        Write(Time.realtimeSinceStartup, 0f, gc, tag + " PARTICLE_ROOTS " + Top(psRoots, 12));
        Write(Time.realtimeSinceStartup, 0f, gc, tag + " LIGHT_ROOTS " + Top(lightNames, 12));

        // All component types, sorted by count, 40 per row.
        List<KeyValuePair<string, int>> all = new List<KeyValuePair<string, int>>(types);
        all.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
        Write(Time.realtimeSinceStartup, 0f, gc, tag + " COMPONENTS total=" + comps.Length + " types=" + all.Count);
        StringBuilder row = new StringBuilder();
        for (int i = 0; i < all.Count; i++)
        {
            if (row.Length > 0) row.Append("; ");
            row.Append(Clean(all[i].Key)).Append('=').Append(all[i].Value);
            if ((i + 1) % 40 == 0 || i == all.Count - 1)
            {
                Write(Time.realtimeSinceStartup, 0f, gc, tag + " TYPES " + row);
                row.Length = 0;
            }
        }
        log.Flush();
    }

    private void Update()
    {
        float t = Time.realtimeSinceStartup;

        if (saveFinished)
        {
            saveFinished = false;
            if (log != null) Write(t, Time.unscaledDeltaTime * 1000f, GC.CollectionCount(0), "SAVE");
            if (collectOnSave.Value && (!collectPending || collectAt < t + 0.2f))
            {
                collectPending = true;
                collectAt = t + 0.2f;
            }
        }

        if (collectPending && t >= collectAt)
        {
            collectPending = false;
            float t0 = Time.realtimeSinceStartup;
            GC.Collect();
            float took = (Time.realtimeSinceStartup - t0) * 1000f;
            lastCount = GC.CollectionCount(0);
            if (log != null) Write(Time.realtimeSinceStartup, took, lastCount, "FORCED_GC");
        }

        if (janitorPending && !collectPending && t >= janitorAt)
        {
            janitorPending = false;
            try { RunJanitor(); }
            catch (Exception e) { Logger.LogWarning("Janitor failed: " + e.Message); }
            if (prewarmIcons.Value && !prewarmDone)
            {
                try { RunPrewarm(); }
                catch (Exception e) { prewarmDone = true; Logger.LogWarning("Prewarm failed: " + e.Message); }
            }
            if (prejitEnabled.Value && prejitQueue == null && !prejitDone)
            {
                try { BuildPrejitQueue(); }
                catch (Exception e) { prejitDone = true; Logger.LogWarning("Prejit failed: " + e.Message); }
            }
        }

        if (prejitQueue != null && !prejitDone) RunPrejitSlice();

        if (censusPending && !collectPending && !janitorPending && t >= censusAt)
        {
            censusPending = false;
            try { RunCensus(); }
            catch (Exception e) { Logger.LogWarning("Census failed: " + e.Message); }
        }

        rootDepth = 0;
        iconDepth = 0;
        if (log == null) return;

        float ms = Time.unscaledDeltaTime * 1000f;
        int count = GC.CollectionCount(0);
        bool gc = count != lastCount;
        bool spike = ms > 20f;
        bool beat = t >= nextBeat;

        if (profileLoads.Value)
        {
            bool combat = InCombatSafe();
            if (combat != lastCombat)
            {
                Write(t, ms, count, (combat ? "COMBAT start" : "COMBAT end") + " f=" + Time.frameCount);
                lastCombat = combat;
            }
            double loadMs = frameLoadTicks * tickToMs;
            if (frameLoadCalls > 0 && (loadMs >= frameSummaryMs || spike))
                Write(t, (float)loadMs, count, "LF f=" + Time.frameCount + " frame=" + ms.ToString("F1") +
                      " loads=" + loadMs.ToString("F2") + " n=" + frameLoadCalls +
                      (frameLoadMaxName != null ? " top=" + frameLoadMaxMs.ToString("F2") + " " + frameLoadMaxName : ""));
            frameLoadTicks = 0; frameLoadCalls = 0; frameLoadMaxMs = 0; frameLoadMaxName = null;
            loadDepth = 0;
        }

        if (gc) Write(t, ms, count, "GC f=" + Time.frameCount);
        else if (spike) Write(t, ms, count, "SPIKE f=" + Time.frameCount);
        else if (beat) Write(t, ms, count, "tick");

        if (beat)
        {
            log.Flush();
            nextBeat = t + 1f;
        }
        lastCount = count;
    }

    private void Write(float t, float ms, int count, string evt)
    {
        double heapMb = GC.GetTotalMemory(false) / 1048576.0;
        log.WriteLine(t.ToString("F3") + "," + ms.ToString("F2") + "," + count + "," +
                      heapMb.ToString("F1") + "," + evt);
    }

    private void OnApplicationQuit()
    {
        if (log != null)
        {
            log.Flush();
            log.Close();
        }
    }
}
