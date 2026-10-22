// Deadfire Stutter Fix - a BepInEx 5 plugin for Pillars of Eternity II: Deadfire (Unity 5.6.3p3, Mono).
// MIT licence, see LICENSE. Built with Claude (Anthropic) in Claude Code; directed and tested by SSS.
//
// Four fixes, each for a measured cause of a stutter (details and evidence: docs/MEASUREMENTS.md).
// Every fix can be switched off in BepInEx\config\alex.deadfirestutterfix.cfg.
//
// 1) [Heap] Garbage collector. Unity 5.6's Mono GC stops the game for a full collection, and in this game
//    a collection takes ~100-170 ms. The fix grows the managed heap once at startup (PreallocateMB, the
//    "large heap" strategy from the Unity 5.6 manual) so collections are rare, and forces one collection
//    right after each scene load (behind the loading screen) and right after a save is written, so the
//    heap starts every area with free room instead of collecting in the middle of a fight.
//
// 2) [Corpses] Corpse click-colliders. After an enemy's death animation the game runs the coroutine
//    GameUtilities.CreateMeshForCollider on the corpse: it bakes the skinned mesh and cooks a MeshCollider
//    (what makes the corpse clickable for looting). That costs 22-44 ms of main-thread work per corpse.
//    Corpses cannot be looted during combat anyway, so the fix runs the game's own coroutine unchanged,
//    but only after combat has ended, one corpse every SpacingFrames frames. Calls outside combat
//    (containers at area load, closing a loot window) are not touched.
//
// 3) [Fixes] CleanCombatTooltips. Every reload left ~19 combat tooltips (one per character in the fight)
//    active although their characters were gone; they were updated every frame and piled up reload after
//    reload. After each area load the fix hands each dead tooltip to the game's own
//    UICombatTooltipManager.Remove(), which returns it to the game's pool. Tooltips of living characters are
//    never touched.
//
// 4) [Fixes] PrewarmIconAtlases. The first time in a launch that an action icon shows an ability (or an
//    item), the game builds a circle-clipped copy of the whole icon atlas (UIAtlasManager.
//    GetCircleClippedAtlas -> UIAtlas.set_spriteMaterial): one ~70-100 ms frame in the first fight. The game
//    keeps that copy for the rest of the launch. The fix makes the same call once, behind a loading screen.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("alex.deadfirestutterfix", "Deadfire Stutter Fix", "1.0.0")]
[BepInIncompatibility("alex.gcprobe")]   // the development build (GC Probe) contains the same fixes: never run both
public class DeadfireStutterFix : BaseUnityPlugin
{
    // Set by the game's save thread, read and cleared by Update() on the main thread.
    private static volatile bool saveFinished;

    private ConfigEntry<int> preallocMb;
    private ConfigEntry<bool> collectOnLoad;
    private ConfigEntry<bool> collectOnSave;
    private ConfigEntry<float> collectDelay;
    private ConfigEntry<bool> deferColliders;
    private ConfigEntry<int> spacingFramesCfg;
    private ConfigEntry<int> delayAfterCombatCfg;
    private ConfigEntry<bool> cleanTooltips;
    private ConfigEntry<bool> prewarmIcons;
    private ConfigEntry<string> prewarmAtlasNames;

    private bool collectPending;
    private float collectAt;
    private bool afterLoadPending;
    private float afterLoadAt;
    private bool prewarmDone;

    // Corpse-collider state (main thread only).
    private static Func<bool> inCombat;
    private static int spacingFrames;
    private static int delayAfterCombatFrames;
    private static int lastInCombatFrame = -1000000;
    private static int nextColliderFrame;

