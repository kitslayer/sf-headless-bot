using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFHeadlessHost
{
    // v26.6 map terrain + ground weapons (Fases 1–4).
    public partial class Plugin
    {
        private const int MapStateMaxPayload = 32;
        private const float MapSyncLogIntervalSec = 30f;
        private static bool _batchModeHost;
        private byte[] _cachedGroundWeaponsBody;
        private int _groundWeaponsEntryCount;
        private float _groundWeaponsRetryAt = -1f;
        private int _groundWeaponsRetryPass;
        private float _mapSyncDiagNextAt = -1f;
        private int _mapSyncObjectsRegistered;
        private float _mapSyncRetryAt = -1f;
        private int _mapSyncRetryPass;
        private const float MapSyncRetryIntervalSec = 4f;
        private const int MapSyncRetryMaxPasses = 4;
        private const float MapInfoBootstrapRetryIntervalSec = 4f;
        private const int MapInfoBootstrapMaxPasses = 3;
        /// <summary>Seconds after map load before weapons, countdown, MapInfo sync, sky spawns.</summary>
        internal static float OraclePreCombatGraceSec = 2f;
        private float _oraclePreCombatReadyAt = -1f;
        private int _oraclePreCombatSceneIndex = -1;
        private float _mapInfoBootstrapAt = -1f;
        private int _mapInfoBootstrapPass;
        private int _mapInfoLastBroadcastCount;
        private Type _mapSyncBaseType;
        private FieldInfo _mapSyncStartPosField;
        private MethodInfo _mapGetDataMethod;
        private MethodInfo _mapSetDataMethod;
        private FieldInfo _mapNetworkControlField;
        private float _skyWeaponTickAt = -1f;
        private int _skyWeaponSpawnCount;
        /// <summary>Realtime sky-weapon schedule — do not use randomWeaponCounter (stock GM.Update resets it).</summary>
        private float _oracleNextSkyWeaponAt = -1f;
        // 2026-06-10: densified for stage-0 RL (was 2 / 5 / 8) — weapons are
        // now physically real (loopback spawn fix) and pickup works; denser
        // drops give the agent regular armed practice windows.
        private const float OracleFirstSkyWeaponDelay = 1f;
        private const float OracleSkyWeaponIntervalMin = 3f;
        private const float OracleSkyWeaponIntervalMax = 5f;
        private static int _mapAwakeRegisterCount;
        private bool _oracleMapLoadInProgress;
        private float _oracleMapLoadStartedAt = -1f;
        private float _oracleMapLoadForceCompleteAt = -1f;
        private const float OracleMapLoadForceCompleteSec = 8f;

        private struct MapStateSnap
        {
            public float StartX, StartY;
            public byte[] Data;
        }

        private void InstallMapTerrainAuthorityPatches()
        {
            try
            {
                var harmony = new Harmony(PluginGuid + ".map-terrain");
                var mapBase = AccessTools.TypeByName("MapInfoSyncableBase");
                if ((object)mapBase != null)
                {
                    // MapInfoSyncableBase is an abstract MonoBehaviour with virtual Awake().
                    // In Mono 2.0 + Unity 5.6.3, Harmony patches on virtual methods of
                    // abstract MonoBehaviour base classes are bypassed by Unity's
                    // SendMessage dispatch — the postfix on the base never fires for
                    // concrete subclass Awake calls. Symptom: every EnsureMapSyncObjects
                    // log entry reported `awake-hits=0`. See Bug D in
                    // notes/bug-investigations/2026-05-24_v0.3.4-session-bugs.md.
                    //
                    // Fix: patch each concrete subclass's Awake too. The FindObjectsOfType
                    // fallback in EnsureMapSyncObjectsRegistered already catches them, but
                    // hooking Awake lets the postfix fire DURING scene-load instead of
                    // after, which is the design intent (m_NetworkControl=true assigned
                    // before any SF code reads the flag).
                    var awakeMethods = new System.Collections.Generic.HashSet<System.Reflection.MethodInfo>();
                    var baseAwake = AccessTools.Method(mapBase, "Awake");
                    if ((object)baseAwake != null) awakeMethods.Add(baseAwake);

                    // Known concrete subclasses in stock SF: GhostPlatform,
                    // MoveAlongPathUsingForce, PillarHandler. Discovery loop catches any
                    // future subclasses too.
                    // IMPORTANT: do NOT use `==` between System.Type instances — Mono 2.0
                    // doesn't have System.Type.op_Equality and C# 9 emits a call to it.
                    // Same landmine family as Environment.CurrentManagedThreadId.
                    // Use ReferenceEquals (which compiles to a direct cmp opcode).
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        System.Type[] types;
                        try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var t in types)
                        {
                            if (ReferenceEquals(t, mapBase)) continue;
                            if (!mapBase.IsAssignableFrom(t)) continue;
                            var subAwake = AccessTools.Method(t, "Awake");
                            if ((object)subAwake == null) continue;
                            if (!ReferenceEquals(subAwake.DeclaringType, t)) continue;
                            awakeMethods.Add(subAwake);
                        }
                    }

                    var postfix = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(MapInfoSyncableBaseAwakePostfix)));
                    int patched = 0;
                    foreach (var m in awakeMethods)
                    {
                        try { harmony.Patch(m, postfix: postfix); patched++; }
                        catch (Exception ex) { Log.LogWarning($"[v26.6] map-terrain Awake patch on {m.DeclaringType?.Name}: {ex.Message}"); }
                    }
                    Log.LogInfo($"[v26.6] Patched MapInfoSyncableBase + {patched - 1} subclass Awake methods (total {patched} patches).");
                }
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if (RefOk(mmType))
                {
                    var onMapInfo = AccessTools.Method(mmType, "OnMapInfoRecieved");
                    if (RefOk(onMapInfo))
                    {
                        harmony.Patch(onMapInfo, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(OnMapInfoRecievedPrefix))));
                        Log.LogInfo("[v0.3.3] Patched MultiplayerManager.OnMapInfoRecieved (fan-out to all MapInfoOnlineTag).");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[v26.6] map terrain patches: {e.Message}"); }
        }

        /// <summary>Stock only delivers MapInfo to the first MapInfoOnlineTag — lava maps need every tag.</summary>
        internal static bool OnMapInfoRecievedPrefix(byte[] data)
        {
            if (data == null || data.Length < 1) return true;
            try
            {
                var tagType = AccessTools.TypeByName("MapInfoOnlineTag");
                if (!RefOk(tagType)) return true;
                var tags = UnityEngine.Object.FindObjectsOfType(tagType);
                if (tags == null || tags.Length == 0) return true;
                foreach (var tag in tags)
                {
                    var comp = tag as Component;
                    if (!RefOk(comp)) continue;
                    comp.SendMessage("RecieveMapInfo", data, SendMessageOptions.DontRequireReceiver);
                }
                return false;
            }
            catch { return true; }
        }

        internal static void MapInfoSyncableBaseAwakePostfix(object __instance)
        {
            if (!_batchModeHost || ReferenceEquals(__instance, null)) return;
            try
            {
                EnsureOracleP2PNetworkReady("map-sync-awake");
                SetMapSyncNetworkControlGlobal(true);
                var t = __instance.GetType();
                var mapBase = AccessTools.TypeByName("MapInfoSyncableBase");
                var netF = RefOk(mapBase) ? AccessTools.Field(mapBase, "m_NetworkControl") : AccessTools.Field(t, "m_NetworkControl");
                if (RefOk(netF)) netF.SetValue(netF.IsStatic ? null : __instance, true);
                var startF = AccessTools.Field(t, "m_StartPos");
                if ((object)startF == null) return;
                Vector2 sp = (Vector2)startF.GetValue(__instance);
                sp = QuantizeMapSyncKey(sp);
                startF.SetValue(__instance, sp);
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                var add = AccessTools.Method(mm.GetType(), "AddMapDataObject");
                if ((object)add == null) return;
                UpsertMapDataObject(mm, add, sp, __instance as Component);
                _mapAwakeRegisterCount++;
            }
            catch { }
        }

        internal static void EnsureOracleP2PNetworkReady(string reason)
        {
            if (!_batchModeHost) return;
            try
            {
                var p2pType = AccessTools.TypeByName("P2PPackageHandler");
                if (!RefOk(p2pType)) return;
                object p2p = null;
                var inst = AccessTools.Property(p2pType, "Instance");
                if (RefOk(inst)) p2p = inst.GetValue(null, null);
                if (!RefOk(p2p)) p2p = UnityEngine.Object.FindObjectOfType(p2pType);
                if (!RefOk(p2p)) return;
                var f = AccessTools.Field(p2pType, "mHasSentOrReceived");
                if (RefOk(f) && !(bool)f.GetValue(p2p))
                {
                    f.SetValue(p2p, true);
                    Log.LogInfo($"[v0.3.3] P2P mHasSentOrReceived=true ({reason}).");
                }
            }
            catch (Exception e) { Log.LogWarning($"[v0.3.3] EnsureOracleP2PNetworkReady: {e.Message}"); }
        }

        internal static void SetMapSyncNetworkControlGlobal(bool on)
        {
            try
            {
                var mapBase = AccessTools.TypeByName("MapInfoSyncableBase");
                if (!RefOk(mapBase)) return;
                var netF = AccessTools.Field(mapBase, "m_NetworkControl");
                if (RefOk(netF)) netF.SetValue(null, on);
            }
            catch { }
        }

        private static void UpsertMapDataObject(object mm, MethodInfo add, Vector2 sp, Component comp)
        {
            if (!RefOk(mm) || !RefOk(add) || !RefOk(comp)) return;
            try
            {
                var dictF = AccessTools.Field(mm.GetType(), "mMapDataObjectToSync");
                if (RefOk(dictF))
                {
                    var dict = dictF.GetValue(mm);
                    if (RefOk(dict))
                    {
                        var contains = dict.GetType().GetMethod("ContainsKey");
                        var setItem = dict.GetType().GetMethod("set_Item");
                        if (RefOk(contains) && RefOk(setItem))
                        {
                            bool has = (bool)contains.Invoke(dict, new object[] { sp });
                            if (has) setItem.Invoke(dict, new object[] { sp, comp });
                            else add.Invoke(mm, new object[] { sp, comp });
                            return;
                        }
                    }
                }
                add.Invoke(mm, new object[] { sp, comp });
            }
            catch { }
        }

        private static object GetMultiplayerManagerInstance()
        {
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) return null;
                var instProp = AccessTools.Property(gmType, "Instance");
                object gm = null;
                if ((object)instProp != null) gm = instProp.GetValue(null, null);
                if (gm == null) return null;
                var mmF = AccessTools.Field(gmType, "mMultiplayerManager");
                if ((object)mmF == null) return null;
                return mmF.GetValue(gm);
            }
            catch { return null; }
        }

        internal void ClearMapDataObjectsOnOracle()
        {
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                var clear = AccessTools.Method(mm.GetType(), "ClearMapDataObjects");
                if ((object)clear != null)
                {
                    clear.Invoke(mm, null);
                    Log.LogInfo("[v26.6] ClearMapDataObjects invoked on oracle.");
                    return;
                }
                var dictF = AccessTools.Field(mm.GetType(), "mMapDataObjectToSync");
                if ((object)dictF != null)
                {
                    var dict = dictF.GetValue(mm) as IDictionary;
                    if (dict != null)
                    {
                        dict.Clear();
                        Log.LogInfo("[v26.6] mMapDataObjectToSync cleared on oracle.");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[v26.6] ClearMapDataObjects: {e.Message}"); }
        }

        /// <summary>Mono 2.x lacks FieldInfo/PropertyInfo op_Inequality — never use `fi != null`.</summary>
        private static bool RefOk(object o) => !ReferenceEquals(o, null);

        /// <summary>Re-enable sky weapon spawns and inFight on the oracle after each round.</summary>
        internal void RearmOracleCombatLoop(string reason)
        {
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) return;
                object gmInst = null;
                var instanceGetter = AccessTools.PropertyGetter(gmType, "Instance");
                if ((object)instanceGetter != null) gmInst = instanceGetter.Invoke(null, null);
                if ((object)gmInst == null) gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                if ((object)gmInst == null)
                {
                    Log.LogWarning($"[P6.5] RearmOracleCombatLoop({reason}): GameManager not found.");
                    return;
                }
                var inFightF = AccessTools.Field(gmType, "inFight");
                if ((object)inFightF != null) inFightF.SetValue(gmInst, true);
                var stillMenuF = AccessTools.Field(gmType, "stillInMenu");
                if ((object)stillMenuF != null) stillMenuF.SetValue(gmInst, false);
                var matchTimeF = AccessTools.Field(gmType, "matchTime");
                if ((object)matchTimeF != null) matchTimeF.SetValue(gmInst, 0f);
                var rwcField = AccessTools.Field(gmType, "randomWeaponCounter");
                if ((object)rwcField != null) rwcField.SetValue(gmInst, 2.0f);
                ScheduleNextSkyWeapon(OracleFirstSkyWeaponDelay);
                Log.LogInfo($"[P6.5] RearmOracleCombatLoop({reason}): inFight=true nextSkyWeapon in {OracleFirstSkyWeaponDelay:0.0}s");
                _skyWeaponTickAt = Time.realtimeSinceStartup + 1.5f;
            }
            catch (Exception e) { Log.LogWarning($"[P6.5] RearmOracleCombatLoop({reason}): {e.Message}"); }
        }

        internal void ScheduleNextSkyWeapon(float delaySec)
        {
            _oracleNextSkyWeaponAt = Time.realtimeSinceStartup + Mathf.Max(0.5f, delaySec);
        }

        /// <summary>
        /// Headless oracle: stock GameManager.Update resets randomWeaponCounter every frame
        /// (logs: rwc stuck at 2.0, no sky spawns after round 2). Use our own realtime timer.
        /// </summary>
        internal bool IsOraclePreCombatGraceActive()
        {
            return _oraclePreCombatReadyAt > 0f && Time.realtimeSinceStartup < _oraclePreCombatReadyAt;
        }

        internal void TickOracleCombatTimers()
        {
            if (!_matchStarted || !_batchModeHost) return;
            if (IsOraclePreCombatGraceActive()) return;
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if (!RefOk(gmType)) return;
                object gmInst = null;
                var ig = AccessTools.PropertyGetter(gmType, "Instance");
                if (RefOk(ig)) gmInst = ig.Invoke(null, null);
                if (!RefOk(gmInst)) gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                if (!RefOk(gmInst)) return;

                var inFightF = AccessTools.Field(gmType, "inFight");
                if (RefOk(inFightF) && !(bool)inFightF.GetValue(gmInst))
                    inFightF.SetValue(gmInst, true);

                var matchTimeF = AccessTools.Field(gmType, "matchTime");
                if (RefOk(matchTimeF))
                {
                    float mt = (float)matchTimeF.GetValue(gmInst);
                    if (mt < 1f) mt = 1f;
                    matchTimeF.SetValue(gmInst, mt + Time.deltaTime);
                }

                if (_oracleNextSkyWeaponAt < 0f)
                    ScheduleNextSkyWeapon(OracleFirstSkyWeaponDelay);
                float now = Time.realtimeSinceStartup;
                if (now < _oracleNextSkyWeaponAt) return;

                SpawnRandomWeaponPrefix(gmInst);
                _skyWeaponSpawnCount++;
                if (_skyWeaponSpawnCount <= 8 || _skyWeaponSpawnCount % 10 == 0)
                    Log.LogInfo($"[P6.5] CombatTimer: sky spawn #{_skyWeaponSpawnCount} (next in {OracleSkyWeaponIntervalMin:0}-{OracleSkyWeaponIntervalMax:0}s)");
                ScheduleNextSkyWeapon(UnityEngine.Random.Range(OracleSkyWeaponIntervalMin, OracleSkyWeaponIntervalMax));
            }
            catch (Exception e) { Log.LogWarning($"[P6.5] TickOracleCombatTimers: {e.Message}"); }
        }

        /// <summary>Legacy entry — combat timers now drive sky weapons every frame.</summary>
        internal void TickOracleSkyWeaponSpawner()
        {
            TickOracleCombatTimers();
        }

        /// <summary>
        /// Stock InitMapDataObjects() is empty — registration only happens in Awake.
        /// Headless often misses Awake; scan the loaded map scene (incl. inactive) and push state.
        /// </summary>
        internal int EnsureMapSyncObjectsRegistered(Scene sceneOverride = default(Scene), bool useSceneOverride = false)
        {
            int added = 0;
            try
            {
                EnsureMapReflection();
                if (!RefOk(_mapSyncBaseType)) return 0;
                var mm = GetMultiplayerManagerInstance();
                if (!RefOk(mm)) return 0;
                var mmType = mm.GetType();
                var add = AccessTools.Method(mmType, "AddMapDataObject");
                var syncMap = AccessTools.Method(mmType, "SyncMapData");
                if (!RefOk(add)) return 0;

                Scene scene = default(Scene);
                bool haveScene = useSceneOverride && sceneOverride.isLoaded;
                if (haveScene) scene = sceneOverride;
                else if (TryFindLoadedSceneForCurrentMapIndex(out scene)) haveScene = true;

                var seen = new HashSet<int>();
                void RegisterOne(Component comp)
                {
                    if (!RefOk(comp) || !RefOk(comp.gameObject)) return;
                    int id = comp.GetInstanceID();
                    if (!seen.Add(id)) return;
                    var t = comp.GetType();
                    var netF = AccessTools.Field(t, "m_NetworkControl");
                    if (RefOk(netF)) netF.SetValue(comp, true);
                    var mb = comp as MonoBehaviour;
                    if (RefOk(mb) && !mb.enabled) mb.enabled = true;
                    Vector2 sp = ReadMapSyncStartPos(comp);
                    var startF = AccessTools.Field(t, "m_StartPos");
                    if (RefOk(startF)) startF.SetValue(comp, sp);
                    UpsertMapDataObject(mm, add, sp, comp);
                    added++;
                    if (RefOk(syncMap))
                    {
                        try { syncMap.Invoke(mm, new object[] { comp }); } catch { }
                    }
                }

                if (haveScene)
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (!RefOk(root)) continue;
                        var comps = root.GetComponentsInChildren(_mapSyncBaseType, true);
                        if (comps != null)
                            foreach (var obj in comps)
                                if (obj is Component c) RegisterOne(c);
                    }
                }

                var all = Resources.FindObjectsOfTypeAll(_mapSyncBaseType);
                if (all != null)
                {
                    foreach (var obj in all)
                    {
                        var c = obj as Component;
                        if (!RefOk(c)) continue;
                        if (haveScene && c.gameObject.scene != scene) continue;
                        RegisterOne(c);
                    }
                }

                _mapSyncObjectsRegistered = added;
                Log.LogInfo($"[v26.6] EnsureMapSyncObjectsRegistered: {added} in scene={(haveScene ? scene.name : "?")} buildIndex={(haveScene ? scene.buildIndex : -1)} awake-hits={_mapAwakeRegisterCount}");
            }
            catch (Exception e) { Log.LogWarning($"[v26.6] EnsureMapSync: {e.Message}"); }
            return added;
        }

        internal void ScheduleMapSyncRetries()
        {
            _mapSyncRetryPass = 0;
            _mapSyncRetryAt = Time.realtimeSinceStartup + 2f;
        }

        private void TickMapSyncRetry()
        {
            if (_mapSyncRetryAt < 0f) return;
            if (Time.realtimeSinceStartup < _mapSyncRetryAt) return;
            _mapSyncRetryAt = -1f;
            Scene scene;
            TryFindLoadedSceneForCurrentMapIndex(out scene);
            int n = EnsureMapSyncObjectsRegistered(scene);
            Log.LogInfo($"[v26.6] MapSync retry pass {_mapSyncRetryPass}: registered={n}");
            _mapSyncRetryPass++;
            if (_mapSyncRetryPass < MapSyncRetryMaxPasses && n == 0)
                _mapSyncRetryAt = Time.realtimeSinceStartup + MapSyncRetryIntervalSec;
            else if (_mapSyncRetryPass < MapSyncRetryMaxPasses)
                _mapSyncRetryAt = Time.realtimeSinceStartup + MapSyncRetryIntervalSec;
        }

        private void EnsurePreSpawnedWeaponsRegistered()
        {
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if (!RefOk(mm)) return;
                var wpType = AccessTools.TypeByName("WeaponPickUp");
                if (!RefOk(wpType)) return;
                var add = AccessTools.Method(mm.GetType(), "AddPreSpawnedWeapon");
                if (!RefOk(add)) return;
                int n = 0;
                Scene scene;
                if (TryFindLoadedSceneForCurrentMapIndex(out scene) && scene.isLoaded)
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (!RefOk(root)) continue;
                        var all = root.GetComponentsInChildren(wpType, true);
                        if (all == null) continue;
                        foreach (var wp in all)
                        {
                            var comp = wp as Component;
                            if (!RefOk(comp)) continue;
                            var p = comp.transform.position;
                            add.Invoke(mm, new object[] { new Vector2(p.y, p.z), wp });
                            n++;
                        }
                    }
                }
                if (n == 0)
                {
                    var found = UnityEngine.Object.FindObjectsOfType(wpType);
                    if (found != null)
                        foreach (var wp in found)
                        {
                            var comp = wp as Component;
                            if (!RefOk(comp)) continue;
                            var p = comp.transform.position;
                            add.Invoke(mm, new object[] { new Vector2(p.y, p.z), wp });
                            n++;
                        }
                }
                if (n > 0) Log.LogInfo($"[v26.6] EnsurePreSpawnedWeaponsRegistered: {n} WeaponPickUp");
            }
            catch (Exception e) { Log.LogWarning($"[v26.6] EnsurePreSpawned: {e.Message}"); }
        }

        internal void InvokeCheckForGroundWeapons(string reason)
        {
            try
            {
                EnsurePreSpawnedWeaponsRegistered();
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                var check = AccessTools.Method(mm.GetType(), "CheckForGroundWeapons");
                if ((object)check == null) return;
                check.Invoke(mm, null);
                Log.LogInfo($"[P6.8/v26.6] CheckForGroundWeapons ({reason})");
            }
            catch (Exception e) { Log.LogError($"[P6.8] CheckForGroundWeapons ({reason}): {e.InnerException?.Message ?? e.Message}"); }
        }

        internal void CacheGroundWeaponsBroadcast(byte[] body)
        {
            if (body == null || body.Length < 2) return;
            _cachedGroundWeaponsBody = (byte[])body.Clone();
            _groundWeaponsEntryCount = body[0] | (body[1] << 8);
            Log.LogInfo($"[P6.8] Cached GroundWeaponsInit count={_groundWeaponsEntryCount} bytes={body.Length}");
        }

        /// <summary>
        /// Factory / conveyor maps: CheckForGroundWeapons may not hit SendBroadcast cache.
        /// Build GroundWeaponsInit from mTempPreSpawnedWeapons and push to clients.
        /// </summary>
        internal void TryBuildAndBroadcastGroundWeapons(string reason)
        {
            try
            {
                if (_cachedGroundWeaponsBody != null && _cachedGroundWeaponsBody.Length >= 2
                    && _groundWeaponsEntryCount > 0)
                {
                    BroadcastGroundWeaponsToAllClients();
                    return;
                }
                var mm = GetMultiplayerManagerInstance();
                if (!RefOk(mm)) return;
                var mmType = mm.GetType();
                var tempF = AccessTools.Field(mmType, "mTempPreSpawnedWeapons");
                if (!RefOk(tempF)) return;
                var temp = tempF.GetValue(mm) as IDictionary;
                if (temp == null || temp.Count == 0)
                {
                    EnsurePreSpawnedWeaponsRegistered();
                    temp = tempF.GetValue(mm) as IDictionary;
                }
                if (temp == null || temp.Count == 0)
                {
                    Log.LogInfo($"[P6.8] TryBuildGroundWeapons({reason}): no pre-spawned weapons in scene.");
                    return;
                }
                int n = temp.Count;
                byte[] body = new byte[2 + 12 * n];
                int o = 0;
                WriteU16LE(body, o, (ushort)n);
                o += 2;
                var getWid = AccessTools.Method(mmType, "GetNextWeaponSpawnID");
                var getSid = AccessTools.Method(mmType, "GetNextSyncableObjectSpawnID", new[] { typeof(bool) });
                foreach (DictionaryEntry entry in temp)
                {
                    Vector2 key = (Vector2)entry.Key;
                    WriteF32LE(body, o, key.x); o += 4;
                    WriteF32LE(body, o, key.y); o += 4;
                    ushort wid = 0, sid = 0;
                    if (RefOk(getWid)) wid = (ushort)(int)getWid.Invoke(mm, null);
                    if (RefOk(getSid)) sid = (ushort)(int)getSid.Invoke(mm, new object[] { true });
                    WriteU16LE(body, o, wid); o += 2;
                    WriteU16LE(body, o, sid); o += 2;
                }
                CacheGroundWeaponsBroadcast(body);
                BroadcastGroundWeaponsToAllClients();
                Log.LogInfo($"[P6.8] TryBuildGroundWeapons({reason}): built+broadcast {n} entries.");
            }
            catch (Exception e) { Log.LogWarning($"[P6.8] TryBuildGroundWeapons({reason}): {e.Message}"); }
        }

        internal void FlushGroundWeaponsAfterCheck(string reason)
        {
            InvokeCheckForGroundWeapons(reason);
            if (_cachedGroundWeaponsBody == null || _groundWeaponsEntryCount <= 0)
                TryBuildAndBroadcastGroundWeapons(reason + "-fallback");
            else
                BroadcastGroundWeaponsToAllClients();
        }

        private void SendCachedGroundWeaponsToClient(SfClient cli)
        {
            if (_cachedGroundWeaponsBody == null || _cachedGroundWeaponsBody.Length < 2) return;
            if (!cli.Initialized) return;
            SendSfPacket(cli.Addr, PktGroundWeaponsInit, _cachedGroundWeaponsBody, 0uL, 0);
            Log.LogInfo($"[P6.8] Resent GroundWeaponsInit to slot={cli.Slot} count={_groundWeaponsEntryCount}");
        }

        private void BroadcastGroundWeaponsToAllClients()
        {
            if (_cachedGroundWeaponsBody == null || _cachedGroundWeaponsBody.Length < 2) return;
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktGroundWeaponsInit, _cachedGroundWeaponsBody, 0uL, 0);
                sent++;
            }
            if (sent > 0)
                Log.LogInfo($"[P6.8] Broadcast GroundWeaponsInit to {sent} client(s), entries={_groundWeaponsEntryCount}");
        }

        /// <summary>
        /// Runs on the ORACLE after additive map scene settles. Clients already got MapChange;
        /// this wires server-side map objects (weapons in geometry, GhostPlatform, barrels, NSOs).
        /// </summary>
        internal void RunPostMapLoadServerInit(Scene scene)
        {
            try
            {
                if (scene.name == "MainScene" || string.IsNullOrEmpty(scene.name)) return;
                if (scene.buildIndex != _currentSceneIndex)
                {
                    Log.LogWarning($"[v26.6] PostMapLoad skip stale scene buildIndex={scene.buildIndex} expected={_currentSceneIndex} name='{scene.name}'");
                    return;
                }
                Log.LogInfo($"[v26.6] PostMapLoad init scene='{scene.name}' buildIndex={scene.buildIndex} matchScene={_currentSceneIndex}");
                ClearMapDataObjectsOnOracle();
                _mapAwakeRegisterCount = 0;
                EnsureOracleP2PNetworkReady("PostMapLoad");
                SetMapSyncNetworkControlGlobal(true);
                HoldOracleOutOfFight("PostMapLoad-grace");
                try { InvokeMultiplayerManagerInitChain(); } catch { }
                EnsureMapSyncObjectsRegistered(scene, true);
                ScheduleMapSyncRetries();
                SuppressScheduledOracleCountDown("PostMapLoad-grace");
                LogMapTerrainProfile(scene);
                ScheduleOraclePreCombatStart(scene);
                // Mirror MarkSceneNsosMovedAfterSettle so NSO keepalive dicts
                // get repopulated even when OnAnySceneLoadedRunSettle early-returned
                // (e.g., stale buildIndex gate triggered, force-complete-via-timeout
                // path). Without this, Bug B's NSO snapshot collapse can still
                // happen for scenes loaded via the force-complete fallback.
                // Companion to commit a70c5b3 (keepalive dict preservation).
                try { MarkSceneNsosMovedAfterSettle(); }
                catch (Exception e) { Log.LogWarning($"[v26.6] PostMapLoad MarkSceneNsos: {e.Message}"); }
                try
                {
                    // Unity 5.6 has no Physics.SyncTransforms — wake rigidbodies so colliders refresh.
                    var rbs = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
                    int woken = 0;
                    if (rbs != null)
                        foreach (var rb in rbs)
                            if ((object)rb != null && !rb.isKinematic) { rb.WakeUp(); woken++; }
                    Log.LogInfo($"[v26.6] PostMapLoad collider refresh: woke {woken} dynamic rigidbody(s).");
                }
                catch (Exception e) { Log.LogWarning($"[v26.6] PostMapLoad collider refresh: {e.Message}"); }
                ScheduleAuthRigRespawnAfterMapLoad("PostMapLoad");
            }
            finally
            {
                FinishOracleMapLoad("PostMapLoad");
            }
        }

        /// <summary>Open-B: re-chain NSO inventory → auth rig spawn after every map load / round advance.</summary>
        internal void ScheduleAuthRigRespawnAfterMapLoad(string reason)
        {
            if (_authSpawnDone) return;
            _nsoInventoryDone = false;
            _nsoInventoryAt = Time.realtimeSinceStartup + 0.25f;
            Log.LogInfo($"[Open-B] Scheduled NSO inventory + auth rig respawn ({reason}).");
        }

        internal void ScheduleMapInfoBootstrapRetries()
        {
            _mapInfoBootstrapAt = Time.realtimeSinceStartup + MapInfoBootstrapRetryIntervalSec;
        }

        internal void ScheduleOraclePreCombatStart(Scene scene)
        {
            _oraclePreCombatSceneIndex = scene.buildIndex;
            _oraclePreCombatReadyAt = Time.realtimeSinceStartup + OraclePreCombatGraceSec;
            Log.LogInfo($"[v0.3.4] Pre-combat grace {OraclePreCombatGraceSec:0.0}s — no weapons/countdown/MapInfo until then (scene={scene.name} idx={scene.buildIndex}).");
        }

        internal void TickOraclePreCombatGrace()
        {
            if (_oraclePreCombatReadyAt < 0f) return;
            if (Time.realtimeSinceStartup < _oraclePreCombatReadyAt) return;
            _oraclePreCombatReadyAt = -1f;
            Scene scene;
            if (!TryFindLoadedSceneForCurrentMapIndex(out scene) || scene.buildIndex != _oraclePreCombatSceneIndex)
            {
                Log.LogWarning($"[v0.3.4] Pre-combat grace fired but scene mismatch (wanted idx={_oraclePreCombatSceneIndex}).");
                if (TryFindLoadedSceneForCurrentMapIndex(out scene))
                    RunOraclePreCombatStart(scene, "grace-fallback");
                return;
            }
            RunOraclePreCombatStart(scene, "grace-complete");
        }

        /// <summary>After grace: weapons, synced map anims (lava/factory/xmas), then countdown/combat.</summary>
        internal void RunOraclePreCombatStart(Scene scene, string reason)
        {
            if (!scene.isLoaded) return;
            Log.LogInfo($"[v0.3.4] Pre-combat start ({reason}) scene='{scene.name}' idx={scene.buildIndex}");
            EnsureMapSyncObjectsRegistered(scene, true);
            FlushGroundWeaponsAfterCheck("pre-combat");
            foreach (var kv in _sfClients)
                if (kv.Value.Spawned) SendCachedGroundWeaponsToClient(kv.Value);
            _groundWeaponsRetryAt = Time.realtimeSinceStartup + 5f;
            _groundWeaponsRetryPass = 0;
            SuppressScheduledOracleCountDown("pre-combat");
            try { InvokeOracleStartCountDown(); } catch (Exception e) { Log.LogWarning($"[P6.5] Pre-combat StartCountDown: {e.Message}"); }
            RearmOracleCombatLoop("pre-combat");
            _mapInfoBootstrapPass = 0;
            _mapInfoLastBroadcastCount = BootstrapOracleMapInfoBroadcast(scene, "pre-combat");
            if (_mapInfoLastBroadcastCount == 0)
                ScheduleMapInfoBootstrapRetries();
            RestartSyncedCodeAnimationsInScene(scene);
            try { InvokeOracleBossMapSetup(); } catch (Exception e) { Log.LogWarning($"[P6.5] Pre-combat boss setup: {e.Message}"); }
        }

        internal static void HoldOracleOutOfFight(string reason)
        {
            if (!_batchModeHost) return;
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if (!RefOk(gmType)) return;
                object gmInst = null;
                var ig = AccessTools.PropertyGetter(gmType, "Instance");
                if (RefOk(ig)) gmInst = ig.Invoke(null, null);
                if (!RefOk(gmInst)) gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                if (!RefOk(gmInst)) return;
                var inFightF = AccessTools.Field(gmType, "inFight");
                if (RefOk(inFightF)) inFightF.SetValue(gmInst, false);
                var rwcF = AccessTools.Field(gmType, "randomWeaponCounter");
                if (RefOk(rwcF)) rwcF.SetValue(gmInst, 99f);
                Log.LogInfo($"[v0.3.4] HoldOracleOutOfFight({reason}): inFight=false (grace window).");
            }
            catch (Exception e) { Log.LogWarning($"[v0.3.4] HoldOracleOutOfFight: {e.Message}"); }
        }

        private struct MapTerrainProfile
        {
            public int CodeAnimTotal, CodeAnimSync, CodeAnimLocal;
            public int EnablePerPlayer, MapSyncTotal, Ghost, MovePath, Pillar, MapSyncOther;
        }

        internal void LogMapTerrainProfile(Scene scene)
        {
            var p = CollectMapTerrainProfile(scene);
            Log.LogInfo($"[v0.3.4] Map profile '{scene.name}' idx={scene.buildIndex}: " +
                $"codeAnim sync={p.CodeAnimSync} local={p.CodeAnimLocal} enablePerPlayer={p.EnablePerPlayer} " +
                $"mapSync={p.MapSyncTotal} (ghost={p.Ghost} move={p.MovePath} pillar={p.Pillar} other={p.MapSyncOther})");
        }

        private MapTerrainProfile CollectMapTerrainProfile(Scene scene)
        {
            var p = new MapTerrainProfile();
            if (!scene.isLoaded) return p;
            var codeType = AccessTools.TypeByName("CodeAnimation");
            var eoppType = AccessTools.TypeByName("EnableObjectsPerPlayer");
            EnsureMapReflection();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!RefOk(root)) continue;
                if (RefOk(codeType))
                {
                    var anims = root.GetComponentsInChildren(codeType, true);
                    if (anims != null)
                        foreach (var o in anims)
                        {
                            p.CodeAnimTotal++;
                            var mb = o as MonoBehaviour;
                            if (!RefOk(mb)) continue;
                            var shallF = AccessTools.Field(codeType, "m_ShallSync");
                            bool sync = RefOk(shallF) && (bool)shallF.GetValue(mb);
                            if (sync) p.CodeAnimSync++; else p.CodeAnimLocal++;
                        }
                }
                if (RefOk(eoppType))
                {
                    var eopps = root.GetComponentsInChildren(eoppType, true);
                    if (eopps != null) p.EnablePerPlayer += eopps.Length;
                }
                if (RefOk(_mapSyncBaseType))
                {
                    var syncs = root.GetComponentsInChildren(_mapSyncBaseType, true);
                    if (syncs != null)
                        foreach (var o in syncs)
                        {
                            p.MapSyncTotal++;
                            var c = o as Component;
                            if (!RefOk(c)) continue;
                            string tn = c.GetType().Name;
                            if (tn.IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0) p.Ghost++;
                            else if (tn.IndexOf("MoveAlong", StringComparison.OrdinalIgnoreCase) >= 0) p.MovePath++;
                            else if (tn.IndexOf("Pillar", StringComparison.OrdinalIgnoreCase) >= 0) p.Pillar++;
                            else p.MapSyncOther++;
                        }
                }
            }
            return p;
        }

        private static void RestartSyncedCodeAnimationsInScene(Scene scene)
        {
            var codeType = AccessTools.TypeByName("CodeAnimation");
            if (!RefOk(codeType)) return;
            int restarted = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!RefOk(root)) continue;
                var anims = root.GetComponentsInChildren(codeType, true);
                if (anims == null) continue;
                foreach (var o in anims)
                {
                    var mb = o as MonoBehaviour;
                    if (!RefOk(mb)) continue;
                    var shallF = AccessTools.Field(codeType, "m_ShallSync");
                    if (RefOk(shallF) && !(bool)shallF.GetValue(mb)) continue;
                    try
                    {
                        var play = AccessTools.Method(codeType, "Play");
                        if (RefOk(play)) { play.Invoke(mb, null); restarted++; }
                    }
                    catch { }
                }
            }
            if (restarted > 0)
                Log.LogInfo($"[v0.3.4] Restarted {restarted} synced CodeAnimation(s) on server (lava/conveyor/etc).");
        }

        internal void TickOracleMapInfoBootstrap()
        {
            if (_mapInfoBootstrapAt < 0f) return;
            if (Time.realtimeSinceStartup < _mapInfoBootstrapAt) return;
            _mapInfoBootstrapAt = -1f;
            Scene scene;
            if (!TryFindLoadedSceneForCurrentMapIndex(out scene))
            {
                if (_mapInfoBootstrapPass < MapInfoBootstrapMaxPasses)
                    ScheduleMapInfoBootstrapRetries();
                _mapInfoBootstrapPass++;
                return;
            }
            int n = BootstrapOracleMapInfoBroadcast(scene, "retry-" + _mapInfoBootstrapPass);
            Log.LogInfo($"[v0.3.3] MapInfo bootstrap retry pass {_mapInfoBootstrapPass}: broadcasts={n}");
            _mapInfoLastBroadcastCount = n;
            _mapInfoBootstrapPass++;
            if (_mapInfoBootstrapPass < MapInfoBootstrapMaxPasses && n == 0)
                ScheduleMapInfoBootstrapRetries();
        }

        /// <summary>
        /// MapInfo (msg 32): synced CodeAnimation (lava, factory belts, xmas props), EnableObjectsPerPlayer.
        /// MapInfoSync (33) + v26 snapshot: GhostPlatform, MoveAlongPath, PillarHandler — not sent here.
        /// </summary>
        internal int BootstrapOracleMapInfoBroadcast(Scene scene, string reason)
        {
            if (!_batchModeHost || !scene.isLoaded) return 0;
            EnsureOracleP2PNetworkReady("mapinfo-" + reason);
            SetMapSyncNetworkControlGlobal(true);
            int sent = 0;
            int codeAnim = 0, eopp = 0;
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if (!RefOk(mm)) return 0;
                var sendMapInfo = AccessTools.Method(mm.GetType(), "SendMapInfo");
                if (!RefOk(sendMapInfo)) return 0;
                var seenGo = new HashSet<int>();

                foreach (var root in scene.GetRootGameObjects())
                {
                    if (!RefOk(root)) continue;
                    foreach (var mb in BuildMapInfoProviderList(root))
                    {
                        if (!RefOk(mb) || !seenGo.Add(mb.gameObject.GetInstanceID())) continue;
                        byte[] payload = TryBuildMapInfoPayload(mb);
                        if (payload == null || payload.Length == 0) continue;
                        try
                        {
                            sendMapInfo.Invoke(mm, new object[] { payload });
                            if ((object)Instance != null)
                                Instance.ForwardBroadcastToV25Clients(32, payload, 0, 0);
                            sent++;
                            if (mb.GetType().Name == "CodeAnimation") codeAnim++;
                            else if (mb.GetType().Name == "EnableObjectsPerPlayer") eopp++;
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning($"[v0.3.4] MapInfo send failed on {mb.gameObject.name}: {ex.Message}");
                        }
                    }
                }
                Log.LogInfo($"[v0.3.4] MapInfo ({reason}) scene='{scene.name}' idx={scene.buildIndex} sent={sent} (codeAnim={codeAnim} enablePerPlayer={eopp})");
            }
            catch (Exception e) { Log.LogWarning($"[v0.3.4] BootstrapOracleMapInfoBroadcast: {e.Message}"); }
            return sent;
        }

        // NOTE: must be eager List<T>, NOT an IEnumerable<T> iterator with `yield return`.
        // The C# 9 compiler lowers `IEnumerable<T>` iterators into a state-machine class
        // whose GetEnumerator() references Environment.CurrentManagedThreadId — a .NET 4.5+
        // property that does NOT exist on Mono 2.0.50727 (Unity 5.6.3). The call throws
        // MissingMethodException, the outer catch swallows it, and MapInfo broadcasts
        // silently fail for scripted maps (lava, factory belts, xmas animations).
        // See: notes/bug-investigations/2026-05-24_v0.3.4-session-bugs.md (Bug A).
        // IEnumerator (non-generic Unity coroutine) is SAFE — only IEnumerable<T> is
        // poisonous on Mono 2.0.
        private static List<MonoBehaviour> BuildMapInfoProviderList(GameObject root)
        {
            var result = new List<MonoBehaviour>();
            var codeType = AccessTools.TypeByName("CodeAnimation");
            var eoppType = AccessTools.TypeByName("EnableObjectsPerPlayer");
            if (RefOk(codeType))
            {
                var anims = root.GetComponentsInChildren(codeType, true);
                if (anims != null)
                    foreach (var o in anims)
                        if (o is MonoBehaviour mb) result.Add(mb);
            }
            if (RefOk(eoppType))
            {
                var eopps = root.GetComponentsInChildren(eoppType, true);
                if (eopps != null)
                    foreach (var o in eopps)
                        if (o is MonoBehaviour mb) result.Add(mb);
            }
            return result;
        }

        private static byte[] TryBuildMapInfoPayload(MonoBehaviour mb)
        {
            if (!RefOk(mb)) return null;
            var t = mb.GetType();
            if (t.Name == "CodeAnimation")
            {
                var shallSyncF = AccessTools.Field(t, "m_ShallSync");
                if (RefOk(shallSyncF) && !(bool)shallSyncF.GetValue(mb)) return null;
                var durationF = AccessTools.Field(t, "duration");
                float duration = RefOk(durationF) ? (float)durationF.GetValue(mb) : 1f;
                var rv = mb.GetComponent(AccessTools.TypeByName("RandomValue"));
                if (RefOk(rv))
                {
                    var valF = AccessTools.Field(rv.GetType(), "value");
                    if (RefOk(valF)) duration *= (float)valF.GetValue(rv);
                }
                var addRandF = AccessTools.Field(t, "aditionalRandomDuration");
                if (RefOk(addRandF))
                    duration += UnityEngine.Random.Range(0f, (float)addRandF.GetValue(mb));
                byte nameLen = (byte)mb.gameObject.name.Length;
                if (nameLen == 0) return null;
                byte[] array = new byte[4 + nameLen];
                using (var output = new MemoryStream(array))
                using (var bw = new BinaryWriter(output))
                {
                    bw.Write(nameLen);
                    bw.Write(duration);
                }
                return array;
            }
            if (t.Name == "EnableObjectsPerPlayer")
            {
                var objectsF = AccessTools.Field(t, "objects");
                if (!RefOk(objectsF)) return null;
                var objects = objectsF.GetValue(mb) as GameObject[];
                if (objects == null || objects.Length == 0) return null;
                byte nameLen = (byte)mb.gameObject.name.Length;
                byte[] array = new byte[4] { nameLen, 0, 0, 0 };
                for (int j = 0; j < 3; j++)
                    array[j + 1] = (byte)UnityEngine.Random.Range(0, objects.Length);
                return array;
            }
            return null;
        }

        internal void FinishOracleMapLoad(string reason)
        {
            _oracleMapLoadInProgress = false;
            _oracleMapLoadForceCompleteAt = -1f;
            _oracleMapLoadStartedAt = -1f;
            Log.LogInfo($"[v26.6] Oracle map load finished ({reason}) scene={_currentSceneIndex}");
            FlushQueuedRoundAdvanceAfterMapLoad("map-load-finished");
        }

        /// <summary>When SceneManager does not re-fire loaded (same map reload), still complete init.</summary>
        internal void ForceCompleteOracleMapLoadIfNeeded(string reason)
        {
            if (!_oracleMapLoadInProgress) return;
            Scene scene;
            if (!TryFindLoadedSceneForCurrentMapIndex(out scene))
            {
                Log.LogWarning($"[v26.6] ForceCompleteMapLoad({reason}): no scene for index {_currentSceneIndex}");
                FinishOracleMapLoad(reason + "-no-scene");
                return;
            }
            Log.LogInfo($"[v26.6] ForceCompleteMapLoad({reason}) scene='{scene.name}' buildIndex={scene.buildIndex}");
            RunPostMapLoadServerInit(scene);
        }

        private bool TryFindLoadedSceneForCurrentMapIndex(out Scene scene)
        {
            scene = default(Scene);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded || s.name == "MainScene") continue;
                if (s.buildIndex == _currentSceneIndex)
                {
                    scene = s;
                    return true;
                }
            }
            return false;
        }

        internal bool IsOracleMapLoadInProgress() => _oracleMapLoadInProgress;

        internal void TickOracleMapLoadTimeout()
        {
            if (!_oracleMapLoadInProgress) return;
            float now = Time.realtimeSinceStartup;
            if (_oracleMapLoadForceCompleteAt > 0f && now >= _oracleMapLoadForceCompleteAt)
            {
                Log.LogWarning($"[v26.6] Map load force-complete after {OracleMapLoadForceCompleteSec:0.0}s (scene={_currentSceneIndex})");
                ForceCompleteOracleMapLoadIfNeeded("timeout");
            }
        }

        internal void ScheduleOracleReloadCurrentMap(string reason)
        {
            _oracleMapLoadInProgress = true;
            _oracleMapLoadStartedAt = Time.realtimeSinceStartup;
            _oracleMapLoadForceCompleteAt = Time.realtimeSinceStartup + OracleMapLoadForceCompleteSec;
            _mapAwakeRegisterCount = 0;
            _sceneLoadRealtime = Time.realtimeSinceStartup;
            _oracleStartMatchAt = Time.realtimeSinceStartup + 0.5f;
            _oracleStartMatchFired = false;
            _oracleCountDownAt = -1f;
            _oracleCountDownFired = false;
            _nsoInventoryAt = -1f;
            _nsoInventoryDone = false;
            _groundWeaponsRetryAt = -1f;
            _oraclePreCombatReadyAt = -1f;
            _groundWeaponsRetryPass = 0;
            Log.LogInfo($"[v26.6] Oracle will load additive scene {_currentSceneIndex} ({reason})");
        }

        private void TickGroundWeaponsRetry()
        {
            if (_groundWeaponsRetryAt < 0f) return;
            if (Time.realtimeSinceStartup < _groundWeaponsRetryAt) return;
            _groundWeaponsRetryAt = -1f;
            FlushGroundWeaponsAfterCheck("post-match-retry-pass" + _groundWeaponsRetryPass);
            _groundWeaponsRetryPass++;
            if (_groundWeaponsRetryPass < 3)
                _groundWeaponsRetryAt = Time.realtimeSinceStartup + 5f;
        }

        private void LogMapSyncDiagnostics(int posCount, int stateCount)
        {
            if (_mapSyncDiagNextAt < 0f) _mapSyncDiagNextAt = Time.realtimeSinceStartup + 5f;
            if (Time.realtimeSinceStartup < _mapSyncDiagNextAt) return;
            _mapSyncDiagNextAt = Time.realtimeSinceStartup + MapSyncLogIntervalSec;
            int ghost = 0, move = 0, pillar = 0, other = 0;
            foreach (var comp in EnumerateMapSyncComponents())
            {
                if ((object)comp == null) continue;
                string tn = comp.GetType().Name;
                if (tn.IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0) ghost++;
                else if (tn.IndexOf("MoveAlong", StringComparison.OrdinalIgnoreCase) >= 0) move++;
                else if (tn.IndexOf("Pillar", StringComparison.OrdinalIgnoreCase) >= 0) pillar++;
                else other++;
            }
            Log.LogInfo($"[v26.6] mapSync pos={posCount} state={stateCount} types ghost={ghost} move={move} pillar={pillar} other={other} registered={_mapSyncObjectsRegistered}");
        }

        private List<Component> EnumerateMapSyncComponents()
        {
            var list = new List<Component>();
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm != null)
                {
                    var dictF = AccessTools.Field(mm.GetType(), "mMapDataObjectToSync");
                    IDictionary dict = null;
                    if ((object)dictF != null) dict = dictF.GetValue(mm) as IDictionary;
                    if (dict != null)
                    {
                        foreach (DictionaryEntry e in dict)
                        {
                            var c = e.Value as Component;
                            if ((object)c != null) list.Add(c);
                        }
                    }
                }
                if (list.Count == 0 && (object)_mapSyncBaseType != null)
                {
                    var all = UnityEngine.Object.FindObjectsOfType(_mapSyncBaseType);
                    if (all != null)
                        foreach (var o in all)
                            if (o is Component c) list.Add(c);
                }
            }
            catch (Exception ex) { Log.LogWarning($"[v26.6] EnumerateMapSync: {ex.Message}"); }
            return list;
        }

        private void EnsureMapReflection()
        {
            if ((object)_mapSyncBaseType == null)
            {
                _mapSyncBaseType = AccessTools.TypeByName("MapInfoSyncableBase");
                if ((object)_mapSyncBaseType != null)
                {
                    _mapSyncStartPosField = AccessTools.Field(_mapSyncBaseType, "m_StartPos");
                    _mapGetDataMethod = AccessTools.Method(_mapSyncBaseType, "GetData");
                    _mapSetDataMethod = AccessTools.Method(_mapSyncBaseType, "SetData");
                    _mapNetworkControlField = AccessTools.Field(_mapSyncBaseType, "m_NetworkControl");
                }
            }
        }

        private Vector2 ReadMapSyncStartPos(Component comp)
        {
            var p = comp.transform.position;
            Vector2 sp = new Vector2(p.y, p.z);
            try
            {
                EnsureMapReflection();
                if (RefOk(_mapSyncStartPosField))
                    sp = (Vector2)_mapSyncStartPosField.GetValue(comp);
            }
            catch { }
            return QuantizeMapSyncKey(sp);
        }

        private List<MapStateSnap> CollectMapStateSnapshot()
        {
            var result = new List<MapStateSnap>();
            EnsureMapReflection();
            if (!RefOk(_mapGetDataMethod)) return result;
            foreach (var comp in EnumerateMapSyncComponents())
            {
                if (!RefOk(comp)) continue;
                try
                {
                    byte[] data = _mapGetDataMethod.Invoke(comp, null) as byte[];
                    if (data == null) data = new byte[0];
                    if (data.Length > MapStateMaxPayload)
                    {
                        var t = new byte[MapStateMaxPayload];
                        Buffer.BlockCopy(data, 0, t, 0, MapStateMaxPayload);
                        data = t;
                    }
                    Vector2 sp = ReadMapSyncStartPos(comp);
                    result.Add(new MapStateSnap { StartX = sp.x, StartY = sp.y, Data = data });
                }
                catch (Exception ex)
                {
                    if (result.Count == 0)
                        Log.LogWarning($"[v26.6 mapState collect] {ex.Message}");
                }
            }
            return result;
        }

        private static int MapStateSectionByteLen(List<MapStateSnap> entries)
        {
            int n = 2;
            if (entries == null) return n;
            foreach (var e in entries)
                n += 8 + 1 + (e.Data?.Length ?? 0);
            return n;
        }

        private static int WriteMapStateSection(byte[] body, int off, List<MapStateSnap> entries)
        {
            ushort count = (ushort)(entries?.Count ?? 0);
            WriteU16LE(body, off, count);
            off += 2;
            if (entries == null) return off;
            foreach (var e in entries)
            {
                WriteF32LE(body, off, e.StartX); off += 4;
                WriteF32LE(body, off, e.StartY); off += 4;
                byte len = (byte)Math.Min(MapStateMaxPayload, e.Data?.Length ?? 0);
                body[off++] = len;
                if (len > 0 && e.Data != null)
                    Buffer.BlockCopy(e.Data, 0, body, off, len);
                off += len;
            }
            return off;
        }

        private List<MapSyncSnap> CollectMapSyncSnapshot()
        {
            var result = new List<MapSyncSnap>();
            EnsureMapReflection();
            foreach (var comp in EnumerateMapSyncComponents())
            {
                if (!RefOk(comp)) continue;
                try
                {
                    var p = comp.transform.position;
                    Vector2 startPos = ReadMapSyncStartPos(comp);
                    result.Add(new MapSyncSnap
                    {
                        StartX = startPos.x, StartY = startPos.y,
                        X = p.x, Y = p.y, Z = p.z
                    });
                }
                catch (Exception ex)
                {
                    if (result.Count == 0)
                        Log.LogWarning($"[P0-14 mapSync collect] {ex.Message}");
                }
            }
            return result;
        }
    }
}