    private void Awake()
    {
        preallocMb = Config.Bind("Heap", "PreallocateMB", 1024,
            "MB to allocate and release once at startup so the managed heap grows and garbage collections become rare. 0 = off.");
        collectOnLoad = Config.Bind("Heap", "CollectOnLoad", true,
            "Force one garbage collection right after the last scene load of an area, behind the loading screen.");
        collectDelay = Config.Bind("Heap", "CollectDelaySeconds", 0.05f,
            "Seconds after a scene load before that collection. Each further scene load restarts the wait. " +
            "Keep it short: on quick transitions the loading screen is gone soon after the scene load.");
        collectOnSave = Config.Bind("Heap", "CollectOnSave", true,
            "Force one garbage collection 0.2 s after a save game has been written to disk.");
        deferColliders = Config.Bind("Corpses", "DeferColliders", true,
            "Build corpse click-colliders only after combat ends, one corpse at a time.");
        spacingFramesCfg = Config.Bind("Corpses", "SpacingFrames", 10,
            "Minimum number of frames between two corpse collider builds.");
        delayAfterCombatCfg = Config.Bind("Corpses", "DelayAfterCombatFrames", 30,
            "Frames to wait after combat ends before building the first deferred corpse collider.");
        cleanTooltips = Config.Bind("Fixes", "CleanCombatTooltips", true,
            "After each area load, return combat tooltips whose character no longer exists to the game's tooltip pool " +
            "(otherwise they stay active and pile up with every reload).");
        prewarmIcons = Config.Bind("Fixes", "PrewarmIconAtlases", true,
            "Behind a loading screen, build the circle-clipped icon atlases the game would otherwise build in the " +
            "first fight of a launch (one ~70-100 ms stutter). Once per launch.");
        prewarmAtlasNames = Config.Bind("Fixes", "PrewarmAtlasNames", "SpellAbilityIcons,ItemIcons",
            "Comma-separated UIAtlas names to pre-build circle-clipped copies of.");

        spacingFrames = Math.Max(1, spacingFramesCfg.Value);
        delayAfterCombatFrames = Math.Max(0, delayAfterCombatCfg.Value);

        SceneManager.sceneLoaded += OnSceneLoaded;

        Harmony harmony = new Harmony("alex.deadfirestutterfix");
        string save = collectOnSave.Value ? PatchSave(harmony) : "off";
        string corpses = deferColliders.Value ? PatchCorpseColliders(harmony) : "off";

        Logger.LogInfo("Deadfire Stutter Fix 1.0.0 | PreallocateMB=" + preallocMb.Value +
                       " CollectOnLoad=" + collectOnLoad.Value + " CollectDelaySeconds=" + collectDelay.Value +
                       " CollectOnSave=" + save + " DeferColliders=" + corpses +
                       " SpacingFrames=" + spacingFrames + " DelayAfterCombatFrames=" + delayAfterCombatFrames +
                       " CleanCombatTooltips=" + cleanTooltips.Value +
                       " PrewarmIconAtlases=" + prewarmIcons.Value + " PrewarmAtlasNames=" + prewarmAtlasNames.Value);
    }

    private void Start()
    {
        if (preallocMb.Value > 0) PreGrowHeap(preallocMb.Value);
    }

    // ---- 1) Garbage collector -------------------------------------------------------------------

    private void PreGrowHeap(int mb)
    {
        float t0 = Time.realtimeSinceStartup;
        // Small 1 KB blocks, as in the Unity manual's example, so the GC treats them like normal small
        // objects. Holding them all at once makes the heap grow; dropping them leaves that room free.
        const int chunk = 1024;
        int count = (int)((long)mb * 1024 * 1024 / chunk);
        object[] hold = new object[count];
        for (int i = 0; i < count; i++) hold[i] = new byte[chunk];
        hold = null;
        GC.Collect();
        Logger.LogInfo("Heap pre-grown by " + mb + " MB in " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0") + " ms.");
    }

    private string PatchSave(Harmony harmony)
    {
        MethodInfo target = AccessTools.Method("Game.SaveLoadManager:ZipAndSaveFileToDiskThread");
        if (target == null)
        {
            Logger.LogWarning("Save method not found; CollectOnSave is off.");
            return "off (method not found)";
        }
        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(DeadfireStutterFix), "OnSaveWritten"));
            return "on";
        }
        catch (Exception e)
        {
            Logger.LogError("Could not hook the save method: " + e);
            return "off (hook failed)";
        }
    }

    // Harmony postfix. Runs on the game's save thread: no Unity calls here.
    private static void OnSaveWritten()
    {
        saveFinished = true;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        float now = Time.realtimeSinceStartup;
        if (collectOnLoad.Value)
        {
            // Several scenes load back to back for one area; each load pushes the collection back.
            collectPending = true;
            collectAt = now + collectDelay.Value;
        }
        if (cleanTooltips.Value || (prewarmIcons.Value && !prewarmDone))
        {
            // Tooltip cleanup and icon prewarm run right after the forced collection, still behind the loading screen.
            afterLoadPending = true;
            afterLoadAt = now + collectDelay.Value + 0.05f;
        }
    }

    private void Update()
    {
        float t = Time.realtimeSinceStartup;

        if (saveFinished)
        {
            saveFinished = false;
            if (!collectPending || collectAt < t + 0.2f)
            {
                collectPending = true;
                collectAt = t + 0.2f;
            }
        }

        if (collectPending && t >= collectAt)
        {
            collectPending = false;
            GC.Collect();
        }

        if (afterLoadPending && !collectPending && t >= afterLoadAt)
        {
            afterLoadPending = false;
            if (cleanTooltips.Value)
            {
                try { RunTooltipCleanup(); }
                catch (Exception e) { Logger.LogWarning("Tooltip cleanup failed: " + e.Message); }
            }
            if (prewarmIcons.Value && !prewarmDone)
            {
                try { RunPrewarm(); }
                catch (Exception e) { prewarmDone = true; Logger.LogWarning("Icon prewarm failed: " + e.Message); }
            }
        }
    }

    // ---- 2) Corpse click-colliders --------------------------------------------------------------

    private string PatchCorpseColliders(Harmony harmony)
    {
        MethodInfo getter = AccessTools.PropertyGetter(AccessTools.TypeByName("Game.GameState"), "InCombat");
        MethodInfo target = AccessTools.Method("Game.GameUtilities:CreateMeshForCollider");
        if (getter == null || !getter.IsStatic || getter.ReturnType != typeof(bool))
        {
            Logger.LogWarning("Corpses: GameState.InCombat not found; corpse fix is off.");
            return "off (InCombat not found)";
        }
        if (target == null || target.ReturnType != typeof(IEnumerator))
        {
            Logger.LogWarning("Corpses: CreateMeshForCollider not found; corpse fix is off.");
            return "off (method not found)";
        }
        try
        {
            inCombat = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(DeadfireStutterFix), "DeferPostfix"));
            return "on";
        }
        catch (Exception e)
        {
            Logger.LogError("Corpses: could not hook CreateMeshForCollider: " + e);
            return "off (hook failed)";
        }
    }

    // Harmony postfix on GameUtilities.CreateMeshForCollider. During combat the game gets a wrapper instead of
    // its own coroutine; the wrapper runs the original unchanged once it is allowed to.
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
        // Wait while in combat, then DelayAfterCombatFrames more (so the first corpse does not land on the
        // game's own end-of-combat frame), then keep SpacingFrames between corpses. If a new fight starts
        // during the wait, wait for that one too. If the corpse goes away meanwhile, Unity stops this coroutine.
        while (true)
        {
            int now = Time.frameCount;
            if (InCombatSafe())
            {
                lastInCombatFrame = now;
                yield return null;
                continue;
            }
            if (now < lastInCombatFrame + delayAfterCombatFrames || now < nextColliderFrame)
            {
                yield return null;
                continue;
            }
            break;
        }
        nextColliderFrame = Time.frameCount + spacingFrames;
        while (original.MoveNext()) yield return original.Current;
    }

    private static bool InCombatSafe()
    {
        try { return inCombat != null && inCombat(); }
        catch { return false; }
    }

    // ---- 3) Combat tooltip cleanup ----------------------------------------------------------------
    // Game.UI.UICombatTooltipManager keeps one UICombatTooltip per character in m_activeTips and spare ones in
    // m_TipPool. After a reload the tooltips of the old characters were never returned to the pool.

    private static Type tipMgrType, tipType;
    private static FieldInfo fActiveTips, fTipPool, fTipTarget;
    private static MethodInfo mTipRemove;
    private static bool tooltipInit, tooltipReady;

    private void InitTooltipCleanup()
    {
        tooltipInit = true;
        tipMgrType = AccessTools.TypeByName("Game.UI.UICombatTooltipManager") ?? AccessTools.TypeByName("UICombatTooltipManager");
        tipType = AccessTools.TypeByName("Game.UI.UICombatTooltip") ?? AccessTools.TypeByName("UICombatTooltip");
        if (tipMgrType != null && tipType != null)
        {
            fActiveTips = AccessTools.Field(tipMgrType, "m_activeTips");
            fTipPool = AccessTools.Field(tipMgrType, "m_TipPool");
            fTipTarget = AccessTools.Field(tipType, "m_targetGameObject");
            mTipRemove = AccessTools.Method(tipMgrType, "Remove");
        }
        tooltipReady = fActiveTips != null && fTipPool != null && fTipTarget != null &&
                       mTipRemove != null && mTipRemove.GetParameters().Length == 1;
        if (!tooltipReady) Logger.LogWarning("Tooltip manager members not found; tooltip cleanup is off.");
    }

    private void RunTooltipCleanup()
    {
        if (!tooltipInit) InitTooltipCleanup();
        if (!tooltipReady) return;

        UnityEngine.Object mgr = UnityEngine.Object.FindObjectOfType(tipMgrType);
        if (mgr == null) return;
        IDictionary active = fActiveTips.GetValue(mgr) as IDictionary;
        IList pool = fTipPool.GetValue(mgr) as IList;
        if (active == null || pool == null) return;

        // Snapshot (key, tooltip) pairs first. Destroyed Unity objects all compare equal to each other
        // (Object.Equals treats them as null), so dictionary lookups by a dead key are not trusted: every
        // dead tooltip is verified afterwards by instance ID.
        Type keyType = mTipRemove.GetParameters()[0].ParameterType;
        List<object> deadKeys = new List<object>();
        List<Component> deadTips = new List<Component>();
        foreach (DictionaryEntry de in active)
        {
            Component tip = de.Value as Component;
            if (tip == null) continue;                                   // tooltip object itself destroyed
            UnityEngine.Object keyObj = de.Key as UnityEngine.Object;
            bool dead = de.Key == null || ((object)keyObj != null && keyObj == null);
            UnityEngine.Object target = fTipTarget.GetValue(tip) as UnityEngine.Object;
            if ((object)target != null && target == null) dead = true;  // its character was destroyed
            if (dead) { deadKeys.Add(de.Key); deadTips.Add(tip); }
        }

        // a) Let the game do it: Remove(key) deactivates the tooltip and returns it to the pool.
        foreach (object key in deadKeys)
        {
            if (key == null || !keyType.IsInstanceOfType(key)) continue;
            try { mTipRemove.Invoke(mgr, new object[] { key }); }
            catch (Exception) { }
        }

        // b) Verify and fix whatever is left: drop entries that still point to a dead or destroyed tooltip,
        //    make sure each dead tooltip is in the pool exactly once and inactive.
        HashSet<int> deadIds = new HashSet<int>();
        foreach (Component tip in deadTips) deadIds.Add(tip.GetInstanceID());
        List<object> leftoverKeys = new List<object>();
        foreach (DictionaryEntry de in active)
        {
            Component tip = de.Value as Component;
            if (tip == null || deadIds.Contains(tip.GetInstanceID())) leftoverKeys.Add(de.Key);
        }
        foreach (object key in leftoverKeys) { try { active.Remove(key); } catch (Exception) { } }

        HashSet<int> pooled = new HashSet<int>();
        for (int i = pool.Count - 1; i >= 0; i--)
        {
            Component c = pool[i] as Component;
            if (c == null || !pooled.Add(c.GetInstanceID())) pool.RemoveAt(i);   // destroyed or duplicate entries
        }
        foreach (Component tip in deadTips)
        {
            if (pooled.Add(tip.GetInstanceID())) pool.Add(tip);
            if (tip.gameObject.activeSelf) tip.gameObject.SetActive(false);
        }

        // c) Active tooltips that neither the active list nor the pool knows about, whose character is gone.
        HashSet<int> known = new HashSet<int>(pooled);
        foreach (DictionaryEntry de in active) { Component c = de.Value as Component; if (c != null) known.Add(c.GetInstanceID()); }
        int orphans = 0;
        foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(tipType))
        {
            Component c = o as Component;
            if (c == null || known.Contains(c.GetInstanceID())) continue;
            if (!c.gameObject.activeSelf) continue;
            UnityEngine.Object target = fTipTarget.GetValue(c) as UnityEngine.Object;
            if (target != null) continue;                                 // its character still exists: leave it alone
            c.gameObject.SetActive(false);
            if (pooled.Add(c.GetInstanceID())) pool.Add(c);
            orphans++;
        }

        if (deadTips.Count > 0 || orphans > 0)
            Logger.LogInfo("Tooltip cleanup: returned " + deadTips.Count + " tooltips of gone characters to the pool" +
                           (orphans > 0 ? ", " + orphans + " orphans" : "") + ". Active " + active.Count + ", pool " + pool.Count + ".");
    }

    // ---- 4) Icon atlas prewarm ------------------------------------------------------------------

    private void RunPrewarm()
    {
        Type mgrType = AccessTools.TypeByName("Game.UI.UIAtlasManager");
        Type atlasType = AccessTools.TypeByName("UIAtlas");
        if (mgrType == null || atlasType == null) { prewarmDone = true; Logger.LogWarning("Icon prewarm: types not found; off."); return; }
        MethodInfo clip = null;
        foreach (MethodInfo m in AccessTools.GetDeclaredMethods(mgrType))
            if (m.Name == "GetCircleClippedAtlas" && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.IsAssignableFrom(atlasType)) clip = m;
        if (clip == null) { prewarmDone = true; Logger.LogWarning("Icon prewarm: GetCircleClippedAtlas not found; off."); return; }

        UnityEngine.Object mgr = UnityEngine.Object.FindObjectOfType(mgrType);
        if (mgr == null) return;                                          // not loaded yet: try again after the next load

        UnityEngine.Object[] atlases = Resources.FindObjectsOfTypeAll(atlasType);
        float t0 = Time.realtimeSinceStartup;
        int built = 0;
        StringBuilder missing = new StringBuilder();
        foreach (string raw in prewarmAtlasNames.Value.Split(','))
        {
            string want = raw.Trim();
            if (want.Length == 0) continue;
            // Every loaded atlas object with that name: the game caches per object, so warming all of them
            // makes sure the one the HUD uses is warm.
            int matches = 0;
            foreach (UnityEngine.Object a in atlases)
            {
                if (a == null || a.name != want) continue;
                matches++;
                clip.Invoke(mgr, new object[] { a });                    // the game's own call; the game caches the result
                built++;
            }
            if (matches == 0) missing.Append(want).Append(' ');
        }
        if (missing.Length > 0 && built == 0) return;                     // nothing loaded yet: try again after the next load
        prewarmDone = true;
        Logger.LogInfo("Icon prewarm: " + built + " atlases in " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F1") + " ms" +
                       (missing.Length > 0 ? " (not found: " + missing.ToString().Trim() + ")" : "") + ".");
    }
}
