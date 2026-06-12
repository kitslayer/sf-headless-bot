using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFHeadlessHost
{
    // SFHeadlessHost — turns a headless Stick Fight instance into a UDP
    // dedicated server. Loaded by BepInEx on the oracle (`-batchmode`) and
    // also on each player's SF install (interactive mode, where only the
    // CLIENT-MODE SHIM runs).
    //
    // ============================================================
    //                    TABLE OF CONTENTS
    // ============================================================
    //  Anchor (search this line)                          ~Line
    //  ─────────────────────────────────────────────────  ─────
    //  private void Awake()                               ~  56   bootstrap, mode detect
    //  Phase 6.5 Step 1 — IsServer=true                   ~ 397   Harmony postfixes to fake host mode
    //  SendBroadcastPrefix                                ~ 524   intercepts host-side broadcasts → forwards
    //                                                              over our v25 UDP socket (P0-11 lives here)
    //  Phase 6.5 Step 2 — invoke GameManager.StartMatch   ~ 724
    //  InvokeMultiplayerManagerInitChain                  ~ 776   manual init chain after scene load
    //  NSO inventory + diagnostics                        ~ 815
    //  === CLIENT-MODE SHIM ===                           ~1137   patches applied on player-side SF
    //  P0-12 — MapInfoSync Vector2 quantize               ~1149   prefix patches (server + client)
    //  InstallClientModePatches                           ~1247   dynamic NSO patch (skip DisableAllRigidBodies)
    //  Bridge: UDP socket                                 ~1674   the v25 raw socket
    //  v26 extension constants (msgTypes 39/40/41)        ~1735
    //  Patched-DLL extensions (msgTypes 56/57)            ~1743
    //  SfDispatch                                         ~1917   inbound packet router
    //  ValidateDamagePacket (P1-8 lives here)             ~2140   anticheat damage validation
    //  Chat / admin                                       ~2224   /code /ping /start /tickrate /help…
    //  RateGuard / AnticheatObserve                       ~2356
    //  Pickup / Drop / Throw handlers                     ~2558
    //  Handshake handlers                                 ~2626   ClientRequestingAccepting → Spawned
    //  HandlePlayerInput (v26)                            ~2933   inbound PktPlayerInput; P0-13 keyframe send here
    //  HandlePlayerUpdate                                 ~3022   v25 client position relay
    //  Phase 6.9 — auth rig spawn + ghost rig update      ~3046
    //  Phase 6.10 — server-authoritative snapshots        ~3214   v26 WorldStateSnapshot broadcast
    //  Phase 6.17 v0.1+v0.2 — projectile sim + hit reg    ~3240
    //  EmitServerDamage                                   ~3369
    //  Tick-history ring buffer (lag-comp)                ~3388
    //  BroadcastWorldStateSnapshot                        ~3443   v26.5 (players + NSOs + projs + mapSync)
    //  CollectAllNsoSnapshot + SendKeyframeSnapshot       ~3556   P0-13 first-snap-on-late-join
    //  P0-14 MapInfoSyncableBase position broadcast       ~3553+  CollectMapSyncSnapshot, etc.
    //  CollectActiveNsoSnapshot                           ~3690+
    //  ApplyClientObjectUpdate (legacy)                   ~3850
    //  ReadEnv                                            ~4530
    // ============================================================
    //
    // Architecture: see ../notes/ARCHITECTURE.md
    // Wire protocol: see ../notes/PROTOCOL.md
    // Object sync model: see ../notes/OBJECT_SYNC.md
    // Open bugs: see ../notes/BUGS_BACKLOG.md (P0-11..P0-15, P1-8)
    //
    // Configuration via env vars (read once at Awake):
    //   SFHEADLESS_PORT       — v25 UDP bind port (default 1337).
    //   SFHEADLESS_BRIDGEPORT — internal bridge port (default 1341).
    //   SFHEADLESS_SCENE      — initial scene index (default 0 = lobby).
    //   SFHEADLESS_DEBUG      — "1" enables verbose tick logging.
    //   SF_ROUND_END_DELAY    — seconds before MapChange after a kill (default 0.5).
    //   SF_NEXT_MATCH_DELAY   — seconds before StartMatch after MapChange (default 2.0).
    //   SF_PRE_COMBAT_DELAY   — seconds after map load before weapons/countdown/MapInfo (default 3.0).
    //   SF_ANTICHEAT_ENFORCE  — "1" turns anticheat into drop-mode (default observe-only).
    //   SF_LOBBY_CODE         — 4-char lobby code returned by /code chat command.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public partial class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.headless-host";
        public const string PluginName = "SFHeadlessHost";
        public const string PluginVersion = "0.3.10";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        internal static int BindPort = 1340;     // Game-traffic port (Lidgren)
        internal static int BridgePort = 1341;   // State-bridge port (this plugin)
        internal static int InitialScene = 0; // 0 = lobby (boots ControllerHandler + GameManager DontDestroyOnLoad infrastructure)
        internal static bool Verbose;
        // Round pacing. Stock SF fires ChangeMap instantly when last player dies
        // (KillPlayer in GameManager.cs). The 0.5s default here gives clients a
        // beat to render the death animation before the map-swoosh starts.
        // SF_ROUND_END_DELAY env var override.
        internal static float RoundEndDelaySec = 0.5f;
        // Time between MapChange broadcast and StartMatch broadcast — must be
        // long enough for clients to load the scene and respawn. Stock SF's
        // k_MAX_SECONDS_UNTIL_AUTO_START is 3s. SF_NEXT_MATCH_DELAY env override.
        internal static float NextMatchDelaySec = 2.0f;
        // Minimum seconds on a map before another kill can advance the round (stops double MapChange / skip).
        internal static float RoundMinPlaySec = 0f;
        // N× wall-speed for headless training (SF_TIMESCALE env, 1–5; 1 = stock).
        // Asserted in LateUpdate only when the game itself intends normal speed.
        internal static float TrainTimeScale = 1f;
        // Stage HP (SF_STAGE_HP env, 1-100; default 100 = DSF comp). Stock
        // match option; curriculum shrinks the kill task at low stages.
        internal static int StageHP = 100;
        private float _roundAdvanceBlockedUntil = -1f;
        private bool _roundAdvanceQueuedAfterMapLoad;
        private readonly HashSet<int> _deathSlotsHandled = new HashSet<int>();
        private float _authDeathCheckAt = -1f;
        private float _pendingClientStartMatchAt = -1f;
        private bool _pendingClientStartMatchFired;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Phase 6.22 — per-lobby plugin log file. The shared BepInEx
            // LogOutput.log gets trampled when multiple oracles run from the
            // same install (last-writer-wins). Set SFHEADLESS_LOGFILE to a
            // unique path per oracle so each gets its own tee'd log.
            // launch-lobby.sh sets this automatically to
            // /tmp/sf-oracle-plugin-<BRIDGE>.log.
            try
            {
                var perLobbyPath = Environment.GetEnvironmentVariable("SFHEADLESS_LOGFILE");
                if (!string.IsNullOrEmpty(perLobbyPath))
                {
                    BepInEx.Logging.Logger.Listeners.Add(new PerLobbyLogListener(perLobbyPath));
                    Log.LogInfo($"Per-lobby log tee → {perLobbyPath}");
                }
            }
            catch (Exception e) { Log.LogWarning($"per-lobby log init failed: {e.Message}"); }

            // Unity 5.6 doesn't have Application.isBatchMode — fall back to
            // checking the command-line for -batchmode.
            bool batchMode = false;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-batchmode" || arg == "-nographics")
                {
                    batchMode = true;
                    break;
                }
            }
            if (!batchMode)
            {
                Log.LogInfo($"{PluginName} {PluginVersion}: interactive run — installing CLIENT-MODE shim.");
                InstallClientModePatches();
                InstallMapInfoSyncQuantize();  // P0-12 — also on the client side
                return;
            }
            Log.LogInfo($"{PluginName} {PluginVersion}: batchmode detected, bootstrapping headless host.");
            _batchModeHost = true;
            // 2026-06-10: cap the headless frame rate. Under xvfb the game
            // renders uncapped (~250% CPU/instance for frames nobody sees).
            // 60fps matches normal play; FixedUpdate physics is unaffected.
            try
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
                Log.LogInfo("[batch] Application.targetFrameRate=60 (uncapped xvfb rendering wasted ~2.5 cores/instance).");
            }
            catch { }
            InstallMapTerrainAuthorityPatches();
            EnsureOracleP2PNetworkReady("batchmode-boot");

            // Phase 6.9 — settle on scene load (ported from CustomServers).
            // Stock PrepareMapForTravel never runs its kinematic-settle branch
            // on the oracle; without this, chains stress-break and crates fall.
            SceneManager.sceneLoaded -= OnAnySceneLoadedRunSettle;
            SceneManager.sceneLoaded += OnAnySceneLoadedRunSettle;

            // P0-12 — install on the server side too. AddMapDataObject runs
            // in MapInfoSyncableBase.Awake on the oracle's scene; without
            // matching quantization the server's dict key would diverge from
            // the client's even though both call the same function (different
            // process, different float arithmetic).
            InstallMapInfoSyncQuantize();

            ReadEnv();

            // P0 — Harmony-patch NetworkSocketServer to bind on BindPort instead
            // of the hardcoded 1337. We do this before HostServer() is called so
            // the patched ctor sees the override.
            try
            {
                var harmony = new Harmony(PluginGuid);
                var sockType = AccessTools.TypeByName("Landfall.Network.Sockets.NetworkSocketServer");
                if ((object)sockType != null)
                {
                    var ctor = AccessTools.Constructor(sockType, Type.EmptyTypes);
                    if ((object)ctor != null)
                    {
                        harmony.Patch(ctor, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(PatchServerPort))));
                        Log.LogInfo($"Patched NetworkSocketServer ctor — bind port will be {BindPort}.");
                    }
                    else
                    {
                        Log.LogWarning("Could not find NetworkSocketServer parameterless ctor.");
                    }
                }
                else
                {
                    Log.LogWarning("Could not find type Landfall.Network.Sockets.NetworkSocketServer.");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Port-patch failed: {e}");
            }

            // Harmony-prefix Controller.Update so that, right before SF's
            // own Update reads PlayerActions.Movement.X / .Y / button states,
            // we write our per-slot input buffer values into the relevant
            // backing fields. This bypasses InControl's tick + Commit
            // lifecycle (which was overwriting our injection from outside).
            try
            {
                var harmony = new Harmony(PluginGuid + ".controller-input-prefix");
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType != null)
                {
                    var updateMethod = AccessTools.Method(ctrlType, "Update");
                    if ((object)updateMethod != null)
                    {
                        var prefix = AccessTools.Method(typeof(Plugin), nameof(InjectInputPrefix));
                        harmony.Patch(updateMethod, prefix: new HarmonyMethod(prefix));
                        Log.LogInfo("Patched Controller.Update with input-injection prefix.");
                    }
                    // 2026-06-09 AIM FIX: rigs are spawned with keyBoard=true, and
                    // stock UserAim() calls RotateTowardsMouse() for keyboard input
                    // EVERY frame — overriding the Aiming axes entirely. Under xvfb
                    // the "mouse" is a meaningless fixed point, so injected aim never
                    // had any effect (empirically: aim was uncorrelated/anti-
                    // correlated with the intended direction). Skip the mouse path in
                    // batchmode; stock then applies LookRotation(0, AimY, -AimX) from
                    // our injected axes — the real couch-gamepad code path.
                    var rotMouse = AccessTools.Method(ctrlType, "RotateTowardsMouse");
                    if ((object)rotMouse != null)
                    {
                        harmony.Patch(rotMouse, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(SkipRotateTowardsMousePrefix))));
                        Log.LogInfo("Patched Controller.RotateTowardsMouse -> skipped in batchmode (aim comes from injected axes).");
                    }
                    else
                    {
                        Log.LogWarning("Controller.RotateTowardsMouse not found — injected aim will be mouse-overridden!");
                    }
                }
                // [INSTR3] Patch Movement.MoveRight / MoveLeft so we can see
                // whether Controller.Update actually invokes them after our
                // input injection.
                var movType = AccessTools.TypeByName("Movement");
                if ((object)movType != null)
                {
                    var mr = AccessTools.Method(movType, "MoveRight");
                    if ((object)mr != null)
                        harmony.Patch(mr, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(MoveRightPrefix))));
                    var ml = AccessTools.Method(movType, "MoveLeft");
                    if ((object)ml != null)
                        harmony.Patch(ml, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(MoveLeftPrefix))));
                    Log.LogInfo("[INSTR3] Patched Movement.MoveRight/MoveLeft entry-loggers.");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Controller.Update prefix patch failed: {e}");
            }

            // Phase 6.5 — host-side patches. Each runs in its own try/catch
            // so one failure (signature drift, missing type) doesn't silently
            // skip the rest. Failures accumulate in _p65MissingPatches and are
            // surfaced as a loud warning after all installs.
            {
                var harmony = new Harmony(PluginGuid + ".phase6-5-observe");

                var mmType = AccessTools.TypeByName("MultiplayerManager");
                TryPatch(harmony, "MultiplayerManager.IsServer (postfix → true)",
                    (object)mmType != null ? AccessTools.PropertyGetter(mmType, "IsServer") : null,
                    postfix: nameof(IsServerPostfix));
                TryPatch(harmony, "MultiplayerManager.SendMessageToAllClients (prefix log+forward)",
                    (object)mmType != null ? AccessTools.Method(mmType, "SendMessageToAllClients") : null,
                    prefix: nameof(SendBroadcastPrefix));

                var mhTypeP = AccessTools.TypeByName("MatchmakingHandler");
                TryPatch(harmony, "MatchmakingHandler.IsNetworkMatch (postfix → true)",
                    (object)mhTypeP != null ? AccessTools.PropertyGetter(mhTypeP, "IsNetworkMatch") : null,
                    postfix: nameof(IsNetworkMatchPostfix));
                // SetNetworkMatch prefix uses a named `ref bool v` to mutate the
                // arg in-place. Harmony binds prefix params by name, so verify
                // SF's first param really is named `v`; if SF ever renames it
                // (e.g. to `value`), the prefix silently no-ops and the fix
                // we depend on regresses.
                var setNetMatchMethod = (object)mhTypeP != null ? AccessTools.Method(mhTypeP, "SetNetworkMatch") : null;
                if ((object)setNetMatchMethod != null)
                {
                    var ps = setNetMatchMethod.GetParameters();
                    if (ps.Length == 0 || ps[0].Name != "v")
                    {
                        Log.LogError($"[P6.5] SetNetworkMatch first param is '{(ps.Length > 0 ? ps[0].Name : "<none>")}', expected 'v' — SetNetworkMatchPrefix will silently no-op. Update prefix signature.");
                    }
                }
                TryPatch(harmony, "MatchmakingHandler.SetNetworkMatch (prefix force arg=true)",
                    setNetMatchMethod, prefix: nameof(SetNetworkMatchPrefix));

                var wsType = AccessTools.TypeByName("WeaponSelectionHandler");
                TryPatch(harmony, "WeaponSelectionHandler.GetRandomWeaponIndex (prefix → valid index)",
                    (object)wsType != null ? AccessTools.Method(wsType, "GetRandomWeaponIndex") : null,
                    prefix: nameof(GetRandomWeaponIndexPrefix));

                // 2026-06-10 PICKUP FIX: in a network match (which we force),
                // BodyPart.OnCollisionEnter routes weapon pickup through
                // MultiplayerManager.RequestWeaponPickUp — a Steamworks P2P
                // round-trip that goes nowhere for in-process rigs, so LOCAL
                // players could NEVER pick up weapons (verified: agent armed
                // 0% over 90s with sky weapons raining). Replicate the stock
                // LOCAL branch (Fighting.PickUpWeapon + destroy pickup) for
                // batchmode, same philosophy as the death-pipeline fix.
                var mmTypeP = AccessTools.TypeByName("MultiplayerManager");
                TryPatch(harmony, "MultiplayerManager.RequestWeaponPickUp (prefix → local pickup)",
                    (object)mmTypeP != null ? AccessTools.Method(mmTypeP, "RequestWeaponPickUp") : null,
                    prefix: nameof(RequestWeaponPickUpLocalPrefix));

                var gmTypeP = AccessTools.TypeByName("GameManager");
                TryPatch(harmony, "GameManager.SpawnRandomWeapon (prefix replace impl)",
                    (object)gmTypeP != null ? AccessTools.Method(gmTypeP, "SpawnRandomWeapon") : null,
                    prefix: nameof(SpawnRandomWeaponPrefix));

                // Batchmode: replace the cosmetic 3-2-1 countdown with its end
                // state, synchronously. Stock CountDownCoroutine freezes time
                // (managerTime=0), drives UI (CountDownHandler/MapInfo), then
                // restores managerTime=1 after 1s realtime — but in batchmode
                // those UI calls can throw (observed on Ice11/57), killing the
                // coroutine mid-freeze with mPlayingCountdown latched true:
                // the clock stays 0 forever and every retry no-ops. Headless
                // pacing is handled by SF_PRE_COMBAT_DELAY; the countdown
                // freeze exists only for the UI, so skip it wholesale.
                TryPatch(harmony, "GameManager.StartCountDown (batch: skip coroutine, apply end state)",
                    (object)gmTypeP != null ? AccessTools.Method(gmTypeP, "StartCountDown") : null,
                    prefix: nameof(StartCountDownBatchPrefix));

                // P2PPackageHandler.SendP2PPacketToUser has two overloads;
                // we want the CSteamID one. AccessTools.Method without a
                // typeArray returns the first match which may be the wrong
                // overload, so probe explicitly.
                MethodInfo csteamSend = null;
                var ppType = AccessTools.TypeByName("P2PPackageHandler");
                if ((object)ppType != null)
                {
                    foreach (var m in ppType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "SendP2PPacketToUser") continue;
                        var ps = m.GetParameters();
                        if (ps.Length >= 1 && ps[0].ParameterType.Name == "CSteamID") { csteamSend = m; break; }
                    }
                }
                TryPatch(harmony, "P2PPackageHandler.SendP2PPacketToUser(CSteamID,…) (prefix log)",
                    csteamSend, prefix: nameof(SendDirectPrefix));

                // Phase 6.9 diagnostics — log when SF's host-side
                // PrepareMapForTravel coroutine reaches each critical step.
                // Tells us whether destructibles are getting the right init
                // (kinematic-settle, joint detach/reattach, InitSyncedObjects).
                TryPatch(harmony, "MultiplayerManager.InitSyncedObjects (postfix log)",
                    (object)mmType != null ? AccessTools.Method(mmType, "InitSyncedObjects") : null,
                    postfix: nameof(InitSyncedObjectsPostfix));
                TryPatch(harmony, "MultiplayerManager.InitMapDataObjects (postfix log)",
                    (object)mmType != null ? AccessTools.Method(mmType, "InitMapDataObjects") : null,
                    postfix: nameof(InitMapDataObjectsPostfix));
                TryPatch(harmony, "MultiplayerManager.ReadyUp (postfix log)",
                    (object)mmType != null ? AccessTools.Method(mmType, "ReadyUp") : null,
                    postfix: nameof(ReadyUpPostfix));

                var ppTypeHeadless = AccessTools.TypeByName("P2PPackageHandler");
                if (_batchModeHost && (object)ppTypeHeadless != null)
                {
                    var isPkt = AccessTools.Method(ppTypeHeadless, "IsPacketAvailable");
                    if ((object)isPkt != null)
                        TryPatch(harmony, "P2PPackageHandler.IsPacketAvailable (headless null-guard)",
                            isPkt, prefix: nameof(IsPacketAvailableHeadlessPrefix));
                }

                if (_batchModeHost)
                    TryPatchHealthHandlerDieForRoundAdvance(harmony);

                if (_p65MissingPatches.Count == 0)
                {
                    Log.LogInfo($"[P6.5] All {_p65PatchesSucceeded}/{_p65PatchesAttempted} patches installed.");
                }
                else
                {
                    Log.LogError($"[P6.5] {_p65PatchesSucceeded}/{_p65PatchesAttempted} patches installed; MISSING: {string.Join("; ", _p65MissingPatches.ToArray())}");
                    Log.LogError("[P6.5] Oracle will boot, but Phase 6.5 host-side gameplay will be partial. Investigate above failures.");
                }
            }

            _bootStartedAt = Time.realtimeSinceStartup;
            _bootState = BootState.WaitForInit;
        }

        // Harmony prefix on Controller.Update. Runs once per controller per
        // frame, immediately before the original method body. We look up
        // our static input buffer by the controller's playerID, and write
        // those values directly into the Movement / Aiming / button-action
        // backing fields. The original Update then reads them and dispatches
        // movement.MoveRight() / etc with our values.
        //
        // Only runs for rigs WE spawned (gated by SlotToRig containing the
        // controller's GameObject) — never touches real-player rigs.
        private static int _prefixCallCount;
        private static int _prefixOurRigCount;
        private static int _applyInputCount;
        private static int _moveRightCallCount;
        private static int _moveLeftCallCount;
        internal static bool InjectInputPrefix(object __instance)
        {
            try
            {
                _prefixCallCount++;
                if ((object)__instance == null) return true;
                var ctrlComp = __instance as Component;
                if ((object)ctrlComp == null) return true;
                var rig = ctrlComp.gameObject;

                int slot = -1;
                foreach (var kv in SlotToRig)
                {
                    if (kv.Value == rig) { slot = kv.Key; break; }
                }
                if (slot < 0)
                {
                    if (_prefixCallCount % 600 == 1)
                        Log.LogDebug($"InjectInputPrefix: rig {rig.name} not ours (prefix call #{_prefixCallCount})");
                    return true;
                }
                _prefixOurRigCount++;

                if (!SlotInputs.TryGetValue(slot, out var input)) return true;

                bool verbose = (_prefixOurRigCount == 1 || _prefixOurRigCount % 120 == 0);
                if (verbose)
                    Log.LogInfo($"[INSTR2a] Prefix entry: slot={slot} SlotInputs.stick=({input.StickX:0.00},{input.StickY:0.00}) ourCallCount={_prefixOurRigCount}");

                var actionsField = AccessTools.Field(__instance.GetType(), "mPlayerActions");
                if ((object)actionsField == null) return true;
                var actions = actionsField.GetValue(__instance);
                if ((object)actions == null) return true;

                // Read CURRENT Movement.X/Y BEFORE our write — tells us whether
                // InControl rebuilt it since our last write last frame.
                if (verbose)
                {
                    var preX = ReadAxis(actions, "Movement", "X");
                    var preY = ReadAxis(actions, "Movement", "Y");
                    Log.LogInfo($"[INSTR2b] PRE-write Movement=({preX:0.00},{preY:0.00})");
                }

                // Stuff our values into Movement.X / .Y backing fields and
                // Movement.thisValue. Read by the original Update body
                // immediately after this prefix returns.
                ForceTwoAxis(actions, "Movement", input.StickX, input.StickY);
                ForceTwoAxis(actions, "Aiming",   input.AimX,   input.AimY);

                // Read again AFTER write — confirms write took effect synchronously.
                if (verbose)
                {
                    var postX = ReadAxis(actions, "Movement", "X");
                    var postY = ReadAxis(actions, "Movement", "Y");
                    // Probe Controller early-return gate variables.
                    var ctrlT = __instance.GetType();
                    var inactiveF = AccessTools.Field(ctrlT, "inactive");
                    var infoF    = AccessTools.Field(ctrlT, "info");
                    var hasCtrlF = AccessTools.Field(ctrlT, "mHasControl");
                    bool inactive = (object)inactiveF != null && (bool)inactiveF.GetValue(__instance);
                    bool hasCtrl  = (object)hasCtrlF != null && (bool)hasCtrlF.GetValue(__instance);
                    bool isDead = false;
                    if ((object)infoF != null) {
                        var infoVal = infoF.GetValue(__instance);
                        if ((object)infoVal != null) {
                            var deadF = AccessTools.Field(infoVal.GetType(), "isDead");
                            if ((object)deadF != null) isDead = (bool)deadF.GetValue(infoVal);
                        }
                    }
                    // GameManager.inFight + stillInMenu — the prime suspects.
                    var gmT = AccessTools.TypeByName("GameManager");
                    bool inFight = false, stillInMenu = false;
                    if ((object)gmT != null) {
                        var fF = AccessTools.Field(gmT, "inFight");
                        var mF = AccessTools.Field(gmT, "stillInMenu");
                        if ((object)fF != null) inFight = (bool)fF.GetValue(null);
                        if ((object)mF != null) stillInMenu = (bool)mF.GetValue(null);
                    }
                    bool willEarlyReturn = inactive || isDead || (!inFight && !stillInMenu);
                    Log.LogInfo($"[INSTR2c] POST-write Movement=({postX:0.00},{postY:0.00}) inactive={inactive} hasControl={hasCtrl} isDead={isDead} inFight={inFight} stillInMenu={stillInMenu} willEarlyReturn={willEarlyReturn}");
                }

                // Button-action backing field updates. PlayerAction is a
                // OneAxisInputControl with private InputControlState
                // (lastState, thisState, nextState). The IsPressed/WasPressed
                // accessors read thisState. We reach into thisState's .State
                // bool and .Value float to set the press.
                ForceButton(actions, "Jump",        (input.Buttons & 0x01) != 0);
                ForceButton(actions, "Jump2",       (input.Buttons & 0x01) != 0);
                ForceButton(actions, "PunchOrFire", (input.Buttons & 0x02) != 0);
                ForceButton(actions, "Block",       (input.Buttons & 0x04) != 0);
                ForceButton(actions, "Throw",       (input.Buttons & 0x08) != 0);
            }
            catch (Exception e)
            {
                if (Verbose && Log != null) Log.LogDebug($"InjectInputPrefix: {e.Message}");
            }
            return true; // always let original Update run
        }

        internal static void MoveRightPrefix(object __instance)
        {
            _moveRightCallCount++;
            if (_moveRightCallCount == 1 || _moveRightCallCount % 30 == 0)
            {
                try
                {
                    var c = __instance as Component;
                    string name = (object)c != null ? c.gameObject.name : "?";
                    Log.LogInfo($"[INSTR3] Movement.MoveRight#{_moveRightCallCount} on {name}");
                }
                catch { }
            }
        }

        internal static void MoveLeftPrefix(object __instance)
        {
            _moveLeftCallCount++;
            if (_moveLeftCallCount == 1 || _moveLeftCallCount % 30 == 0)
            {
                try
                {
                    var c = __instance as Component;
                    string name = (object)c != null ? c.gameObject.name : "?";
                    Log.LogInfo($"[INSTR3] Movement.MoveLeft#{_moveLeftCallCount} on {name}");
                }
                catch { }
            }
        }

        // Phase 6.5 Step 1 — IsServer=true. Static property getter so no __instance.
        internal static void IsServerPostfix(ref bool __result)
        {
            __result = true;
        }

        // Phase 6.5 Step 2d — IsNetworkMatch=true. Pin against Controller's reset.
        internal static void IsNetworkMatchPostfix(ref bool __result)
        {
            __result = true;
        }

        // Phase 6.9 diagnostics — track PrepareMapForTravel progress on oracle.
        private static int _initSyncedCallCount;
        internal static void InitSyncedObjectsPostfix()
        {
            _initSyncedCallCount++;
            Log.LogInfo($"[P6.9 init] InitSyncedObjects called (#{_initSyncedCallCount}). PrepareMapForTravel reached settle-end on the oracle.");
        }
        private static int _initMapDataCallCount;
        internal static void InitMapDataObjectsPostfix()
        {
            _initMapDataCallCount++;
            Log.LogInfo($"[P6.9 init] InitMapDataObjects called (#{_initMapDataCallCount}).");
        }
        private static int _readyUpCallCount;
        internal static void ReadyUpPostfix()
        {
            _readyUpCallCount++;
            Log.LogInfo($"[P6.9 init] MultiplayerManager.ReadyUp called (#{_readyUpCallCount}).");
        }

        // Phase 6.5 Step 2d — force every SetNetworkMatch(v) call to use v=true.
        // Defeats the inlined-getter problem because the backing field stays true.
        private static int _setNetMatchInterceptCount;
        internal static bool SetNetworkMatchPrefix(ref bool v)
        {
            _setNetMatchInterceptCount++;
            if (!v && _setNetMatchInterceptCount <= 5)
                Log.LogInfo($"[P6.5] SetNetworkMatch(false) intercepted #{_setNetMatchInterceptCount} → forcing true");
            v = true;
            return true; // run original with forced arg
        }

        // Phase 6.5 Step 2e — replace GameManager.SpawnRandomWeapon. Computes a
        // spawn position matching the original method's logic, picks a weapon
        // via the (already-patched) GetRandomWeaponIndex, and calls
        // MultiplayerManager.SpawnWeapon directly. Returns false to skip
        // original.
        private static int _srwCallCount;
        internal static bool SpawnRandomWeaponPrefix(object __instance)
        {
            try
            {
                _srwCallCount++;
                // Reset randomWeaponCounter to a new value (mirrors original lines 252-264).
                var gmType = __instance.GetType();
                var rwcField = AccessTools.Field(gmType, "randomWeaponCounter");
                var extraField = AccessTools.Field(gmType, "extraSpawnWeaponTime");
                float extra = (object)extraField != null ? (float)extraField.GetValue(__instance) : 0f;
                if ((object)rwcField != null)
                {
                    float newWait = UnityEngine.Random.Range(5f, 8f) + extra;
                    rwcField.SetValue(__instance, newWait);
                }

                // Pick a weapon index. Honors the /weapons chat allow-list
                // if set, otherwise round-robin 0..7.
                int weaponIdx = PickWeaponId(_srwCallCount);

                // Spawn position mirroring original: Y=11*scale, Z=Random(-8,8).
                float zOff = UnityEngine.Random.Range(0f, 8f);
                if (_srwCallCount % 2 == 0) zOff *= -1f;
                float scale = 1f;
                var lastAppliedScaleF = AccessTools.Field(gmType, "LastAppliedScale");
                if ((object)lastAppliedScaleF != null)
                {
                    var v = lastAppliedScaleF.GetValue(__instance);
                    if (v is float f) scale = f;
                }
                Vector3 spawnPos = Vector3.up * (11f * scale) + Vector3.forward * zOff;

                // Find MultiplayerManager.SpawnWeapon and invoke directly. SF
                // host code's `mNetworkManager` field is private; we go via
                // FindObjectOfType.
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mmType == null) return false;
                var mmInst = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)mmInst == null)
                {
                    var all = Resources.FindObjectsOfTypeAll(mmType);
                    if (all != null && all.Length > 0) mmInst = all[0];
                }
                if ((object)mmInst == null)
                {
                    if (_srwCallCount <= 3)
                        Log.LogWarning("[P6.5 SRW] MultiplayerManager instance is null; skipping.");
                    return false;
                }
                // 2026-06-10 WEAPON-EXISTENCE FIX: stock SpawnWeapon only
                // SERIALIZES a WeaponSpawned packet for clients — on a stock
                // listen-server the host's own client-side handler then
                // instantiates the physical weapon, but this headless host
                // never loops the packet back to itself, so NO physical
                // WeaponPickUp ever existed in-world (verified: 1000+ "sky
                // spawn" log lines, zero pickups possible, agent armed 0%).
                // Replicate the loopback: build the same 8-byte payload stock
                // writes and call OnWeaponSpawned(byte[]) — instantiates the
                // weapon locally + registers mSpawnedWeapons properly.
                // (Training fleet has no socket clients; the socket broadcast
                // is intentionally skipped to keep spawn IDs consistent.)
                // Both GetNext* take `bool beginFromEnd = false` — raw reflection
                // does NOT apply default args, so pass false explicitly.
                var getWid = AccessTools.Method(mmType, "GetNextWeaponSpawnID", new[] { typeof(bool) });
                var getSid = AccessTools.Method(mmType, "GetNextSyncableObjectSpawnID", new[] { typeof(bool) });
                var onSpawned = AccessTools.Method(mmType, "OnWeaponSpawned", new[] { typeof(byte[]) });
                if ((object)getWid == null || (object)getSid == null || (object)onSpawned == null)
                {
                    if (_srwCallCount <= 3)
                        Log.LogWarning("[P6.5 SRW] OnWeaponSpawned/GetNext* not found — cannot spawn local weapon.");
                    return false;
                }
                ushort widN = (ushort)getWid.Invoke(mmInst, new object[] { false });
                ushort sidN = (ushort)getSid.Invoke(mmInst, new object[] { false });
                byte[] payload = new byte[8];
                using (var ms = new System.IO.MemoryStream(payload))
                using (var bw = new System.IO.BinaryWriter(ms))
                {
                    bw.Write((byte)weaponIdx);
                    bw.Write((sbyte)spawnPos.y);
                    bw.Write((sbyte)spawnPos.z);
                    bw.Write(widN);
                    bw.Write(sidN);
                    bw.Write((byte)0);   // not a present
                }
                onSpawned.Invoke(mmInst, new object[] { payload });
                if (_srwCallCount <= 5 || _srwCallCount % 10 == 0)
                    Log.LogInfo($"[P6.5 SRW] call#{_srwCallCount} → local OnWeaponSpawned(id={weaponIdx}, pos={spawnPos}, wid={widN})");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5 SRW] threw: {e.Message}");
            }
            return false; // skip original SpawnRandomWeapon
        }

        // Phase 6.5 Step 2c — force GetRandomWeaponIndex to return a valid index.
        // Stock SF returns -1 if m_WeaponRaritiesArray is empty (UI never set up).
        // Network branch in SpawnRandomWeapon only uses the int index; weaponObject
        // is consumed only by the local-spawn path which we don't take.
        private static int _grwiCallCount;
        internal static bool GetRandomWeaponIndexPrefix(bool mustBeActive, ref GameObject weaponObject, ref int __result)
        {
            _grwiCallCount++;
            weaponObject = null;
            __result = PickWeaponId(_grwiCallCount);
            if (_grwiCallCount <= 3 || _grwiCallCount % 5 == 0)
                Log.LogInfo($"[P6.5] GetRandomWeaponIndexPrefix call#{_grwiCallCount} → returning {__result}");
            return false; // skip original
        }

        // Phase 6.8 — chat-driven weapon allow-list. When empty, picks
        // from a round-robin 0..7 (stock SF's first 8 weapons — pistol
        // through shotgun in stock id order). When set via /weapons
        // chat command, picks uniformly from the allow-list.
        // Static so /weapons handler (instance method) and the static
        // GRWI/SRW prefixes share state.
        internal static readonly System.Collections.Generic.HashSet<int> _allowedWeaponIds = new System.Collections.Generic.HashSet<int>();
        private static int _allowedWeaponCycleIdx;
        internal static int PickWeaponId(int seed)
        {
            if (_allowedWeaponIds.Count > 0)
            {
                // Round-robin across the allow-list. Could randomize but
                // deterministic order makes tournaments more predictable.
                var arr = new int[_allowedWeaponIds.Count];
                _allowedWeaponIds.CopyTo(arr);
                System.Array.Sort(arr);
                int pick = arr[_allowedWeaponCycleIdx % arr.Length];
                _allowedWeaponCycleIdx++;
                return pick;
            }
            return seed % 8;
        }

        // Phase 6.5 Step 1 — log host broadcasts. Observe-only: return true so the
        // original method runs (it's a no-op on the oracle because mConnectedClients
        // is empty; we just want to see which msgTypes SF host code wants to send).
        // Use object[] __args to dodge needing typed refs to EP2PSend (Steamworks).
        private static int _p65BroadcastCount;
        private static readonly Dictionary<byte, int> _p65BroadcastByType = new Dictionary<byte, int>();
        private static int _p65ObjUpdateIdxLogCount;
        private static readonly HashSet<ushort> _p65ObjUpdateSeenIndices = new HashSet<ushort>();
        private static int _p65ObjUpdateFilterCount;
        private static int _p65DestructionFilterCount;
        internal static bool SendBroadcastPrefix(object[] __args)
        {
            try
            {
                _p65BroadcastCount++;
                var data = __args.Length > 0 ? __args[0] as byte[] : null;
                // (byte)__args[1] on a boxed byte-backed enum may throw on
                // strict CLRs; Convert.ToInt32 unboxes via IConvertible and
                // works for either a raw byte or an enum.
                byte msgType = UnboxByte(__args.Length > 1 ? __args[1] : null);
                bool ignoreServer = __args.Length > 2 && __args[2] is bool b && b;
                // SF's signature is (..., ulong ignoreUserID, ...) — raw ulong,
                // NOT CSteamID. So a typed cast works, but use Convert for
                // robustness against future SF refactors.
                ulong ignoreUID = UnboxUlong(__args.Length > 3 ? __args[3] : null);

                int prev;
                _p65BroadcastByType.TryGetValue(msgType, out prev);
                _p65BroadcastByType[msgType] = prev + 1;

                bool first = prev == 0;
                bool sample = first || (_p65BroadcastByType[msgType] % 60 == 0);
                if (sample)
                {
                    Log.LogInfo($"[P6.5] HostBroadcast#{_p65BroadcastCount} msgType={msgType}({MsgTypeName(msgType)}) bodyLen={data?.Length ?? 0} ignoreSrv={ignoreServer} ignoreUID={ignoreUID} count[{msgType}]={_p65BroadcastByType[msgType]}");
                }
                // For ObjectUpdate, log the index every time so we can see
                // which scene NSOs are broadcasting (the index is the first
                // 2 bytes of the body, ushort LE).
                    if (msgType == 31 && data != null && (object)Instance != null)
                        Instance.CacheGroundWeaponsBroadcast(data);
                    if (msgType == 33 && sample)
                        Log.LogInfo($"[v26.6] Host MapInfoSync forward count={_p65BroadcastByType[msgType]} bodyLen={data?.Length ?? 0}");
                    if (msgType == 26 && data != null && data.Length >= 2 && _p65ObjUpdateIdxLogCount < 30)
                {
                    ushort idx = (ushort)(data[0] | (data[1] << 8));
                    if (!_p65ObjUpdateSeenIndices.Contains(idx))
                    {
                        _p65ObjUpdateSeenIndices.Add(idx);
                        _p65ObjUpdateIdxLogCount++;
                        Log.LogInfo($"[P6.5] ObjectUpdate from new index={idx} (total unique={_p65ObjUpdateSeenIndices.Count})");
                    }
                }

                // Phase 6.5 Step 2 — forward the broadcast through our v25
                // protocol so the real client actually receives it. SF's own
                // SendMessageToAllClients loop iterates mConnectedClients which
                // is empty on the oracle (we never registered the user there),
                // so SF's loop is a no-op. We do the actual delivery here.
                // SF's MsgType enum byte values match our v25 protocol's Pkt*
                // constants 1:1 for the first 38 entries.
                //
                // Phase 6.7 filter: if the oracle's own mirror rig generates
                // PlayerUpdate (10) or PlayerTalked (12) broadcasts, do NOT
                // forward them. The client already receives a relay of the
                // real player's PlayerUpdate via HandlePlayerUpdate, and an
                // oracle-rig PlayerUpdate would appear as a phantom 2nd
                // player on the client's screen.
                if ((object)Instance != null && data != null
                    && msgType != 10  // PktPlayerUpdate
                    && msgType != 12) // PktPlayerTalked
                {
                    // Extract the channel arg (index 5 in SendMessageToAllClients).
                    // The patched DLL routes incoming packets by channel — using
                    // channel 0 for everything sends them to CheckMessageType
                    // which throws "Messagetype X is not setup!" for things like
                    // ObjectUpdate that should go to NSO.ListenForPackages instead.
                    byte channel = 0;
                    if (__args.Length > 5)
                    {
                        try { channel = (byte)Convert.ToInt32(__args[5]); } catch { }
                    }
                    bool skip = false;
                    // For ObjectUpdate, filter out broadcasts where the object's
                    // Y position is out of int16 range (overflow artifact).
                    if (msgType == 26 && data.Length >= 4)
                    {
                        short posYmul100 = (short)(data[2] | (data[3] << 8));
                        if (posYmul100 < -3000)
                        {
                            skip = true;
                            if (_p65ObjUpdateFilterCount < 5 || _p65ObjUpdateFilterCount % 100 == 0)
                                Log.LogInfo($"[P6.5] Skipping ObjectUpdate forward — Y={posYmul100/100f:0.0} out of playable range (#{_p65ObjUpdateFilterCount})");
                            _p65ObjUpdateFilterCount++;
                        }
                    }
                    // BOXES/ICE VANISHING FIX (reverted from P0-11 Y-aware
                    // heuristic 2026-05-23 night). Back to drop-ALL for
                    // server-originated destructions. The Y-aware filter
                    // introduced random ice/chain breaks because chains
                    // stress-break on the oracle's scene under joint
                    // forces (above Y=-30 obviously) and we were forwarding
                    // those.
                    //
                    // The "ghost box" tradeoff (server destroys box, client
                    // still has it) is rare and recoverable. The "ice
                    // randomly breaks" was constant during play. Pick the
                    // less-bad failure mode.
                    //
                    // Legit destructions still propagate from clients via
                    // the INBOUND RelayBodyToAll path — kicking ice as a
                    // player still works because the player rig (dynamic)
                    // colliding with the ice (kinematic on client now)
                    // fires OnCollisionEnter → SendDestructMessage → server.
                    if (msgType == 28 || msgType == 29 || msgType == 30)
                    {
                        skip = (object)Instance != null
                            && Instance.ShouldSkipServerOriginatedDestruction(data, data?.Length ?? 0);
                        if (skip)
                        {
                            if (_p65DestructionFilterCount < 5 || _p65DestructionFilterCount % 50 == 0)
                                Log.LogInfo($"[destruction] Skip server-originated msgType={msgType} (#{_p65DestructionFilterCount}) — killbox/chain-load");
                            _p65DestructionFilterCount++;
                        }
                    }
                    if (!skip) Instance.ForwardBroadcastToV25Clients(msgType, data, ignoreUID, channel);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5] broadcast prefix threw: {e.Message}");
            }
            return true; // run original (no-op on oracle because mConnectedClients is empty)
        }

        // Forward an intercepted host broadcast through our v25 UDP socket.
        // Channel is critical — the patched DLL routes by channel; non-zero
        // channels (e.g. 10 for ObjectUpdate) dispatch via NSO.ListenForPackages,
        // while channel 0 goes to P2PPackageHandler.CheckMessageType.
        internal void ForwardBroadcastToV25Clients(byte msgType, byte[] body, ulong ignoreUID, byte channel = 0)
        {
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (!cli.Initialized) continue;
                if (ignoreUID != 0 && cli.SteamID == ignoreUID) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (sent > 0 && _p65BroadcastCount <= 5)
                Log.LogInfo($"[P6.5] Forwarded msgType={msgType}({MsgTypeName(msgType)}) bodyLen={body.Length} ch={channel} to {sent} v25 client(s).");
        }

        // Phase 6.5 Step 1 — log direct user-targeted sends (CSteamID overload).
        private static int _p65DirectCount;
        private static readonly Dictionary<byte, int> _p65DirectByType = new Dictionary<byte, int>();
        internal static bool SendDirectPrefix(object[] __args)
        {
            try
            {
                _p65DirectCount++;
                // args: [CSteamID clientID, byte[] data, MsgType type, EP2PSend, int channel]
                ulong sid = 0uL;
                if (__args.Length > 0 && __args[0] != null)
                {
                    var idObj = __args[0];
                    var f = AccessTools.Field(idObj.GetType(), "m_SteamID");
                    if ((object)f != null) sid = (ulong)f.GetValue(idObj);
                }
                var data = __args.Length > 1 ? __args[1] as byte[] : null;
                byte msgType = UnboxByte(__args.Length > 2 ? __args[2] : null);

                int prev;
                _p65DirectByType.TryGetValue(msgType, out prev);
                _p65DirectByType[msgType] = prev + 1;

                bool first = prev == 0;
                bool sample = first || (_p65DirectByType[msgType] % 60 == 0);
                if (sample)
                {
                    Log.LogInfo($"[P6.5] DirectSend#{_p65DirectCount} → sid={sid} msgType={msgType}({MsgTypeName(msgType)}) bodyLen={data?.Length ?? 0} count[{msgType}]={_p65DirectByType[msgType]}");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5] direct prefix threw: {e.Message}");
            }
            return true;
        }

        // Phase 6.5 Step 2 — schedule + invoke GameManager.StartMatch on the oracle.
        private static float _oracleStartMatchAt = -1f;
        private static bool _oracleStartMatchFired;
        private static float _oracleCountDownAt = -1f;
        private static bool _oracleCountDownFired;

        /// <summary>PostMapLoad runs StartCountDown; cancel the duplicate scheduled tick.</summary>
        internal static void SuppressScheduledOracleCountDown(string reason)
        {
            _oracleCountDownAt = -1f;
            _oracleCountDownFired = true;
            Log.LogInfo($"[P6.5] Suppressed scheduled StartCountDown ({reason}).");
        }

        private static void InvokeOracleStartCountDown()
        {
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) { Log.LogWarning("[P6.5] GameManager type not found (countdown)"); return; }
                object gmInst = null;
                var instanceGetter = AccessTools.PropertyGetter(gmType, "Instance");
                if ((object)instanceGetter != null) gmInst = instanceGetter.Invoke(null, null);
                if ((object)gmInst == null) gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                if ((object)gmInst == null) { Log.LogWarning("[P6.5] GameManager instance not found (countdown)"); return; }

                var startCountDown = AccessTools.Method(gmType, "StartCountDown");
                bool countDownOk = false;
                if ((object)startCountDown != null)
                {
                    try
                    {
                        startCountDown.Invoke(gmInst, null);
                        countDownOk = true;
                        Log.LogInfo("[P6.5] Invoked GameManager.StartCountDown() on oracle (boss/minigame coroutines).");
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning($"[P6.5] StartCountDown threw: {e.InnerException?.Message ?? e.Message}");
                    }
                }
                var inFightField = AccessTools.Field(gmType, "inFight");
                if (!countDownOk && (object)inFightField != null)
                {
                    inFightField.SetValue(gmInst, true);
                    Log.LogInfo("[P6.5] Fallback: GameManager.inFight = true (no countdown UI in batchmode).");
                }
                // Also reset randomWeaponCounter so a weapon will spawn soon.
                var rwcField = AccessTools.Field(gmType, "randomWeaponCounter");
                if ((object)rwcField != null)
                    rwcField.SetValue(gmInst, 2.0f);
                if ((object)Instance != null)
                    Instance.ScheduleNextSkyWeapon(OracleFirstSkyWeaponDelay);

                // Phase 6.9: manually invoke the network branch of
                // PrepareMapForTravel that SF's host normally runs (and which
                // never reaches us on the oracle — confirmed empirically by
                // zero hits on InitSyncedObjectsPostfix). This is the critical
                // sequence for destructibles + chains + ice.
                InvokeMultiplayerManagerInitChain();
            }
            catch (Exception e)
            {
                Log.LogError($"[P6.5] InvokeOracleStartCountDown threw: {e}");
            }
        }

        // Phase 6.9 — settle phase at Landfall map load. Freezes all RBs briefly,
        // then re-enables dynamics only on pushable crates (not chain-style ice).
        private void OnAnySceneLoadedRunSettle(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "MainScene" || string.IsNullOrEmpty(scene.name)) return;
            if ((object)Instance != null && scene.buildIndex != Instance._currentSceneIndex)
            {
                Log.LogInfo($"[P6.9 settle] Skip stale scene '{scene.name}' buildIndex={scene.buildIndex} (match={Instance._currentSceneIndex}).");
                if (Instance.IsOracleMapLoadInProgress())
                    Instance.ForceCompleteOracleMapLoadIfNeeded("stale-settle-skip");
                return;
            }
            _sceneLoadRealtime = Time.realtimeSinceStartup;
            _nsoSpawnPos.Clear();
            _nsoPeriodicKeyframeNextAt = Time.realtimeSinceStartup + 1f;
            Log.LogInfo($"[P6.9 settle] Scene loaded: '{scene.name}' (buildIndex={scene.buildIndex}); starting settle coroutine.");
            StartCoroutine(SettlePhaseCoroutine(scene));
            StartCoroutine(DelayedMapTerrainInitCoroutine());
        }

        private System.Collections.IEnumerator DelayedMapTerrainInitCoroutine()
        {
            yield return new WaitForSeconds(OraclePreCombatGraceSec);
            Scene scene;
            if (TryFindLoadedSceneForCurrentMapIndex(out scene))
                EnsureMapSyncObjectsRegistered(scene, true);
            else
                EnsureMapSyncObjectsRegistered();
            InvokeCheckForGroundWeapons("scene-loaded-delay");
            _groundWeaponsRetryAt = Time.realtimeSinceStartup + 4f;
        }

        private System.Collections.IEnumerator SettlePhaseCoroutine(Scene scene)
        {
            yield return null;
            var rootGOs = scene.GetRootGameObjects();
            var allRBs = new List<Rigidbody>();
            foreach (var go in rootGOs)
            {
                if ((object)go == null) continue;
                allRBs.AddRange(go.GetComponentsInChildren<Rigidbody>(true));
            }
            int n = allRBs.Count;
            Log.LogInfo($"[P6.9 settle] Scene '{scene.name}': freezing {n} rigidbodies for settle phase.");
            bool[] wasKinematic = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var rb = allRBs[i];
                if ((object)rb == null) continue;
                wasKinematic[i] = rb.isKinematic;
                rb.isKinematic = true;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            float settleSec = n > 50 ? 2.5f : 1.5f;
            yield return new WaitForSecondsRealtime(settleSec);
            var dpType = AccessTools.TypeByName("DestructiblePiece");
            var dontEnableType = AccessTools.TypeByName("DontEnableRig");
            FieldInfo simpleField = (object)dpType != null ? AccessTools.Field(dpType, "simpleDestruction") : null;
            FieldInfo eventField = (object)dpType != null ? AccessTools.Field(dpType, "eventDestruction") : null;
            int reEnabled = 0;
            for (int i = 0; i < n; i++)
            {
                var rb = allRBs[i];
                if ((object)rb == null) continue;
                if (wasKinematic[i]) continue;
                bool stayKinematic = false;
                if ((object)dpType != null)
                {
                    var dp = rb.GetComponent(dpType);
                    if ((object)dp != null)
                    {
                        bool simple = (object)simpleField != null && (bool)simpleField.GetValue(dp);
                        bool ev = (object)eventField != null && (bool)eventField.GetValue(dp);
                        if (!simple && !ev) stayKinematic = true;
                    }
                }
                if ((object)dontEnableType != null && rb.GetComponent(dontEnableType) != null) stayKinematic = true;
                if (!stayKinematic)
                {
                    rb.isKinematic = false;
                    reEnabled++;
                }
            }
            Log.LogInfo($"[P6.9 settle] Settle complete for '{scene.name}': {reEnabled}/{n} rigidbodies re-enabled dynamic.");
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            MarkSceneNsosMovedAfterSettle();
            if ((object)Instance != null)
                Instance.RunPostMapLoadServerInit(scene);
        }

        // After settle, seed snapshot tracking so quiescent crates still broadcast once.
        private void MarkSceneNsosMovedAfterSettle()
        {
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                if (all == null) return;
                float now = Time.realtimeSinceStartup;
                foreach (var nso in all)
                {
                    var comp = nso as Component;
                    if ((object)comp == null) continue;
                    ushort id = 0;
                    if ((object)_nsoIndexProp != null)
                        id = (ushort)_nsoIndexProp.GetValue(nso, null);
                    else if ((object)_nsoIndexField != null)
                        id = (ushort)_nsoIndexField.GetValue(nso);
                    var p = comp.transform.position;
                    _nsoLastBroadcastPos[id] = p;
                    _nsoLastMovedAt[id] = now;
                    if (p.y > -30f && !IsChainStyleDestructibleRoot(comp.gameObject) && !IsWeaponNsoRoot(comp.gameObject))
                        _nsoSpawnPos[id] = p;
                }
            }
            catch (Exception ex) { Log.LogWarning($"[P6.9 settle] MarkSceneNsosMovedAfterSettle: {ex.Message}"); }
        }

        // Phase 6.9 — manual invoke of MultiplayerManager.InitMapDataObjects +
        // ReadyUp + InitSyncedObjects. Mirrors GameManager.PrepareMapForTravel
        // lines 1023-1029. The full PrepareMapForTravel coroutine ALSO does
        // a kinematic-settle phase before this (set all rigidbodies kinematic,
        // detach joints, wait 1s, reattach) which is what stops crates from
        // tipping off their stack at scene-load. That bigger fix is the
        // "true" Phase 6.9 work — these three calls are the minimum to make
        // NSOs networked properly.
        private static void InvokeMultiplayerManagerInitChain()
        {
            try
            {
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mmType == null) { Log.LogWarning("[P6.9] MultiplayerManager type not found"); return; }
                var mmInst = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)mmInst == null) { Log.LogWarning("[P6.9] MultiplayerManager instance not found"); return; }

                var initMapData = AccessTools.Method(mmType, "InitMapDataObjects");
                if ((object)initMapData != null)
                {
                    try { initMapData.Invoke(mmInst, null); Log.LogInfo("[P6.9] InitMapDataObjects invoked manually."); }
                    catch (Exception e) { Log.LogError($"[P6.9] InitMapDataObjects threw: {e.InnerException?.Message ?? e.Message}"); }
                }

                var clientsField = AccessTools.Field(mmType, "mConnectedClients");
                var clientsArr = (object)clientsField != null ? clientsField.GetValue(mmInst) as Array : null;
                var readyUp = AccessTools.Method(mmType, "ReadyUp");
                if ((object)readyUp != null && clientsArr != null && clientsArr.Length > 0)
                {
                    try { readyUp.Invoke(mmInst, null); Log.LogInfo("[P6.9] ReadyUp invoked manually."); }
                    catch (Exception e) { Log.LogError($"[P6.9] ReadyUp threw: {e.InnerException?.Message ?? e.Message}"); }
                }
                else
                {
                    Log.LogInfo("[P6.9] Skipping ReadyUp — mConnectedClients empty on oracle (expected).");
                }

                // InitSyncedObjects is the critical one — runs NSO.Init on every
                // syncable object in scene, which calls AddSyncableObject + sets
                // mIsListening=true + InitRigidBodies. Without it, NSOs are in
                // a half-initialized state where physics works but networking
                // doesn't (boxes broadcast position but their NetworkSpawnID
                // never gets registered properly).
                var initSynced = AccessTools.Method(mmType, "InitSyncedObjects");
                if ((object)initSynced != null)
                {
                    try { initSynced.Invoke(mmInst, null); Log.LogInfo("[P6.9] InitSyncedObjects invoked manually — NSOs should now be fully networked."); }
                    catch (Exception e) { Log.LogError($"[P6.9] InitSyncedObjects threw: {e.InnerException?.Message ?? e.Message}"); }
                }

                // Phase 6.8 — CheckForGroundWeapons broadcasts the map's
                // pre-placed weapons (the ones in level geometry, registered
                // via InitWeaponPickUpOnAwake → AddPreSpawnedWeapon). Stock SF
                // calls this from GameManager.StartMapSequence after the map
                // loads + IsNetworkMatch is true. On our oracle that coroutine
                // chain doesn't fire; manually invoking ensures clients get
                // GroundWeaponsInit (msgType 31) so map-preset weapons appear
                // at their fixed spots. Addresses user-reported "I cant grab
                // guns that spawn on some maps."
                var checkGround = AccessTools.Method(mmType, "CheckForGroundWeapons");
                if ((object)checkGround != null)
                {
                    try { checkGround.Invoke(mmInst, null); Log.LogInfo("[P6.8] CheckForGroundWeapons invoked manually — map-preset weapons broadcast."); }
                    catch (Exception e) { Log.LogError($"[P6.8] CheckForGroundWeapons threw: {e.InnerException?.Message ?? e.Message}"); }
                }
                if ((object)Instance != null)
                {
                    Instance.EnsureMapSyncObjectsRegistered();
                    Instance.FlushGroundWeaponsAfterCheck("InitChain");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"[P6.9] InvokeMultiplayerManagerInitChain threw: {e}");
            }
        }

        /// <summary>Boss/Halloween maps: wake CustomMap handlers after scene + countdown init.</summary>
        private static void InvokeOracleBossMapSetup()
        {
            if (!_batchModeHost) return;
            try
            {
                // Gate by LOADED SCENE NAME, not a hardcoded index range. Boss/
                // event maps (HalloweenBoss2 = buildIndex 95, Space/Factory boss
                // variants, Pumpkin, etc.) live outside the old 100-109 range, so
                // that check silently skipped them → boss/event never spawned.
                bool isEventMap = false;
                for (int si = 0; si < SceneManager.sceneCount; si++)
                {
                    var sc = SceneManager.GetSceneAt(si);
                    if (!sc.isLoaded || sc.name == "MainScene") continue;
                    string n = sc.name.ToLowerInvariant();
                    if (n.Contains("boss") || n.Contains("halloween") || n.Contains("pumpkin")
                        || n.Contains("christmas") || n.Contains("xmas") || n.Contains("event"))
                    { isEventMap = true; break; }
                }
                if (!isEventMap) return;
                var behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                if (behaviours == null) return;
                int invoked = 0;
                foreach (var mb in behaviours)
                {
                    if ((object)mb == null) continue;
                    string tn = mb.GetType().Name;
                    if (tn.IndexOf("CustomMap", StringComparison.OrdinalIgnoreCase) < 0
                        && tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) < 0
                        && tn.IndexOf("Halloween", StringComparison.OrdinalIgnoreCase) < 0
                        && tn.IndexOf("Pumpkin", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    foreach (var m in mb.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (ReferenceEquals(m, null) || m.GetParameters().Length != 0) continue;
                        string mn = m.Name;
                        if (mn == "Awake" || mn == "Start" || mn.IndexOf("Init", StringComparison.OrdinalIgnoreCase) >= 0
                            || mn.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try { m.Invoke(mb, null); invoked++; } catch { }
                            break;
                        }
                    }
                }
                if (invoked > 0)
                    Log.LogInfo($"[P6.5] Boss map setup: invoked {invoked} handler(s) on event scene.");
            }
            catch (Exception e) { Log.LogWarning($"[P6.5] InvokeOracleBossMapSetup: {e.Message}"); }
        }

        // ── In-process scripted-bot layer (SFGYM_BOT_SLOTS) ───────────────
        // Drives N bot rigs entirely server-side, no external client. Spawns
        // them at the loaded map's spawn points, walks them at each other,
        // swings, and runs a stock-faithful death + revive cycle. See the
        // method bodies for 1:1-with-stock-SF notes.
        internal static List<int> AutoSpawnBotSlots;       // null = disabled
        // Slots driven by an external RL policy via the setBotAction bridge
        // command. DriveScriptedBots still spawns/revives/death-checks them but
        // does NOT write their per-tick inputs (Python owns those). Empty =
        // fully scripted self-play (default).
        internal static HashSet<int> RlControlledSlots = new HashSet<int>();
        // SF_FIXED_MAP=N pins every round-advance to scene N (consistent env for
        // RL training). -1 = random rotation (default).
        internal static int FixedMap = -1;
        private float _botAutoSpawnAt = -1f;
        private bool _botAutoSpawnDone;
        private float _botBootstrapFireAt = -1f;
        private bool _botBootstrapFireDone;
        private bool _botScriptedDriveActive;
        private int _botDriveTickCounter;
        // Post-MovePlayer kinematic flip: stock MovePlayer leaves rigidbodies
        // kinematic when IsNetworkMatch=true; we flip them dynamic afterwards
        // to match what the local host does to its own player.
        private Dictionary<int, float> _botKinematicFlipAt = new Dictionary<int, float>();

        // One-shot NetworkSyncableObject inventory — fires once after match-start
        // settles. Tells us how many syncable objects are in the loaded scene,
        // their listening state, and whether mHasControl is true (which gates
        // ObjectUpdate broadcasting on the host side).
        private static bool _nsoInventoryDone;
        private static float _nsoInventoryAt = -1f;
        private static void RunNetworkSyncableObjectInventory()
        {
            try
            {
                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType == null) { Log.LogWarning("[P6.5 NSO] type not found"); return; }
                var nsos = UnityEngine.Object.FindObjectsOfType(nsoType);
                if (nsos == null) { Log.LogInfo("[P6.5 NSO] FindObjectsOfType returned null"); return; }
                int total = nsos.Length;
                int listening = 0;
                var mHasControlF = AccessTools.Field(nsoType, "mHasControl");
                var mIsListeningF = AccessTools.Field(nsoType, "mIsListening");
                var mIndexF = AccessTools.Field(nsoType, "m_Index");
                // mHasControl is static — single value across all NSOs.
                bool staticHasControl = false;
                if ((object)mHasControlF != null) staticHasControl = (bool)mHasControlF.GetValue(null);
                System.Text.StringBuilder sample = new System.Text.StringBuilder();
                int sampled = 0;
                foreach (var o in nsos)
                {
                    bool listen = (object)mIsListeningF != null && (bool)mIsListeningF.GetValue(o);
                    if (listen) listening++;
                    if (sampled < 10)
                    {
                        var comp = o as Component;
                        string name = (object)comp != null ? comp.gameObject.name : "?";
                        ushort idx = (object)mIndexF != null ? (ushort)mIndexF.GetValue(o) : (ushort)0;
                        sample.Append($"\n   [{sampled}] name={name} idx={idx} listening={listen}");
                        sampled++;
                    }
                }
                Log.LogInfo($"[P6.5 NSO] Inventory: {total} NetworkSyncableObjects found in active scene. Static mHasControl={staticHasControl}, {listening}/{total} are listening (mIsListening=true).{sample}");

                // === Phase 6.7 brute-force fixes ===

                // Fix 1: force-set static mHasControl=true. NSO.Start reads
                // MultiplayerManager.IsServer (which Mono inlined past our
                // postfix) and writes the result here. Single static field
                // across all 91 NSOs — one write fixes everything.
                if ((object)mHasControlF != null && !staticHasControl)
                {
                    mHasControlF.SetValue(null, true);
                    Log.LogInfo("[P6.5 NSO] Forced static NetworkSyncableObject.mHasControl = true.");
                }

                // Fix 2: directly populate per-NSO state instead of calling
                // SF's InitSyncedObjects (which throws because each NSO's
                // mNetworkManager is null — NSO.Awake bailed out early when
                // IsNetworkMatch was momentarily false during scene load).
                // We retroactively:
                //   - set NSO.mNetworkManager from GameManager.Instance.mMultiplayerManager
                //   - set NSO.mPacketHandler from GameManager.Instance.P2PPackageHandler
                //   - flip NSO.mIsListening = true
                if (total > 0 && listening == 0)
                {
                    var gmType = AccessTools.TypeByName("GameManager");
                    object gmInst = null;
                    if ((object)gmType != null)
                    {
                        var instGetter = AccessTools.PropertyGetter(gmType, "Instance");
                        if ((object)instGetter != null) gmInst = instGetter.Invoke(null, null);
                    }
                    object mmFromGm = null;
                    object ppFromGm = null;
                    if ((object)gmInst != null)
                    {
                        var mmField = AccessTools.Field(gmType, "mMultiplayerManager");
                        if ((object)mmField != null) mmFromGm = mmField.GetValue(gmInst);
                        var ppProp = AccessTools.PropertyGetter(gmType, "P2PPackageHandler");
                        if ((object)ppProp != null) ppFromGm = ppProp.Invoke(gmInst, null);
                    }
                    var nmField = AccessTools.Field(nsoType, "mNetworkManager");
                    var phField = AccessTools.Field(nsoType, "mPacketHandler");
                    var otsField = AccessTools.Field(nsoType, "mObjectToSync");
                    var updIdxField = AccessTools.Field(nsoType, "mUpdateIndex");
                    var sendRateField = AccessTools.Field(nsoType, "mSendRate");
                    var sendRatePerSecField = AccessTools.Field(nsoType, "mSendRatePerSecond");
                    int patched = 0, listenSet = 0, otsSet = 0, updIdxSet = 0;
                    int nsoIter = 0;
                    foreach (var o in nsos)
                    {
                        nsoIter++;
                        try
                        {
                            var oComp = o as Component;
                            // Distribute NSOs across UpdateIndexHandler buckets
                            // (0..MAX_UPDATE_INDEX-1, currently 5). Without this,
                            // all NSOs cluster on bucket 0 and only fire on every
                            // 5th frame, halving broadcast density.
                            if ((object)updIdxField != null)
                            {
                                updIdxField.SetValue(o, nsoIter % 5);
                                updIdxSet++;
                            }
                            if ((object)nmField != null && (object)mmFromGm != null)
                            {
                                var cur = nmField.GetValue(o);
                                if ((object)cur == null) { nmField.SetValue(o, mmFromGm); patched++; }
                            }
                            if ((object)phField != null && (object)ppFromGm != null)
                            {
                                var cur = phField.GetValue(o);
                                if ((object)cur == null) phField.SetValue(o, ppFromGm);
                            }
                            // mObjectToSync = base.transform if null (the source of the LateUpdate NullRef).
                            if ((object)otsField != null && (object)oComp != null)
                            {
                                var cur = otsField.GetValue(o) as Transform;
                                if ((object)cur == null) { otsField.SetValue(o, oComp.transform); otsSet++; }
                            }
                            // mSendRate = 1/mSendRatePerSecond if uninitialized (default would be 1/0 = inf).
                            if ((object)sendRateField != null && (object)sendRatePerSecField != null)
                            {
                                float sr = (float)sendRateField.GetValue(o);
                                if (sr <= 0f || float.IsInfinity(sr))
                                {
                                    float srPerSec = (float)sendRatePerSecField.GetValue(o);
                                    if (srPerSec <= 0f) srPerSec = 5f;
                                    sendRateField.SetValue(o, 1f / srPerSec);
                                }
                            }
                            if ((object)mIsListeningF != null)
                            {
                                mIsListeningF.SetValue(o, true);
                                listenSet++;
                            }
                        }
                        catch (Exception e) { Log.LogWarning($"[P6.5 NSO] patch one NSO threw: {e.Message}"); }
                    }
                    Log.LogInfo($"[P6.5 NSO] Patched {patched} NSOs (mNetworkManager was null), set mObjectToSync on {otsSet}, distributed mUpdateIndex on {updIdxSet}, mIsListening=true on {listenSet}/{total}.");

                    // Probe: snapshot 10 NSOs' initial position + kinematic state
                    // so we can see in the log whether the oracle's boxes
                    // actually move when the mirror rig walks through them.
                    _probeNsos.Clear();
                    int probeCount = 0;
                    foreach (var o in nsos)
                    {
                        if (probeCount >= 10) break;
                        var comp = o as Component;
                        if ((object)comp == null) continue;
                        var rb = comp.GetComponentInChildren<Rigidbody>();
                        bool kin = (object)rb != null && rb.isKinematic;
                        Vector3 pos = comp.transform.position;
                        ushort idx = 0;
                        var idxF = AccessTools.Field(nsoType, "m_Index");
                        if ((object)idxF != null) idx = (ushort)idxF.GetValue(o);
                        _probeNsos.Add(new ProbeNsoEntry { Component = comp, Name = comp.gameObject.name, Index = idx, InitialPos = pos, HasRigidbody = (object)rb != null, IsKinematic = kin });
                        Log.LogInfo($"[NSO probe] [{probeCount}] name='{comp.gameObject.name}' index={idx} pos={pos} rb={(object)rb != null} kinematic={kin}");
                        probeCount++;
                    }
                    _probeNextLogAt = Time.realtimeSinceStartup + 5f;
                }
            }
            catch (Exception e)
            {
                Log.LogError($"[P6.5 NSO] inventory threw: {e}");
            }
            finally
            {
                _nsoInventoryDone = true;
            }
        }

        // === NSO movement probe ===
        // Captures a few NSOs' initial position at scene-ready and reports
        // displacement every 5s. Answers: "do oracle boxes actually move
        // when the mirror rig walks through them?"
        private struct ProbeNsoEntry
        {
            public Component Component;
            public string Name;
            public ushort Index;
            public Vector3 InitialPos;
            public bool HasRigidbody;
            public bool IsKinematic;
        }
        private static readonly List<ProbeNsoEntry> _probeNsos = new List<ProbeNsoEntry>();
        private static float _probeNextLogAt = -1f;
        private static void TickNsoProbe()
        {
            if (_probeNsos.Count == 0) return;
            if (Time.realtimeSinceStartup < _probeNextLogAt) return;
            _probeNextLogAt = Time.realtimeSinceStartup + 5f;
            int moved = 0;
            for (int i = 0; i < _probeNsos.Count; i++)
            {
                var e = _probeNsos[i];
                if ((object)e.Component == null) continue;
                Vector3 cur = e.Component.transform.position;
                float disp = (cur - e.InitialPos).magnitude;
                if (disp > 0.05f) moved++;
                Log.LogInfo($"[NSO probe] [{i}] name='{e.Name}' index={e.Index} pos={cur} disp={disp:0.00} (init={e.InitialPos})");
            }
            Log.LogInfo($"[NSO probe] summary: {moved}/{_probeNsos.Count} moved >5cm from initial.");
        }

        // Periodic state probe — log GameManager.inFight + randomWeaponCounter
        // so we can see whether the host-side game loop is actually running.
        // NB: Mono 2.0.50727 lacks FieldInfo.op_Inequality — must cast to
        // object before any reflection-type null comparison.
        private static float _stateProbeLastAt;
        private static void StateProbe()
        {
            try
            {
                if (Time.realtimeSinceStartup - _stateProbeLastAt < 2.0f) return;
                _stateProbeLastAt = Time.realtimeSinceStartup;
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) return;
                var instanceGetter = AccessTools.PropertyGetter(gmType, "Instance");
                object gmInst = null;
                if ((object)instanceGetter != null) gmInst = instanceGetter.Invoke(null, null);
                if ((object)gmInst == null) return;
                var inFightF = AccessTools.Field(gmType, "inFight");
                var rwcF = AccessTools.Field(gmType, "randomWeaponCounter");
                var matchTimeF = AccessTools.Field(gmType, "matchTime");
                var stillInMenuF = AccessTools.Field(gmType, "stillInMenu");
                bool inFight = (object)inFightF != null && (bool)inFightF.GetValue(gmInst);
                float rwc = (object)rwcF != null ? (float)rwcF.GetValue(gmInst) : float.NaN;
                float mt = (object)matchTimeF != null ? (float)matchTimeF.GetValue(gmInst) : float.NaN;
                bool stillInMenu = (object)stillInMenuF != null && (bool)stillInMenuF.GetValue(gmInst);

                var mhType = AccessTools.TypeByName("MatchmakingHandler");
                bool isNetMatch = false;
                if ((object)mhType != null)
                {
                    var inmField = AccessTools.Field(mhType, "mIsNetworkMatch");
                    if ((object)inmField != null) isNetMatch = (bool)inmField.GetValue(null);
                }
                Log.LogInfo($"[P6.5 probe] inFight={inFight} rwc={rwc:0.00} matchTime={mt:0.00} stillInMenu={stillInMenu} IsNetMatch={isNetMatch}");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5 probe] {e.Message}");
            }
        }
        private static void InvokeOracleStartMatch()
        {
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) { Log.LogWarning("[P6.5] GameManager type not found"); return; }
                // Try the singleton accessor first — GameManager._instance is
                // set in Awake on the MainScene boot; persists if marked
                // DontDestroyOnLoad.
                object gmInst = null;
                var instanceGetter = AccessTools.PropertyGetter(gmType, "Instance");
                if ((object)instanceGetter != null)
                {
                    gmInst = instanceGetter.Invoke(null, null);
                }
                if ((object)gmInst == null)
                {
                    gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                }
                if ((object)gmInst == null)
                {
                    // Last resort: scan FindObjectsOfTypeAll (catches inactive + scene-less).
                    var includeInactive = Resources.FindObjectsOfTypeAll(gmType);
                    if (includeInactive != null && includeInactive.Length > 0)
                    {
                        gmInst = includeInactive[0];
                        Log.LogInfo($"[P6.5] GameManager found via FindObjectsOfTypeAll (count={includeInactive.Length}).");
                    }
                }
                if ((object)gmInst == null) { Log.LogWarning("[P6.5] GameManager instance not found (Instance/FindObjectOfType/FindObjectsOfTypeAll all null)"); return; }
                var mwType = AccessTools.TypeByName("MapWrapper");
                if ((object)mwType == null) { Log.LogWarning("[P6.5] MapWrapper type not found"); return; }

                int sceneIdx = (object)Instance != null ? Instance._currentSceneIndex : 6;
                var mapWrapper = Activator.CreateInstance(mwType);
                var mtField = AccessTools.Field(mwType, "MapType");
                var mdField = AccessTools.Field(mwType, "MapData");
                if ((object)mtField != null) mtField.SetValue(mapWrapper, (byte)0);
                if ((object)mdField != null) mdField.SetValue(mapWrapper, BitConverter.GetBytes(sceneIdx));

                var startMatchMethod = AccessTools.Method(gmType, "StartMatch", new[] { mwType, typeof(bool) });
                if ((object)startMatchMethod == null) { Log.LogWarning("[P6.5] StartMatch(MapWrapper,bool) method not found"); return; }
                Log.LogInfo($"[P6.5] Invoking GameManager.StartMatch(MapType=0, sceneIdx={sceneIdx}, MovePlayers=false).");
                startMatchMethod.Invoke(gmInst, new object[] { mapWrapper, false });
                Log.LogInfo("[P6.5] GameManager.StartMatch returned (no immediate exception).");
            }
            catch (Exception e)
            {
                Log.LogError($"[P6.5] InvokeOracleStartMatch threw: {e}");
            }
        }

        // === CLIENT-MODE SHIM ===
        // Runs on the user's graphical Steam client (NOT batchmode oracle).
        // Goal: make crate/destructible physics work locally so the user sees
        // boxes move when they push them. SF's stock client logic forces all
        // NSO rigidbodies kinematic (DisableAllRigidBodies in NSO.Init) and
        // sets static mHasControl=false (because IsServer is false on the
        // client) — both prevent local physics + local broadcasts.
        //
        // Two surgical patches let the client act as the local-physics
        // authority for boxes, with the oracle continuing as the network
        // coordinator. Doesn't try to flip IsServer entirely (which would
        // break weapon spawning on the client).
        // P0-12 — quantize the Vector2 key used for MapInfoSyncableBase
        // dictionary lookup. Stock SF stores objects by world-space
        // (position.y, position.z) with bit-exact float comparison. Float32
        // can differ by a few ULPs between server and clients at Awake
        // time, causing silent lookup failures → MapInfoSync packets
        // arrive but client never applies SetData. Round to 0.01 (1 cm) —
        // well below the spacing between platforms, well above any
        // realistic precision drift.
        private const float MapSyncKeyQuantum = 0.01f;
        internal static Vector2 QuantizeMapSyncKey(Vector2 v)
        {
            float invQ = 1f / MapSyncKeyQuantum;
            return new Vector2(
                Mathf.Round(v.x * invQ) / invQ,
                Mathf.Round(v.y * invQ) / invQ);
        }

        // Prefix on MultiplayerManager.AddMapDataObject(Vector2, MapInfoSyncableBase).
        // We can't change a struct argument by ref in a Harmony prefix on a
        // non-out parameter, but we CAN modify the dictionary via the
        // postfix side. Instead, intercept by writing the quantized key
        // back into the MapInfoSyncableBase's m_StartPos AND replacing
        // the pos arg via the __args array (Harmony allows this).
        internal static bool AddMapDataObjectPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return true;
                if (!(__args[0] is Vector2 pos)) return true;
                var quantized = QuantizeMapSyncKey(pos);
                __args[0] = quantized;
                // Also update m_StartPos on the MapInfoSyncableBase so
                // outbound SyncMapData broadcasts the quantized key.
                if (__args[1] != null)
                {
                    var t = __args[1].GetType();
                    var f = AccessTools.Field(t, "m_StartPos");
                    if ((object)f != null) f.SetValue(__args[1], quantized);
                }
            }
            catch { }
            return true;
        }

        // Prefix on MultiplayerManager.OnMapDataRecieved(byte[]).
        // The body's first 8 bytes are the Vector2 key the server sent.
        // After our AddMapDataObjectPrefix the server's keys are already
        // quantized, so the wire's key matches our dict — no action
        // needed here. But: if for some reason the server didn't quantize
        // (older oracle, race), an inbound un-quantized key would still
        // miss our quantized dict. Rewrite the body's first 8 bytes to
        // the quantized form as a belt-and-suspenders.
        internal static bool OnMapDataRecievedPrefix(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 8) return true;
                float x = BitConverter.ToSingle(data, 0);
                float y = BitConverter.ToSingle(data, 4);
                if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)) return true;
                var q = QuantizeMapSyncKey(new Vector2(x, y));
                var xBytes = BitConverter.GetBytes(q.x);
                var yBytes = BitConverter.GetBytes(q.y);
                Buffer.BlockCopy(xBytes, 0, data, 0, 4);
                Buffer.BlockCopy(yBytes, 0, data, 4, 4);
            }
            catch { }
            return true;
        }

        private static bool _mapSyncQuantizeInstalled;
        private static void InstallMapInfoSyncQuantize()
        {
            if (_mapSyncQuantizeInstalled) return;
            _mapSyncQuantizeInstalled = true;
            try
            {
                var mgrType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mgrType == null) { Log.LogWarning("[P0-12] MultiplayerManager not found."); return; }
                var harmony = new Harmony(PluginGuid + ".mapsync-quantize");
                var addM = AccessTools.Method(mgrType, "AddMapDataObject");
                if ((object)addM != null)
                {
                    harmony.Patch(addM, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(AddMapDataObjectPrefix))));
                    Log.LogInfo("[P0-12] Patched MultiplayerManager.AddMapDataObject (quantize Vector2 key to 0.01).");
                }
                else Log.LogWarning("[P0-12] AddMapDataObject not found.");
                var recvM = AccessTools.Method(mgrType, "OnMapDataRecieved");
                if ((object)recvM != null)
                {
                    harmony.Patch(recvM, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(OnMapDataRecievedPrefix))));
                    Log.LogInfo("[P0-12] Patched MultiplayerManager.OnMapDataRecieved (quantize inbound Vector2 key).");
                }
                else Log.LogWarning("[P0-12] OnMapDataRecieved not found.");
            }
            catch (Exception e) { Log.LogWarning($"[P0-12] install failed: {e.Message}"); }
        }

        private static void InstallClientModePatches()
        {
            try
            {
                var harmony = new Harmony(PluginGuid + ".client-shim");

                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType == null) { Log.LogWarning("[CLIENT] NetworkSyncableObject type not found."); return; }

                // REVERTED 2026-05-23 night — the "dynamic NSOs locally"
                // patch (Patch 1) caused random ice/chain destruction events
                // during normal gameplay: each client's local box physics
                // would do NSO-on-NSO collisions that fire DestructiblePiece
                // .Collide → SendDestructMessage → server → all clients see
                // a spurious break.
                //
                // Stock SF kinematic NSOs on clients:
                //   - NSO-on-NSO collisions don't fire OnCollisionEnter
                //     (both bodies kinematic — no contact resolution)
                //   - Player-on-NSO collisions still fire (player rig is
                //     dynamic) — so kicking ice still destructs correctly,
                //     just via the server-relay round-trip
                //   - Cost: pushing a box has ~RTT latency before it
                //     visually moves locally (the v26 snapshot drives it)
                //
                // Net: stable destruction model > instant push feedback.
                // Tradeoff is the same one P0-5's original fix took.
                Log.LogInfo("[CLIENT] Stock-default kinematic NSOs (no DisableAllRigidBodies skip — prevents spurious NSO-on-NSO destruction events).");
            }
            catch (Exception e)
            {
                Log.LogError($"[CLIENT] Client-mode shim install failed: {e}");
            }
        }
        // Generic skip-prefix: return false to skip the original method.
        internal static bool SkipPrefix() => false;

        // Skip Controller.RotateTowardsMouse in batchmode (keyboard-typed rigs
        // would otherwise have their aim overridden by the meaningless xvfb
        // mouse every frame — see the aim-fix patch install for details).
        internal static bool SkipRotateTowardsMousePrefix() => !_batchModeHost;

        // 2026-06-10 AUTO-PICKUP TICK: the stock pickup chain
        // (BodyPart.OnCollisionEnter → RequestWeaponPickUp) is broken twice
        // over for in-process rigs (mNetworkPlayer null NREs before the
        // request; the request's server path dies on empty registries). Rather
        // than patch every broken stock layer, arm rigs directly: every few
        // ticks, any unarmed spawned rig standing within reach of a settled,
        // pickup-able weapon picks it up via the same local replication the
        // prefix uses. Radius ~1.2m ≈ body contact, stock gates respected.
        private int _autoPickupTickCounter;
        internal void TickAutoPickup()
        {
            if (!_batchModeHost) return;
            if ((++_autoPickupTickCounter % 5) != 0) return;   // ~10-20Hz effective
            // 2.0m ≈ body radius + arm reach — walked-onto-it plausibility,
            // dense enough for stage-0 learning.
            try { TryLocalPickupSweep(2.0f, null, 0); }
            catch { }
            if ((_autoPickupTickCounter % 300) != 0) return;   // rest ~every 5s
            // On a fixed training map rounds advance WITHOUT a scene reload, so
            // unclaimed sky-drops accumulate forever (330+ observed) — physics
            // and scan drag stock play never has (players pick up; reloads
            // clear). Keep the newest 24, destroy the oldest beyond that.
            try { CapGroundWeapons(24); }
            catch { }
            // DSF comp ruleset: NO regen (stock regen heals 10 HP/s, erasing
            // the agent's damage during every ammo/pickup gap). regen comes
            // from PlayerPrefs (unset in our prefixes → 0) but re-assert
            // periodically so no later LoadOptions/packet path can flip it.
            // HP: stock match option (the same lobby setting DSF sets to 100).
            // SF_STAGE_HP (default 100) lets the curriculum shrink the kill
            // task — TakeDamage scales by 100/HP, so HP=25 means one clip
            // finishes the dummy and the win signal becomes dense; ramp
            // 25→50→100 as win_mean climbs. Clamped to [1,100]: 0 would make
            // every hit infinite damage.
            try
            {
                var ohT = AccessTools.TypeByName("OptionsHolder");
                if ((object)ohT != null)
                {
                    var regenF = AccessTools.Field(ohT, "regen");
                    if ((object)regenF != null && (int)regenF.GetValue(null) != 0) regenF.SetValue(null, 0);
                    var hpFld = AccessTools.Field(ohT, "HP");
                    if ((object)hpFld != null && (int)hpFld.GetValue(null) != StageHP) hpFld.SetValue(null, StageHP);
                }
            }
            catch { }
        }

        // Destroy the oldest non-permanent ground weapons beyond `max`.
        // Age = WeaponPickUp.counter (seconds since spawn, counts up).
        private static void CapGroundWeapons(int max)
        {
            var wpT = AccessTools.TypeByName("WeaponPickUp");
            if ((object)wpT == null) return;
            var arr = UnityEngine.Object.FindObjectsOfType(wpT);
            if (arr == null || arr.Length <= max) return;
            var counterF = AccessTools.Field(wpT, "counter");
            var unEndingF = AccessTools.Field(wpT, "unEnding");
            var flyUpF = AccessTools.Field(wpT, "flyUpAfter");
            var list = new List<KeyValuePair<float, Component>>();
            for (int i = 0; i < arr.Length; i++)
            {
                var w = arr[i] as Component;
                if ((object)w == null) continue;
                bool unEnding = false; float age = 0f; float flyUp = 0f;
                try
                {
                    if ((object)unEndingF != null) unEnding = (bool)unEndingF.GetValue(w);
                    if ((object)counterF != null) age = (float)counterF.GetValue(w);
                    if ((object)flyUpF != null) flyUp = (float)flyUpF.GetValue(w);
                }
                catch { }
                if (unEnding) continue;   // map-permanent weapons stay
                // Map-PRESET ground weapons (IsGroundWeapon ⇔ flyUpAfter=+inf)
                // are part of the map, oldest by counter, and never respawn on
                // a no-reload training map — exempt them from the cap.
                if (float.IsPositiveInfinity(flyUp)) continue;
                list.Add(new KeyValuePair<float, Component>(age, w));
            }
            if (list.Count <= max) return;
            list.Sort((a, b) => b.Key.CompareTo(a.Key));   // oldest first
            int kill = list.Count - max;
            for (int i = 0; i < kill; i++)
                UnityEngine.Object.Destroy(list[i].Value.gameObject);
            Log.LogInfo($"[weapons] ground-weapon cap: destroyed {kill} oldest (had {arr.Length}, cap {max})");
        }

        // 2026-06-10 PICKUP FIX (see patch-install comment). Stock
        // RequestWeaponPickUp's own server path dies on the oracle:
        // mSpawnedWeapons is empty and mConnectedClients has no PlayerObject
        // for in-process rigs. Resolve the picking rig + touched weapon
        // spatially and replicate the stock LOCAL pickup outcome.
        internal static bool RequestWeaponPickUpLocalPrefix(ushort __0, byte __1)
        {
            if (!_batchModeHost) return true;   // real client path elsewhere
            try
            {
                TryLocalPickupSweep(2.5f, __0, __1);
            }
            catch (Exception e)
            {
                var inner0 = (e is System.Reflection.TargetInvocationException tie0 && (object)tie0.InnerException != null)
                    ? tie0.InnerException : e;
                Log.LogWarning($"RequestWeaponPickUpLocalPrefix: {inner0}");
            }
            return false;   // never run the broken stock network path in batchmode
        }

        // GameManager.StartCountDown prefix — batchmode only. The stock
        // CountDownCoroutine is UI-driven (freeze clock → 3-2-1 → unfreeze);
        // headless, its UI calls can throw and kill the coroutine mid-freeze
        // (managerTime stuck 0, mPlayingCountdown latched true — the Ice11
        // clock wedge). Apply the end state synchronously and skip it.
        internal static bool StartCountDownBatchPrefix(object __instance)
        {
            if (!_batchModeHost) return true;   // real clients keep stock behavior
            try
            {
                var gmT = AccessTools.TypeByName("GameManager");
                var thT = AccessTools.TypeByName("TimeHandler");
                if ((object)gmT != null)
                {
                    var inFightF = AccessTools.Field(gmT, "inFight");
                    if ((object)inFightF != null) inFightF.SetValue(null, true);
                    var playingF = AccessTools.Field(gmT, "mPlayingCountdown");
                    if ((object)playingF != null && (object)__instance != null)
                        playingF.SetValue(__instance, false);
                }
                if ((object)thT != null)
                {
                    var mtF = AccessTools.Field(thT, "managerTime");
                    if ((object)mtF != null) mtF.SetValue(null, 1f);
                }
                Log.LogInfo("[time] StartCountDown (batch): applied end state synchronously (inFight=true, managerTime=1).");
            }
            catch (Exception e)
            {
                Log.LogWarning($"StartCountDownBatchPrefix: {e.Message}");
            }
            return false;   // skip the stock coroutine entirely
        }

        // Shared local-pickup core: nearest (unarmed, alive rig | pickup-able
        // weapon) pair within `radius` → replicate the stock LOCAL pickup
        // (Fighting.PickUpWeapon + destroy). Used by both the request prefix
        // (radius 2.5, collision-confirmed) and the auto-pickup tick (1.2).
        internal static void TryLocalPickupSweep(float radius, ushort? reqIdx, byte reqPlayer)
        {
            var wpType = AccessTools.TypeByName("WeaponPickUp");
            var bpType = AccessTools.TypeByName("BodyPart");
            var fgType = AccessTools.TypeByName("Fighting");
            var ciT = AccessTools.TypeByName("CharacterInformation");
            if ((object)wpType == null || (object)fgType == null) return;
            var idF = AccessTools.Field(wpType, "id");
            var unEndingF = AccessTools.Field(wpType, "unEnding");
            var abilityF = AccessTools.Field(wpType, "sendTheMovementAbility");
            var counterF = AccessTools.Field(wpType, "counter");
            var cantF = AccessTools.Field(wpType, "cantBePickledUpFor");
            var weaponFldF = AccessTools.Field(fgType, "weapon");
            var deadFldF = ((object)ciT != null) ? AccessTools.Field(ciT, "isDead") : null;

            var weapons = UnityEngine.Object.FindObjectsOfType(wpType);
            if (weapons == null || weapons.Length == 0) return;
            Component bestW = null; Component bestFg = null; GameObject bestRig = null; float bestD = radius;
            foreach (var kv in SlotToRig)
            {
                var rig = kv.Value;
                if ((object)rig == null) continue;
                var fg = rig.GetComponentInChildren(fgType);
                if ((object)fg == null) continue;
                // only UNARMED, ALIVE rigs pick up (stock caller gates)
                try
                {
                    if ((object)weaponFldF != null && (object)weaponFldF.GetValue(fg) != null) continue;
                    if ((object)deadFldF != null)
                    {
                        var cinf = rig.GetComponentInChildren(ciT);
                        if ((object)cinf != null && (bool)deadFldF.GetValue(cinf)) continue;
                    }
                }
                catch { }
                Vector3 rp = rig.transform.position;
                if ((object)bpType != null)
                {
                    var bp = rig.GetComponentInChildren(bpType) as Component;
                    if ((object)bp != null) rp = bp.transform.position;
                }
                for (int i = 0; i < weapons.Length; i++)
                {
                    var w = weapons[i] as Component;
                    if ((object)w == null) continue;
                    try
                    {
                        if ((object)counterF != null && (float)counterF.GetValue(w) <= 0.3f) continue;
                        if ((object)cantF != null && (float)cantF.GetValue(w) >= 0f) continue;
                    }
                    catch { }
                    float d = Vector3.Distance(rp, w.transform.position);
                    if (d < bestD) { bestD = d; bestW = w; bestRig = rig; bestFg = fg as Component; }
                }
            }
            if ((object)bestW == null || (object)bestRig == null)
            {
                if (reqIdx.HasValue)
                    Log.LogWarning($"[pickup] request (idx={reqIdx} player={reqPlayer}) but no (rig,weapon) pair within {radius:0.0}m — dropped");
                return;
            }
            int wid = ((object)idF != null) ? (int)idF.GetValue(bestW) : 0;
            int ability = ((object)abilityF != null) ? (int)abilityF.GetValue(bestW) : -1;
            bool unEnding = (object)unEndingF != null && (bool)unEndingF.GetValue(bestW);
            if (ability != -1)
            {
                try
                {
                    var maType = AccessTools.TypeByName("MovementAbility");
                    if ((object)maType != null)
                    {
                        var ma = bestRig.GetComponentInChildren(maType);
                        var setM = ((object)ma != null) ? AccessTools.Method(maType, "SetAbility") : null;
                        if ((object)setM != null) setM.Invoke(ma, new object[] { ability });
                    }
                }
                catch { }
            }
            var pick = AccessTools.Method(fgType, "PickUpWeapon");
            if ((object)pick == null) return;
            pick.Invoke(bestFg, new object[] { wid, bestW.gameObject });
            if (!unEnding) UnityEngine.Object.Destroy(bestW.gameObject);
            Log.LogInfo($"[pickup] armed rig (weapon id={wid}, d={bestD:0.00}m, via={(reqIdx.HasValue ? "request" : "tick")})");
        }

        // Headless oracle has NO Steam: the stock IsP2PPacketAvailable always
        // throws "Steamworks is not initialized" (via TestIfAvailableClient),
        // and ListenForPackages can also NullRef on a null channel — both every
        // frame, each with a multi-line stack trace. Unfiltered that wrote tens
        // of MB/s per oracle; on 2026-06-06 the combined dirty-page writeback
        // storm across the fleet hard-froze the box for ~13h. There are no real
        // P2P packets to read in a local single-process host, so ALWAYS report
        // "none" and skip the original entirely — this also prevents the
        // downstream ListenForPackages NullRef (it never tries to read a packet).
        internal static bool IsPacketAvailableHeadlessPrefix(object __instance, int channel, ref bool __result)
        {
            if (!_batchModeHost) return true;
            __result = false;
            return false;
        }

        // NSO.Start postfix on client: force the static mHasControl=true so
        // the client's NSO.LateUpdate broadcasts position deltas.
        internal static void NsoStartPostfix_Client(object __instance)
        {
            try
            {
                var t = __instance.GetType();
                var f = AccessTools.Field(t, "mHasControl");
                if ((object)f != null) f.SetValue(null, true);
            }
            catch { /* swallow — Mono inlining may have us miss */ }
        }

        // Per-patch install with status tracking. A single try/catch around
        // the whole block silently skipped patches if any one threw early.
        // Failures now accumulate in _p65MissingPatches for a post-install
        // summary line.
        private static int _p65PatchesAttempted;
        private static int _p65PatchesSucceeded;
        private static readonly List<string> _p65MissingPatches = new List<string>();
        private static void TryPatch(Harmony harmony, string label, MethodInfo target, string prefix = null, string postfix = null)
        {
            _p65PatchesAttempted++;
            if ((object)target == null)
            {
                _p65MissingPatches.Add($"{label} — target method not found");
                Log.LogError($"[P6.5] SKIP {label} — target method not found.");
                return;
            }
            try
            {
                var pfx = prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(Plugin), prefix)) : null;
                var pst = postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(Plugin), postfix)) : null;
                harmony.Patch(target, prefix: pfx, postfix: pst);
                _p65PatchesSucceeded++;
                Log.LogInfo($"[P6.5] Patched {label}.");
            }
            catch (Exception e)
            {
                _p65MissingPatches.Add($"{label} — {e.GetType().Name}: {e.Message}");
                Log.LogError($"[P6.5] FAIL {label}: {e}");
            }
        }

        // Safe-unbox helpers. Mono's runtime is permissive about typed casts on
        // boxed enums, but a direct `(byte)box` can throw InvalidCastException
        // on a stricter CLR. Convert.* uses IConvertible which handles both
        // raw primitives and byte-backed enums uniformly.
        private static byte UnboxByte(object o)
        {
            if (o == null) return (byte)255;
            try { return (byte)Convert.ToInt32(o); } catch { return (byte)255; }
        }
        private static ulong UnboxUlong(object o)
        {
            if (o == null) return 0uL;
            try { return Convert.ToUInt64(o); } catch { return 0uL; }
        }

        // SF MsgType enum (P2PPackageHandler.MsgType byte values, from decompile).
        private static string MsgTypeName(byte b) => b switch
        {
            0  => "Ping",
            1  => "PingResponse",
            2  => "ClientJoined",
            3  => "ClientRequestingAccepting",
            4  => "ClientAccepted",
            5  => "ClientInit",
            6  => "ClientRequestingIndex",
            7  => "ClientRequestingToSpawn",
            8  => "ClientSpawned",
            9  => "ClientReadyUp",
            10 => "PlayerUpdate",
            11 => "PlayerTookDamage",
            12 => "PlayerTalked",
            13 => "PlayerForceAdded",
            14 => "PlayerForceAddedAndBlock",
            15 => "PlayerLavaForceAdded",
            16 => "PlayerFallOut",
            17 => "PlayerWonWithRicochet",
            18 => "MapChange",
            19 => "WeaponSpawned",
            20 => "WeaponThrown",
            21 => "RequestingWeaponThrow",
            22 => "ClientRequestWeaponDrop",
            23 => "WeaponDropped",
            24 => "WeaponWasPickedUp",
            25 => "ClientRequestingWeaponPickUp",
            26 => "ObjectUpdate",
            27 => "ObjectSpawned",
            28 => "ObjectSimpleDestruction",
            29 => "ObjectInvokeDestructionEvent",
            30 => "ObjectDestructionCollision",
            31 => "GroundWeaponsInit",
            32 => "MapInfo",
            33 => "MapInfoSync",
            34 => "WorkshopMapsLoaded",
            35 => "StartMatch",
            36 => "ObjectHello",
            37 => "OptionsChanged",
            38 => "KickPlayer",
            _  => "?"
        };

        private static float ReadAxis(object actions, string fieldName, string axis)
        {
            try
            {
                var f = AccessTools.Field(actions.GetType(), fieldName);
                if ((object)f == null) return 0f;
                var ctrl = f.GetValue(actions);
                if ((object)ctrl == null) return 0f;
                var t = ctrl.GetType();
                var backing = AccessTools.Field(t, "<" + axis + ">k__BackingField");
                if ((object)backing != null) return (float)backing.GetValue(ctrl);
                var prop = AccessTools.Property(t, axis);
                if ((object)prop != null) return (float)prop.GetValue(ctrl, null);
            }
            catch { }
            return float.NaN;
        }

        // Force the named TwoAxisInputControl's X/Y to (x, y) by writing
        // its private fields. Run from the Harmony prefix on Controller.Update.
        private static void ForceTwoAxis(object actions, string fieldName, float x, float y)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, new Vector2(x, y));
            var xBacking = AccessTools.Field(t, "<X>k__BackingField");
            var yBacking = AccessTools.Field(t, "<Y>k__BackingField");
            if ((object)xBacking != null) xBacking.SetValue(ctrl, x);
            if ((object)yBacking != null) yBacking.SetValue(ctrl, y);
        }

        // Force a button's IsPressed / Value via InputControlState struct
        // backing fields. PlayerAction.thisState is an InputControlState
        // struct with public bool State and public float Value; reading
        // IsPressed returns thisState.State.
        private static void ForceButton(object actions, string fieldName, bool pressed)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var pa = f.GetValue(actions);
            if ((object)pa == null) return;
            // OneAxisInputControl has private thisState field.
            var thisStateField = AccessTools.Field(pa.GetType(), "thisState");
            if ((object)thisStateField == null) return;
            // thisState is a struct. We have to box, mutate, write back.
            object state = thisStateField.GetValue(pa);
            if ((object)state == null) return;
            var stateType = state.GetType();
            var stateField = AccessTools.Field(stateType, "State");
            var valueField = AccessTools.Field(stateType, "Value");
            if ((object)stateField != null) stateField.SetValue(state, pressed);
            if ((object)valueField != null) valueField.SetValue(state, pressed ? 1.0f : 0.0f);
            thisStateField.SetValue(pa, state);
        }

        // Boot is driven by Update() as a state machine because Unity 5.6's
        // Mono runtime is missing IteratorStateMachineAttribute (emitted by the
        // C# compiler for any method with `yield return`). Using a plain
        // state machine keeps the assembly compatible.
        private enum BootState { Idle, WaitForInit, LoadingScene, WaitingForSceneSettle, HostStarting, Running }

        private BootState _bootState = BootState.Idle;
        private float _bootStartedAt;
        private float _stateSince;
        private AsyncOperation _loadOp;
        private int _settleFrames;
        private int _heartbeatTicks;
        private float _lastHeartbeat;
        // Rolling counters for the heartbeat status line — diffed against
        // current totals each interval to compute per-second rates.
        private long  _heartbeatLastPkt;
        private uint  _heartbeatLastSnap;
        private uint  _heartbeatLastInput;

        private int _updateErrorTicks;
        private string _updateErrorFirstStackTrace;

        // SF_TIMESCALE (2026-06-09): run the headless sim at N× wall speed for
        // training throughput. Physics is IDENTICAL per game-second (Unity just
        // runs more FixedUpdates of the same fixedDeltaTime per wall-second).
        // The game's TimeHandler.Update rewrites Time.timeScale EVERY frame
        // (managerTime normally 1, lerped to 0 for the stock round-end slowmo,
        // pauseTime for pause), so we assert our scale in LateUpdate (runs
        // after TimeHandler) and ONLY when the game intends normal speed —
        // stock slowmo/pause behavior is preserved exactly.
        // REFLECTION, not a direct TimeHandler reference: refs/Assembly-CSharp.dll
        // carries the SERVER assembly identity ("Assembly-CSharp.srv"), so direct
        // game-type references fail to resolve at runtime (FileNotFoundException
        // on JIT, spamming every frame — that's why this codebase reflects all
        // game types; learned the hard way 2026-06-09).
        private static FieldInfo _thPauseF, _thManagerF;
        private static bool _thLookupDone;
        private float _clockFrozenSince = -1f;   // realtime when a frozen clock was first seen with rigs present
        private float _lastWedgeBreakAt = -999f; // realtime of the last wedge break (burst-mode window)
        private void LateUpdate()
        {
            if (!_batchModeHost) return;
            // Frame cap re-assert: OptionsHolder/SetTargetFrameRate overwrite
            // targetFrameRate from settings (uncapped headless = ~2.5 wasted
            // cores/instance rendering for nobody).
            if (Application.targetFrameRate != 60) Application.targetFrameRate = 60;
            if (!_thLookupDone)
            {
                _thLookupDone = true;
                var tht = AccessTools.TypeByName("TimeHandler");
                if ((object)tht != null)
                {
                    _thPauseF = AccessTools.Field(tht, "pauseTime");
                    _thManagerF = AccessTools.Field(tht, "managerTime");
                }
                Log.LogInfo($"[timescale] TimeHandler fields resolved: pause={( (object)_thPauseF != null )} manager={( (object)_thManagerF != null )} target={TrainTimeScale:0.0}x");
            }
            if ((object)_thPauseF == null || (object)_thManagerF == null) return;
            try
            {
                // CLOCK-WEDGE BREAKER (2026-06-10, found on Ice11/scene 57):
                // some maps' transition freeze sets TimeHandler.managerTime=0
                // and the ramp-back coroutine never runs headless, leaving
                // Time.timeScale at 0 FOREVER — no physics, no countdown, no
                // stall-advance; rigs pinned at spawn, reward flat 0. Stock
                // pauses are transient (round slowmo / map change, a few
                // seconds), so a frozen clock with rigs spawned for >5 REAL
                // seconds is always the wedge. Restore the game's own
                // normal-speed state (the stock TimeHandler.Update then
                // recomputes timeScale=1 from these fields every frame).
                if (Time.timeScale < 0.01f && SlotToRig.Count > 0)
                {
                    // Burst mode: the first wedge waits 5s (don't fight a
                    // legitimate transient pause), but when the freeze is
                    // recurring per-round (Ice11: every map-entry re-freezes
                    // and the stock restore path is dead), waiting 5s every
                    // ~18s round taxes ~25% of wall time — break repeats
                    // after 2s while in the burst window.
                    float thresh = (Time.realtimeSinceStartup - _lastWedgeBreakAt < 120f) ? 2f : 5f;
                    if (_clockFrozenSince < 0f)
                        _clockFrozenSince = Time.realtimeSinceStartup;
                    else if (Time.realtimeSinceStartup - _clockFrozenSince > thresh)
                    {
                        float pause0 = (float)_thPauseF.GetValue(null);
                        float mgr0 = (float)_thManagerF.GetValue(null);
                        _thPauseF.SetValue(null, 1f);
                        _thManagerF.SetValue(null, 1f);
                        Time.timeScale = (TrainTimeScale > 1f) ? TrainTimeScale : 1f;
                        Log.LogWarning($"[time] WEDGE-BREAKER: clock frozen >{thresh:0}s in-match (pause={pause0:0.###} manager={mgr0:0.###}, scene={SceneManager.GetActiveScene().name}) — forced both to 1.");
                        _clockFrozenSince = -1f;
                        _lastWedgeBreakAt = Time.realtimeSinceStartup;
                    }
                }
                else
                {
                    _clockFrozenSince = -1f;
                }
                if (TrainTimeScale > 1f)
                {
                    float pause = (float)_thPauseF.GetValue(null);
                    float mgr = (float)_thManagerF.GetValue(null);
                    if (pause == 1f && mgr == 1f && Time.timeScale != TrainTimeScale)
                        Time.timeScale = TrainTimeScale;
                }
            }
            catch { }
        }

        private void Update()
        {
            try
            {
                StepBoot();
            }
            catch (Exception e)
            {
                _updateErrorTicks++;
                // Don't kill the boot state; just log periodically so we can see what's wrong.
                // Print full stack trace separately — `{e}` formatting via Mono sometimes
                // elides frames. Capture first stack trace + log it on every rate-limited
                // print so it survives in long-running log truncation. Resolves diagnostic
                // half of Bug #45 in notes/bug-investigations/2026-05-24_v0.3.4-session-bugs.md.
                if (_updateErrorFirstStackTrace == null)
                    _updateErrorFirstStackTrace = e.StackTrace ?? "(no stack)";
                if (_updateErrorTicks <= 5 || _updateErrorTicks % 300 == 0)
                {
                    Log.LogError($"SFHeadlessHost.Update (count={_updateErrorTicks}) {e.GetType().Name}: {e.Message}");
                    Log.LogError($"  inner: {e.InnerException?.GetType().Name}: {e.InnerException?.Message}");
                    Log.LogError($"  stack[first]: {_updateErrorFirstStackTrace}");
                    Log.LogError($"  stack[current]: {e.StackTrace}");
                }
            }
        }

        private void StepBoot()
        {
            switch (_bootState)
            {
                case BootState.Idle:
                    return;

                case BootState.WaitForInit:
                    // 2 second settle to let BepInEx + Unity main-thread init.
                    if (Time.realtimeSinceStartup - _bootStartedAt < 2.0f) return;
                    Log.LogInfo($"Step 1: SceneManager.LoadScene({InitialScene}, Single)");
                    try
                    {
                        _loadOp = SceneManager.LoadSceneAsync(InitialScene, LoadSceneMode.Single);
                    }
                    catch (Exception e)
                    {
                        Log.LogError($"LoadSceneAsync({InitialScene}) threw: {e}");
                        _bootState = BootState.Idle;
                        return;
                    }
                    if (_loadOp == null)
                    {
                        Log.LogError($"LoadSceneAsync({InitialScene}) returned null.");
                        _bootState = BootState.Idle;
                        return;
                    }
                    _bootState = BootState.LoadingScene;
                    _stateSince = Time.realtimeSinceStartup;
                    return;

                case BootState.LoadingScene:
                    if (_loadOp == null || _loadOp.isDone)
                    {
                        var s = SceneManager.GetActiveScene();
                        Log.LogInfo($"Scene loaded: {s.name} (buildIndex={s.buildIndex})");
                        _bootState = BootState.WaitingForSceneSettle;
                        _settleFrames = 0;
                    }
                    else if (Time.realtimeSinceStartup - _stateSince > 30.0f)
                    {
                        Log.LogError("Scene load timed out after 30s — aborting.");
                        _bootState = BootState.Idle;
                    }
                    return;

                case BootState.WaitingForSceneSettle:
                    // Wait a few frames so Awake/Start on the new scene's objects finishes.
                    if (++_settleFrames < 3) return;
                    _bootState = BootState.HostStarting;
                    return;

                case BootState.HostStarting:
                    StartHost();
                    StartBridge();
                    // Cache playerPrefab while ControllerHandler still exists
                    // in MainScene — needed because subsequent loadMap(Single)
                    // destroys it but we still want to spawn rigs in any scene.
                    TryCachePlayerPrefab();
                    _bootState = BootState.Running;
                    _lastHeartbeat = Time.realtimeSinceStartup;
                    _lastStateEmit = Time.realtimeSinceStartup;
                    return;

                case BootState.Running:
                    // Drain any incoming bridge commands (debug bridge on 1341).
                    DrainBridgeCommands();
                    // Drain raw v25 protocol packets from patched DLL clients.
                    DrainSfServer();
                    // Drop stale clients so we don't keep forwarding broadcasts
                    // to ghosts after ungraceful disconnects.
                    SweepStaleClients();
                    // Fire scheduled match-start if /start was issued (user-driven now,
                    // no longer auto-armed by ClientRequestingToSpawn).
                    if (_autoStartAt > 0f && Time.realtimeSinceStartup >= _autoStartAt && !_matchStarted)
                    {
                        _autoStartAt = -1f;
                        FireMatchStart("scheduled");
                    }
                    // Phase 6.5 Step 2 — kick GameManager.StartMatch on the oracle
                    // so the StartMapSequence coroutine runs (additively loads
                    // the scene + sets up the map).
                    if (_oracleStartMatchAt > 0f && Time.realtimeSinceStartup >= _oracleStartMatchAt && !_oracleStartMatchFired)
                    {
                        _oracleStartMatchAt = -1f;
                        _oracleStartMatchFired = true;
                        InvokeOracleStartMatch();
                        // Schedule StartCountDown 3s later — after StartMapSequence
                        // has had time to do its TimeHandler decay + LoadMap +
                        // 1.1s WaitForSecondsRealtime. StartCountDown's own
                        // coroutine yields 1s then flips inFight=true, which is
                        // what makes the weapon-spawn counter actually tick.
                        _oracleCountDownAt = Time.realtimeSinceStartup + 3.0f;
                        _oracleCountDownFired = false;
                        Log.LogInfo("[P6.5] Scheduled GameManager.StartCountDown in 3s (flips inFight=true).");
                    }
                    // Phase 6.5 Step 2b — kick StartCountDown so inFight goes true.
                    if (_oracleCountDownAt > 0f && Time.realtimeSinceStartup >= _oracleCountDownAt && !_oracleCountDownFired)
                    {
                        _oracleCountDownAt = -1f;
                        _oracleCountDownFired = true;
                        InvokeOracleStartCountDown();
                        // Schedule NSO inventory 4s later — gives StartMapSequence
                        // + PrepareMapForTravel + InitSyncedObjects time to settle.
                        _nsoInventoryAt = Time.realtimeSinceStartup + 4.0f;
                        _nsoInventoryDone = false;
                    }
                    if (_nsoInventoryAt > 0f && Time.realtimeSinceStartup >= _nsoInventoryAt && !_nsoInventoryDone)
                    {
                        _nsoInventoryAt = -1f;
                        RunNetworkSyncableObjectInventory();
                        // Schedule authoritative-player spawn after NSO state is fixed.
                        _authSpawnAt = Time.realtimeSinceStartup + 1.0f;
                        // Schedule scripted-bot spawn slightly later so the map
                        // scene + spawn points are fully settled first.
                        if (AutoSpawnBotSlots != null && AutoSpawnBotSlots.Count > 0)
                            _botAutoSpawnAt = Time.realtimeSinceStartup + 5.0f;
                    }
                    // Phase 6.9 — spawn real NetworkPlayers per connected client.
                    // They're the server's authoritative copy; eventually driven
                    // by client inputs (Phase 6.12) and broadcast back to all
                    // clients as snapshot (Phase 6.10) for reconciliation.
                    if (_authSpawnAt > 0f && Time.realtimeSinceStartup >= _authSpawnAt && !_authSpawnDone)
                    {
                        _authSpawnAt = -1f;
                        _authSpawnDone = true;
                        SpawnAuthoritativePlayersForAllClients();
                    }
                    // ── Scripted-bot lifecycle (SFGYM_BOT_SLOTS) ──
                    if (_botAutoSpawnAt > 0f && Time.realtimeSinceStartup >= _botAutoSpawnAt && !_botAutoSpawnDone)
                    {
                        _botAutoSpawnAt = -1f;
                        _botAutoSpawnDone = true;
                        AutoSpawnBots();
                    }
                    // Bots have no remote client to fire /start, so bootstrap a
                    // FireMatchStart ourselves shortly after boot.
                    if (AutoSpawnBotSlots != null && AutoSpawnBotSlots.Count > 0 && !_botBootstrapFireDone && _botBootstrapFireAt < 0f)
                        _botBootstrapFireAt = Time.realtimeSinceStartup + 2.0f;
                    if (_botBootstrapFireAt > 0f && Time.realtimeSinceStartup >= _botBootstrapFireAt && !_botBootstrapFireDone)
                    {
                        _botBootstrapFireAt = -1f;
                        _botBootstrapFireDone = true;
                        if (!_matchStarted)
                        {
                            Log.LogInfo("[bot-spawn] bootstrapping FireMatchStart for SFGYM_BOT_SLOTS auto-spawn.");
                            FireMatchStart("SFGYM_BOT_SLOTS bootstrap");
                        }
                    }
                    TickBotRoundRearm();
                    DriveScriptedBots();
                    // Round advance: kill detected → fire MapChange after delay.
                    if (_pendingRoundAdvanceAt > 0f && Time.realtimeSinceStartup >= _pendingRoundAdvanceAt)
                    {
                        _pendingRoundAdvanceAt = -1f;
                        AdvanceRound();
                    }
                    if (_pendingClientStartMatchAt > 0f && Time.realtimeSinceStartup >= _pendingClientStartMatchAt && !_pendingClientStartMatchFired)
                    {
                        _pendingClientStartMatchAt = -1f;
                        _pendingClientStartMatchFired = true;
                        BroadcastStartMatch();
                        Log.LogInfo("[SF] Deferred StartMatch sent to clients (post MapChange load window).");
                    }
                    // After MapChange settles, send StartMatch to kick the next round's countdown.
                    if (_pendingStartMatchAt > 0f && Time.realtimeSinceStartup >= _pendingStartMatchAt)
                    {
                        _pendingStartMatchAt = -1f;
                        BroadcastStartMatch();
                        Log.LogInfo("[SF] Round advance: StartMatch sent.");
                    }
                    if (_pendingRearmCombatAt > 0f && Time.realtimeSinceStartup >= _pendingRearmCombatAt)
                    {
                        _pendingRearmCombatAt = -1f;
                        RearmOracleCombatLoop("delayed-post-StartMatch");
                        FlushGroundWeaponsAfterCheck("post-StartMatch");
                    }
                    TickOracleMapLoadTimeout();
                    TickPeriodicWeaponRearm();
                    TickOracleSkyWeaponSpawner();
                    // Push the latest per-slot inputs into each spawned rig's
                    // CharacterActions. Done every frame even if no new input
                    // arrived — analog sticks need their last value held so
                    // the rig keeps moving between input packets.
                    WriteInputsToRigs();
                    // Walk-over weapon pickup for spawned rigs (the stock
                    // collision→network pickup chain is dead headless).
                    TickAutoPickup();
                    // Emit a state snapshot at 30 Hz if anyone has pinged us.
                    if (_bridgePeer != null && Time.realtimeSinceStartup - _lastStateEmit >= (1.0f / 30.0f))
                    {
                        _lastStateEmit = Time.realtimeSinceStartup;
                        EmitStateSnapshot();
                    }
                    var interval = Verbose ? 5.0f : 30.0f;
                    if (Time.realtimeSinceStartup - _lastHeartbeat >= interval)
                    {
                        float elapsed = Time.realtimeSinceStartup - _lastHeartbeat;
                        _lastHeartbeat = Time.realtimeSinceStartup;
                        _heartbeatTicks++;
                        // Rates over the interval window.
                        float pktRate   = (_sfPacketsRx        - _heartbeatLastPkt)   / elapsed;
                        float snapRate  = (_serverTick         - _heartbeatLastSnap)  / elapsed;
                        float inputRate = (_inputPacketsRx     - _heartbeatLastInput) / elapsed;
                        _heartbeatLastPkt   = _sfPacketsRx;
                        _heartbeatLastSnap  = _serverTick;
                        _heartbeatLastInput = _inputPacketsRx;
                        int spawned = 0, connected = 0;
                        foreach (var kv in _sfClients) { connected++; if (kv.Value.Spawned) spawned++; }
                        Log.LogInfo($"heartbeat: scene={SceneManager.GetActiveScene().name} tick={_heartbeatTicks} | clients={connected} spawned={spawned} | rx={pktRate:0.0}/s snap={snapRate:0.0}/s input={inputRate:0.0}/s | rigs={SlotToRig.Count} matchStarted={_matchStarted}");
                    }
                    // Phase 6.5 — periodic state probe (only after match has started).
                    if (_matchStarted)
                    {
                        StateProbe();
                        TickNsoProbe();
                        TickNsoFallGuard();
                        TickStaleNsoFreezer();
                        TickGroundWeaponsRetry();
                        TickMapSyncRetry();
                        TickOracleMapInfoBootstrap();
                        TickOraclePreCombatGrace();
                        TickBoxDiagnostic();
                        TickAuthRigDeathCheck();
                    }
                    // Phase 6.17 — advance virtual projectiles each frame.
                    TickProjectiles();
                    // Phase 6.10 — 30Hz authoritative-state broadcast (msgType 39).
                    TickWorldStateSnapshot();
                    return;
            }
        }

        // ========== Bridge: UDP socket the Go server talks to ==========
        // Wire format v0 (JSON, easy to debug; will go binary in v1 if needed):
        //   Go → Oracle commands:
        //     {"cmd":"ping"}
        //     {"cmd":"loadMap","scene":N}
        //     {"cmd":"snapshot"}  -- request a one-shot snapshot
        //     {"cmd":"sub"}       -- subscribe to 30Hz snapshot stream (default after first contact)
        //   Oracle → Go responses (always JSON, one packet each):
        //     {"reply":"pong","tick":N,"scene":"X"}
        //     {"reply":"snapshot","tick":N,"scene":"X","ents":[{"slot":i,"x":...,"y":...,"z":...,"vx":...,"vy":...,"vz":...}]}
        //     {"reply":"ack","cmd":"loadMap","ok":true}

        private UdpClient _bridge;
        private IPEndPoint _bridgePeer; // last sender; we reply to whoever pinged us last

        // Path A: oracle's own raw-UDP socket speaking the v25 protocol
        // directly to patched DLL clients. Bound on BindPort (typically 1337).
        private UdpClient _sfServer;
        private long _sfPacketsRx;
        private long _sfPacketsTx;

        // V25 protocol packet types (mirror packets.go iota order).
        private const byte PktPing                          = 0;
        private const byte PktPingResponse                  = 1;
        private const byte PktClientJoined                  = 2;
        private const byte PktClientRequestingAccepting     = 3;
        private const byte PktClientAccepted                = 4;
        private const byte PktClientInit                    = 5;
        private const byte PktClientRequestingIndex         = 6;
        private const byte PktClientRequestingToSpawn       = 7;
        private const byte PktClientSpawned                 = 8;
        private const byte PktClientReadyUp                 = 9;
        private const byte PktPlayerUpdate                  = 10;
        private const byte PktPlayerTookDamage              = 11;
        private const byte PktPlayerTalked                  = 12;
        private const byte PktPlayerForceAdded              = 13;
        private const byte PktPlayerForceAddedAndBlock      = 14;
        private const byte PktPlayerLavaForceAdded          = 15;
        private const byte PktPlayerFallOut                 = 16;
        private const byte PktPlayerWonWithRicochet         = 17;
        private const byte PktMapChange                     = 18;
        private const byte PktWeaponSpawned                 = 19;
        private const byte PktWeaponThrown                  = 20;
        private const byte PktRequestingWeaponThrow         = 21;
        private const byte PktClientRequestWeaponDrop       = 22;
        private const byte PktWeaponDropped                 = 23;
        private const byte PktWeaponWasPickedUp             = 24;
        private const byte PktClientRequestingWeaponPickUp  = 25;
        private const byte PktObjectUpdate                  = 26;
        private const byte PktObjectSpawned                 = 27;
        private const byte PktObjectSimpleDestruction       = 28;
        private const byte PktObjectInvokeDestructionEvent  = 29;
        private const byte PktObjectDestructionCollision    = 30;
        private const byte PktGroundWeaponsInit             = 31;
        private const byte PktMapInfo                       = 32;
        private const byte PktMapInfoSync                   = 33;
        private const byte PktWorkshopMapsLoaded            = 34;
        private const byte PktStartMatch                    = 35;
        private const byte PktObjectHello                   = 36;
        private const byte PktOptionsChanged                = 37;
        private const byte PktKickPlayer                    = 38;
        // === v26 extension (Phase 6.10+) — server-authoritative protocol ===
        // Stock SF's MsgType enum stops at KickPlayer=38. We extend with new
        // types for the prediction+reconciliation architecture. Stock clients
        // (no v26 plugin loaded) receive these and ignore via default case
        // in P2PPackageHandler.CheckMessageType.
        private const byte PktWorldStateSnapshot            = 39;  // server → all clients, 30Hz
        private const byte PktPlayerInput                   = 40;  // client → server, 60Hz (Phase 6.12)
        private const byte PktClientFireWeapon              = 41;  // client → server, on Weapon.ActuallyShoot (Phase 6.17)
        private const byte PktV26Announce                   = 42;  // server → all clients, UTF-8 banner text (recon plugin draws it 3s)
        // === Patched-DLL extensions (kit's patched Assembly-CSharp.dll has
        // these beyond stock SF's 0-38 range). We don't synthesize them, but
        // we relay so peer clients see each other. From ALKA's
        // relay_handlers.go (his P1-4 fix).
        private const byte PktLerpPlayer                    = 56;  // empty body, triggers remote-lerp on NetworkPlayer
        private const byte PktColorChanged                  = 57;  // HTML color string body (4-64 bytes)

        // Per-client connection state. Keyed by remote address:port string so
        // the same SF instance keeps its slot/SteamID across packets.
        private class SfClient
        {
            public IPEndPoint Addr;
            public ulong SteamID;
            public int Slot;
            public float LastSeen;
            public bool Accepted;
            public bool Initialized;
            public bool Spawned;
            // Phase 6.12 — last PktPlayerInput sequence number consumed for
            // this slot. Phase 6.12.2 will stamp this into outgoing snapshots
            // so the client can do reconciliation replay.
            public uint LastInputSeq;
            // Phase 6.15.1 — has the server-emitted welcome chat been sent yet?
            public bool SentWelcome;
        }
        private readonly Dictionary<string, SfClient> _sfClients = new Dictionary<string, SfClient>();
        private float _lastStateEmit;
        private long _bridgeTick;

        // Slot → spawned Player rig GameObject (populated by TrySpawnPlayer).
        // Used by the input-injection path to find which rig to drive.
        private static readonly Dictionary<int, GameObject> SlotToRig = new Dictionary<int, GameObject>();

        // Cached player prefab — captured the first time we find ControllerHandler
        // in the active scene (MainScene). Survives subsequent scene changes so we
        // can spawn rigs in Landfall scenes (which have no ControllerHandler).
        private static GameObject _cachedPlayerPrefab;

        // Per-slot input frame the bridge has most recently received. Drained by
        // the per-frame input-write hook so values are written every Update
        // regardless of whether new inputs arrived (analog sticks need to keep
        // their last value across frames; otherwise the rig stops moving when
        // the input rate dips).
        private struct InputFrame
        {
            public float StickX, StickY, AimX, AimY;
            public int Buttons; // bit0=jump, bit1=fire, bit2=block, bit3=throw
        }
        private static readonly Dictionary<int, InputFrame> SlotInputs = new Dictionary<int, InputFrame>();

        // Pending teleport target for the next sceneLoaded callback (set by
        // the loadMap bridge command). Applied to every spawned rig once the
        // new scene's geometry is in place.
        private static Vector3 _pendingTeleport;
        private static bool _pendingTeleportArmed;

        private static void OnSceneLoadedTeleport(Scene scene, LoadSceneMode mode)
        {
            if (!_pendingTeleportArmed) return;
            _pendingTeleportArmed = false;
            SceneManager.sceneLoaded -= OnSceneLoadedTeleport;
            Log.LogInfo($"OnSceneLoadedTeleport: scene={scene.name} target={_pendingTeleport}; teleporting {SlotToRig.Count} rigs.");
            foreach (var kv in SlotToRig)
            {
                if ((object)kv.Value == null) continue;
                TeleportRig(kv.Value, _pendingTeleport);
            }
        }

        // TeleportRig moves the rig root + every BodyPart Rigidbody to the
        // target position. The root transform alone doesn't move the visible
        // rig (body parts have independent Rigidbody-driven positions); we
        // have to relocate them all and zero their velocity so they don't
        // immediately bounce back to the old location.
        private static void TeleportRig(GameObject rig, Vector3 target)
        {
            try
            {
                var rootPos = rig.transform.position;
                var delta = target - rootPos;
                rig.transform.position = target;

                var bpType = AccessTools.TypeByName("BodyPart");
                if ((object)bpType == null) return;
                var bps = rig.GetComponentsInChildren(bpType);
                int moved = 0;
                foreach (var bp in bps)
                {
                    var bpComp = bp as Component;
                    if ((object)bpComp == null) continue;
                    var rb = bpComp.GetComponent<Rigidbody>();
                    if ((object)rb == null) continue;
                    rb.position = rb.position + delta;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    bpComp.transform.position = bpComp.transform.position + delta;
                    moved++;
                }
                Log.LogInfo($"TeleportRig: moved {moved} body parts by delta={delta}");
            }
            catch (Exception e)
            {
                Log.LogError($"TeleportRig threw: {e.Message}");
            }
        }

        // ==================================================================
        // PATH A — sfdsrv-compatible raw UDP v25 protocol, server side
        // ==================================================================
        //
        // Wire format (mirrors packets.go::AsBytes):
        //   [u32 timestamp LE][u8 msgType][N body][u64 steamID LE][u8 channel]
        //
        // Minimum packet size: 14 bytes (5 SOPH header + 9 EOPH trailer).

        private void DrainSfServer()
        {
            if ((object)_sfServer == null) return;
            int processed = 0;
            while (processed++ < 64) // cap per frame
            {
                try
                {
                    if (_sfServer.Available <= 0) return;
                    IPEndPoint from = null;
                    byte[] data = _sfServer.Receive(ref from);
                    if (data == null || data.Length < 14) continue;
                    _sfPacketsRx++;
                    SfDispatch(data, from);
                }
                catch (Exception e)
                {
                    if (Verbose) Log.LogDebug($"SF server recv: {e.Message}");
                    return;
                }
            }
        }

        // Periodic sweep — drop _sfClients entries whose last seen exceeds
        // ClientTimeoutSec. Without this, ungracefully disconnected clients
        // accumulate and keep receiving broadcasts forever.
        private const float ClientTimeoutSec = 30f;
        private float _lastClientSweepAt;
        private void SweepStaleClients()
        {
            if (Time.realtimeSinceStartup - _lastClientSweepAt < 5f) return;
            _lastClientSweepAt = Time.realtimeSinceStartup;
            float cutoff = Time.realtimeSinceStartup - ClientTimeoutSec;
            List<string> toRemove = null;
            foreach (var kv in _sfClients)
            {
                if (kv.Value.LastSeen < cutoff)
                {
                    if (toRemove == null) toRemove = new List<string>();
                    toRemove.Add(kv.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (var k in toRemove)
                {
                    var cli = _sfClients[k];
                    Log.LogInfo($"[SF] Dropping stale client {k} (slot={cli.Slot} steamID={cli.SteamID}, last seen {Time.realtimeSinceStartup - cli.LastSeen:0.0}s ago)");
                    _sfClients.Remove(k);
                    // Also forget the v26 endpoint + rate guard for the slot,
                    // otherwise we'd keep sending snapshots into the void.
                    if (cli.Slot >= 0) _slotV26Endpoint.Remove(cli.Slot);
                    _rateGuards.Remove(k);
                }
                // Lobby emptied out: clear the match flag so the next player's
                // /start fires a fresh MapChange. _matchStarted was a one-way
                // latch — once a match started it never reset, so any later
                // /start hit the "already in progress" no-op and the client
                // sat in the lobby forever.
                if (_sfClients.Count == 0 && _matchStarted)
                {
                    Log.LogInfo("[SF] Lobby empty — resetting match state for next /start.");
                    ResetMatchStateForLobby();
                }
            }
        }

        private void ResetMatchStateForLobby()
        {
            _matchStarted = false;
            _autoStartAt = -1f;
            _pendingClientStartMatchAt = -1f;
            _pendingClientStartMatchFired = false;
            _pendingRoundAdvanceAt = -1f;
        }

        // Parse the wrapper and route by msgType. Body bytes are forwarded
        // to handlers without copying — they read from offset 5 to length-9.
        private void SfDispatch(byte[] data, IPEndPoint from)
        {
            // SOPH: u32 timestamp + u8 msgType
            byte msgType = data[4];
            // EOPH: u64 steamID + u8 channel
            int bodyOffset = 5;
            int bodyLen = data.Length - 14;
            ulong steamID = ReadU64LE(data, data.Length - 9);
            byte channel = data[data.Length - 1];

            // Verbose log every Nth packet so we can see what's happening.
            if (_sfPacketsRx == 1 || _sfPacketsRx % 30 == 0)
                Log.LogInfo($"[SF] rx#{_sfPacketsRx} type={msgType} bodyLen={bodyLen} ch={channel} from={from} steamID={steamID}");

            // ALKA-style anticheat — per-client packet rate observation. Logs
            // when a client exceeds thresholds. Drops the packet only when
            // SF_ANTICHEAT_ENFORCE=1 is set (off by default — needs healthy
            // traffic telemetry to tune without dropping legit bursts).
            if (AnticheatObserve(from, msgType)) return;

            // Phase 6.12 — v26 PktPlayerInput is keyed by slot embedded in the
            // body, not by source IP+port (the SFClientRecon plugin sends from
            // its own ephemeral UDP socket, not the patched DLL's). Route it
            // directly to the slot-based handler and skip the auto-add path
            // below, which would create a phantom SfClient entry every time.
            if (msgType == PktPlayerInput)
            {
                try { HandlePlayerInput(data, bodyOffset, bodyLen, from); }
                catch (Exception ex) { Log.LogWarning($"[SF] HandlePlayerInput threw: {ex.Message}"); }
                return;
            }
            if (msgType == PktClientFireWeapon)
            {
                try { HandleClientFireWeapon(data, bodyOffset, bodyLen, from); }
                catch (Exception ex) { Log.LogWarning($"[SF] HandleClientFireWeapon threw: {ex.Message}"); }
                return;
            }

            // Track client.
            string key = from.ToString();
            if (!_sfClients.TryGetValue(key, out var cli))
            {
                cli = new SfClient { Addr = from, Slot = -1 };
                _sfClients[key] = cli;
                Log.LogInfo($"[SF] new client appeared: {from}");
            }
            cli.LastSeen = Time.realtimeSinceStartup;
            // CRITICAL: do NOT overwrite cli.SteamID from the envelope here.
            // SF's SendP2PPacketToUser puts the DESTINATION's SteamID in the
            // envelope, not the sender's. When P1's OnClientJoined fires it
            // calls PingAllUsers → P1 sends a Ping with envelope steamID=P2's,
            // and a blind overwrite would clobber P1's record. cli.SteamID is
            // set exactly once from ClientRequestingIndex's body (which DOES
            // carry the sender's identity) — that's enough.
            if (cli.SteamID == 0 && steamID != 0)
            {
                // First-ever steamID for this addr — accept it (covers e.g.
                // direct ClientRequestingAccepting before ClientRequestingIndex).
                cli.SteamID = steamID;
            }

            // ALKA P0-5 defense: a bad/malformed packet in one handler should
            // log + drop, not bubble out and skip the rest of the batch (which
            // would happen if it propagated up to DrainSfServer's catch).
            try
            {
            switch (msgType)
            {
                case PktPing:
                    HandlePing(cli, data, bodyOffset, bodyLen);
                    break;
                case PktClientRequestingAccepting:
                    HandleClientRequestingAccepting(cli);
                    break;
                case PktClientRequestingIndex:
                    HandleClientRequestingIndex(cli, data, bodyOffset, bodyLen);
                    break;
                case PktClientRequestingToSpawn:
                    HandleClientRequestingToSpawn(cli, data, bodyOffset, bodyLen);
                    break;
                case PktPlayerUpdate:
                    HandlePlayerUpdate(cli, data, bodyOffset, bodyLen, channel);
                    break;
                case PktClientReadyUp:
                    HandleClientReadyUp(cli, data, bodyOffset, bodyLen);
                    break;

                // === Phase 6.6 — gameplay packets ===
                // Pickup: re-broadcast as WeaponWasPickedUp with the same body
                // (1 byte playerIndex + 2 byte weaponNetworkIndex). SF's
                // OnPlayerRequestingWeaponPickUp would validate against
                // mSpawnedWeapons which is empty on the oracle, so we
                // bypass validation. (1 client, no anti-cheat threat model.)
                case PktClientRequestingWeaponPickUp:
                    HandlePickupRequest(cli, data, bodyOffset, bodyLen);
                    break;

                // SF's host code broadcasts PlayerTookDamage with ignoreUserID=0
                // — INCLUDING the sender. That return-trip is the killing-blow
                // signal: client.SyncClientHealth applies the damage, sees
                // damage==666.666, sets health=0, calls Die(). Without the echo
                // back to the sender, void/lava damage never kills them.
                case PktPlayerTookDamage:
                    if (!ValidateDamagePacket(cli, data, bodyOffset, bodyLen)) break;
                    RelayBodyToAll(msgType, data, bodyOffset, bodyLen, channel);
                    break;
                // PlayerWonWithRicochet has no abuse vector worth validating yet.
                case PktPlayerWonWithRicochet:
                    RelayBodyToAll(msgType, data, bodyOffset, bodyLen, channel);
                    break;

                // "Relay to all OTHER clients" — SF's host passes ignoreUserID =
                // sender so they don't get duplicate force events / fall-outs.
                case PktPlayerForceAdded:
                case PktPlayerForceAddedAndBlock:
                case PktPlayerLavaForceAdded:
                case PktPlayerFallOut:
                case PktPlayerTalked:        // chat / voice / commands (see PlayerTalked hex log below)
                case PktOptionsChanged:      // lobby option toggles (ALKA BUGS_BACKLOG P0-4)
                case PktLerpPlayer:          // patched-DLL ext, remote-lerp trigger (ALKA P1-4)
                case PktColorChanged:        // patched-DLL ext, player color (ALKA P1-4)
                case PktKickPlayer:          // host kick — patched DLL emits, peer clients see who got booted
                    if (msgType == PktPlayerTalked)
                    {
                        LogPlayerTalkedTelemetry(cli, data, bodyOffset, bodyLen, channel);
                        TryProcessChatCommand(cli, data, bodyOffset, bodyLen);
                    }
                    RelayBodyToOthers(cli, msgType, data, bodyOffset, bodyLen, channel);
                    // Void/lava: FallOut often arrives without a 666 relay (solo or last player).
                    if (msgType == PktPlayerFallOut && _matchStarted)
                        ScheduleRoundAdvanceOnDeath("player-fallout");
                    break;

                case PktObjectUpdate:
                    // BOXES FIX — apply the client's NSO update to the
                    // server's own scene state BEFORE relaying. Previously
                    // we only relayed; the server's local NSO sat at its
                    // spawn position forever, so v26 WorldStateSnapshot
                    // broadcast (Phase 6.14) overrode the client's correct
                    // local-push with the server's stale "still at spawn"
                    // view. Now the server tracks who-pushed-what and the
                    // snapshot is accurate.
                    ApplyClientObjectUpdate(data, bodyOffset, bodyLen);
                    RelayBodyToOthers(cli, msgType, data, bodyOffset, bodyLen, channel);
                    break;

                // "Relay to ALL including sender" for destruction events.
                // In vanilla SF, the host applies the break locally and broadcasts
                // to non-host clients. In our dedicated-server setup, NO client
                // is the host — the sender hasn't applied the break locally yet
                // either, so they need the echo back to actually see the ice/
                // crate/chain break. Without this, the breaker's screen shows
                // unbroken ice while others see it shattered. Spotted in ALKA's
                // BUGS_BACKLOG P0-3 — same fix shape as our PlayerTookDamage
                // include-sender for the killing-blow signal.
                case PktObjectSimpleDestruction:
                case PktObjectInvokeDestructionEvent:
                case PktObjectDestructionCollision:
                    RelayBodyToAll(msgType, data, bodyOffset, bodyLen, channel);
                    break;

                // Weapon drop: SF's OnPlayerRequestingWeaponDrop just appends
                // the next two IDs (weaponSpawnID + syncableObjectSpawnID) and
                // broadcasts as WeaponDropped. We replicate that logic in pure
                // C# so the IDs come from our counter and stay in sync with
                // weapon spawns.
                case PktClientRequestWeaponDrop:
                    HandleDropRequest(cli, data, bodyOffset, bodyLen);
                    break;

                // Weapon throw: client sends RequestingWeaponThrow (21) with
                // [bool justDrop][byte weaponIdx][ShortVector2 pos][ByteVector2 rot]
                // [optional ByteVector2 aim]. Host appends weaponSpawnID +
                // syncableObjectSpawnID and broadcasts as WeaponThrown (20).
                case PktRequestingWeaponThrow:
                    HandleThrowRequest(cli, data, bodyOffset, bodyLen);
                    break;

                default:
                    if (Verbose) Log.LogDebug($"[SF] unhandled type={msgType} from={from}");
                    break;
            }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[SF] dispatch threw on msgType={msgType} from={from}: {ex.Message}");
            }
        }

        // /lobbies handler — read the multi-process registry directly so we can
        // tell a player about OTHER lobbies running on this same host.
        private string ListOtherLobbiesFromRegistry()
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("SF_LOBBIES_DIR") ?? "/tmp/sf-lobbies";
                if (!System.IO.Directory.Exists(dir)) return "No other lobbies (registry not found).";
                var entries = new List<string>();
                string myCode = Environment.GetEnvironmentVariable("SF_LOBBY_CODE") ?? "";
                foreach (var path in System.IO.Directory.GetFiles(dir, "*.conf"))
                {
                    string code = "?", port = "?";
                    foreach (var line in System.IO.File.ReadAllLines(path))
                    {
                        var eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                        if (k == "code") code = v;
                        else if (k == "port") port = v;
                    }
                    if (code == myCode) continue;
                    entries.Add($"{code}:{port}");
                }
                if (entries.Count == 0) return "No other lobbies running.";
                return "Other lobbies: " + string.Join(", ", entries.ToArray());
            }
            catch (Exception ex) { return $"(error: {ex.Message})"; }
        }

        // Phase 6.16 v0.1 — basic damage validation (no rewind yet).
        // Body shape: byte attackerIdx, f32 damage, bool playParticles, ...
        // The full rewind-based authority is designed in
        // notes/phase6/13-rewind-buffer.md. For now we just reject obvious
        // anomalies so a malicious client can't one-shot people with
        // arbitrary damage values.
        private uint _damagePacketsDropped;

        // === Behavioral anti-cheat — impossible-melee / instakill detection ===
        //
        // Goal (per design): stay PERMISSIVE. We only act on behavior that is
        // physically impossible in legit play and only after it repeats across
        // more than two distinct rounds — so a single fluke never kicks anyone.
        //
        // The signature we catch: a player dies (server sees the 666.666
        // killing-blow marker) having received almost no real accumulated
        // damage this round (<AcSuspectMaxAccum) from at most one hit, at melee
        // range. Legit kills always accumulate ~full-HP worth of real damage
        // before the killing blow, so this only trips on faked/spoofed instant
        // kills. We require it in >2 distinct rounds before kicking.
        private const float AcSuspectMaxAccum = 60f;   // victim HP is ~100; <60 received = couldn't legitimately die
        private const float AcMeleeRange = 4.0f;        // melee reach is short; spoofed kills register here
        private const int   AcFlaggedRoundsToKick = 3;  // strictly >2 distinct rounds
        private readonly float[] _acRoundDmgToVictim = new float[4];
        private readonly int[]   _acRoundHitsToVictim = new int[4];
        private readonly Dictionary<int, HashSet<int>> _acFlaggedRounds = new Dictionary<int, HashSet<int>>();
        private readonly HashSet<int> _acKicked = new HashSet<int>();
        private int _acRoundIndex;
        private bool AcEnabled => Environment.GetEnvironmentVariable("SF_AC_BEHAVIOR") != "0";

        // Called at every round boundary to reset per-life damage accumulators.
        private void AcResetRound()
        {
            for (int i = 0; i < 4; i++) { _acRoundDmgToVictim[i] = 0f; _acRoundHitsToVictim[i] = 0; }
            _acRoundIndex++;
        }

        // Called for every accepted PlayerTookDamage. attackerIdx/dmg already
        // parsed + validated. victimSlot is the packet sender's slot.
        private void AcTrackDamage(int victimSlot, byte attackerIdx, float dmg, bool isKillingBlow)
        {
            if (!AcEnabled) return;
            if (victimSlot < 0 || victimSlot > 3) return;

            if (!isKillingBlow)
            {
                // Real incremental damage — accumulate for this victim's life.
                _acRoundDmgToVictim[victimSlot] += dmg;
                _acRoundHitsToVictim[victimSlot]++;
                return;
            }

            // Killing blow. Environment kills (lava/void) use 255 — never a cheat.
            if (attackerIdx > 3) return;
            // Self-kill (suicide / fell into the void / own explosive) — the
            // attacker and victim are the same player. Never a cheat; this is the
            // false-positive that kicked a lone player throwing himself off the map.
            if (attackerIdx == victimSlot) return;
            // Anti-cheat is about player-vs-player interactions. With fewer than 2
            // players connected there is no one to legitimately kill, so any kill
            // here is environmental/self — never flag it.
            if (_sfClients.Count < 2) return;
            if (_acKicked.Contains(attackerIdx)) return;

            float accum = _acRoundDmgToVictim[victimSlot];
            int hits = _acRoundHitsToVictim[victimSlot];
            if (accum >= AcSuspectMaxAccum || hits > 1) return;  // plenty of real damage → legit kill

            // Range gate — confirm the kill happened at melee distance so we
            // don't false-positive on a legit long-range one-shot weapon.
            bool meleeRange = true;
            if (SlotToRig.TryGetValue(attackerIdx, out var attRig) && (object)attRig != null
                && SlotToRig.TryGetValue(victimSlot, out var vicRig) && (object)vicRig != null)
            {
                float dist = Vector3.Distance(attRig.transform.position, vicRig.transform.position);
                meleeRange = dist <= AcMeleeRange;
            }
            if (!meleeRange) return;

            // Flag this round for the attacker.
            if (!_acFlaggedRounds.TryGetValue(attackerIdx, out var rounds))
            {
                rounds = new HashSet<int>();
                _acFlaggedRounds[attackerIdx] = rounds;
            }
            rounds.Add(_acRoundIndex);
            Log.LogWarning($"[anticheat behavior] Impossible kill by slot={attackerIdx} on slot={victimSlot} " +
                           $"(victim took only {accum:0.#} dmg over {hits} hit(s) at melee range). " +
                           $"Flagged rounds: {rounds.Count}/{AcFlaggedRoundsToKick}.");

            if (rounds.Count >= AcFlaggedRoundsToKick)
            {
                _acKicked.Add(attackerIdx);
                AcKickForCheat(attackerIdx, "instant melee kills (impossible without cheats)");
            }
        }

        // Announce the kick to everyone, then boot the offender.
        private void AcKickForCheat(int slot, string reason)
        {
            string msg = $"Player {slot + 1} kicked: {reason}";
            BroadcastChatToAll(msg);
            SendAnnouncementToAll(msg);   // recon plugin shows a top banner for 3s
            Log.LogWarning($"[anticheat behavior] KICK slot={slot}: {reason}");
            byte[] kickBody = new byte[1] { (byte)slot };
            BroadcastSfPacket(PktKickPlayer, kickBody, 0uL, 0);
        }

        // Push a top-of-screen banner string to every recon client over the
        // v26 channel (their :1339 endpoint). Stock clients without the recon
        // plugin never listen here, so this is a no-op for them.
        private void SendAnnouncementToAll(string text)
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(text ?? "");
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                IPEndPoint v26Ep;
                if (!_slotV26Endpoint.TryGetValue(kv.Value.Slot, out v26Ep))
                    v26Ep = new IPEndPoint(kv.Value.Addr.Address, V26_CLIENT_PORT);
                SendSfPacket(v26Ep, PktV26Announce, body, 0, 0);
            }
        }

        // Send a server chat line to every connected player. SF chat bubbles
        // fade on their own after ~3s, satisfying the "auto-disappear" ask.
        private void BroadcastChatToAll(string text)
        {
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (cli == null || !cli.Initialized) continue;
                SendChatToPlayer(cli, text);
            }
        }

        private bool ValidateDamagePacket(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 5) return false;
            byte attackerIdx = data[off];
            float dmg = BitConverter.ToSingle(data, off + 1);
            // Magnitude check — SF's killing-blow marker is 666.666; anything
            // above 1000 is clearly out of band. Negative damage is healing
            // and stock SF doesn't use it.
            if (float.IsNaN(dmg) || float.IsInfinity(dmg) || dmg < 0f || dmg > 1000f)
            {
                _damagePacketsDropped++;
                Log.LogWarning($"[anticheat damage] Reject damage={dmg} from slot={sender.Slot} (attacker idx={attackerIdx}). Dropped #{_damagePacketsDropped}");
                return false;
            }
            // Attacker slot bound check.
            if (attackerIdx > 3 && attackerIdx != 255)  // 255 = environment kill (lava/void)
            {
                _damagePacketsDropped++;
                Log.LogWarning($"[anticheat damage] Reject — attacker idx {attackerIdx} out of range. Dropped #{_damagePacketsDropped}");
                return false;
            }
            // P1-8 REVERTED 2026-05-23 night — the original "reject if
            // attackerIdx != sender.Slot" check was WRONG. In stock SF,
            // PktPlayerTookDamage is emitted by the VICTIM's
            // NetworkPlayer.UnitWasDamaged after their HealthHandler took
            // damage. The body's attackerIdx is the SHOOTER (computed by
            // looking up mController.damager in ConnectedClients), while
            // the sender of the packet is the victim. So attackerIdx !=
            // sender.Slot is THE NORMAL CASE — and rejecting it blocks
            // all damage between players (void/lava worked because they
            // had attacker == sender, but bullets/punches did not).
            //
            // The audit's "spoofing" concern stands but the fix-shape was
            // wrong. Proper anticheat for damage-source spoofing requires
            // server-side hit detection (Phase 6.17 v0.2+, in progress —
            // server emits its own damage instead of trusting clients).
            // Until that's the only damage path, we trust clients and
            // rely on the existing range/magnitude checks below.
            // Phase 6.14.5 v0.2 — range plausibility with rewind buffer.
            // Damage packets don't carry a client-tick reference (would need
            // a patched-DLL extension), so we assume the hit happened
            // ~2 ticks ago (≈66ms at 30Hz snapshot rate — typical RTT/2 +
            // client-processing latency). LookupTickSample retrieves the
            // historical positions; if not available (still in early ticks),
            // fall back to current.
            if (attackerIdx != 255 && sender.Slot >= 0)
            {
                Vector3 attPos, vicPos;
                bool gotHistoric = false;
                if (_serverTick >= 2)
                {
                    var sample = LookupTickSample(_serverTick - 2);
                    if (sample != null && sample.Alive[attackerIdx] && sample.Alive[sender.Slot])
                    {
                        attPos = sample.Positions[attackerIdx];
                        vicPos = sample.Positions[sender.Slot];
                        gotHistoric = true;
                    }
                    else { attPos = vicPos = Vector3.zero; }
                }
                else { attPos = vicPos = Vector3.zero; }

                if (!gotHistoric)
                {
                    if (SlotToRig.TryGetValue(attackerIdx, out var attRig) && (object)attRig != null
                        && SlotToRig.TryGetValue(sender.Slot, out var vicRig) && (object)vicRig != null)
                    {
                        attPos = attRig.transform.position;
                        vicPos = vicRig.transform.position;
                    }
                    else return true;  // not enough info to validate; trust
                }

                float dist = Vector3.Distance(attPos, vicPos);
                const float MaxPlausibleReach = 50f;
                if (dist > MaxPlausibleReach)
                {
                    _damagePacketsDropped++;
                    Log.LogWarning($"[anticheat damage] Reject — distance {dist:0.0}u > {MaxPlausibleReach}u (attacker slot {attackerIdx}, victim slot {sender.Slot}, {(gotHistoric ? "rewind" : "live")}). Dropped #{_damagePacketsDropped}");
                    return false;
                }
            }
            // Future (Phase 6.16+): weapon-specific max-reach (sword=3.5u,
            // pistol=18u, RPG=22u). Requires per-slot weapon tracking which
            // we don't have yet on the oracle side.

            // Behavioral tracking — accumulate real damage / detect spoofed
            // instant kills. Does not reject here (handled via kick).
            bool isKillingBlow = System.Math.Abs(dmg - 666.666f) < 0.01f;
            AcTrackDamage(sender.Slot, attackerIdx, dmg, isKillingBlow);
            return true;
        }

        // Phase 6.15 — server-emitted chat. Used for command responses.
        // Wire format: body = raw UTF-8 bytes of the message (no length
        // prefix; total length comes from the v25 wrapper). Channel encodes
        // the talker's slot as (slot*2)+3; we use the recipient's owner
        // channel so it shows up over their own player.
        private void SendChatToPlayer(SfClient target, string text)
        {
            if (target == null || target.Slot < 0) return;
            byte[] body = System.Text.Encoding.UTF8.GetBytes(text);
            byte ch = (byte)((target.Slot * 2) + 3);
            SendSfPacket(target.Addr, PktPlayerTalked, body, 0uL, ch);
        }

        // Phase 6.15 — chat command parser. Body of PktPlayerTalked is
        // raw UTF-8 (verified from decompiled NetworkPlayer.OnTalked). If the
        // text starts with '/' we treat it as a server command. Format mirrors
        // ALKA's MOD_CLIENT.md (/code, /room, /ping, /start initially).
        private void TryProcessChatCommand(SfClient sender, byte[] data, int off, int len)
        {
            try
            {
                if (len == 0) return;
                string text = System.Text.Encoding.UTF8.GetString(data, off, len);
                if (string.IsNullOrEmpty(text) || text[0] != '/') return;
                var space = text.IndexOf(' ');
                string cmd = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();
                Log.LogInfo($"[chat] slot={sender.Slot} command='{text}'");
                switch (cmd)
                {
                    case "/code":
                    case "/room":
                        string code = Environment.GetEnvironmentVariable("SF_LOBBY_CODE");
                        SendChatToPlayer(sender, "Lobby code: " + (string.IsNullOrEmpty(code) ? "<unknown>" : code));
                        break;
                    case "/ping":
                        SendChatToPlayer(sender, "pong");
                        break;
                    case "/start":
                        if (_matchStarted)
                        {
                            SendChatToPlayer(sender, "Match already in progress.");
                        }
                        else
                        {
                            SendChatToPlayer(sender, "Starting match...");
                            FireMatchStart($"chat /start from slot {sender.Slot}");
                        }
                        break;
                    case "/version":
                        SendChatToPlayer(sender, $"sf-multiplayer {PluginVersion} (v26 protocol)");
                        break;
                    case "/restart":
                    case "/next":
                        if (_pendingRoundAdvanceAt > 0f)
                            SendChatToPlayer(sender, "Round advance already pending.");
                        else
                        {
                            SendChatToPlayer(sender, "Advancing to next map...");
                            _pendingRoundAdvanceAt = Time.realtimeSinceStartup + 1.0f;
                        }
                        break;
                    case "/map":
                    {
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim());
                        if (string.IsNullOrEmpty(arg))
                        {
                            SendChatToPlayer(sender, $"Current map: scene {_currentSceneIndex}. Usage: /map <1-124>. Random next: /next.");
                            break;
                        }
                        if (!int.TryParse(arg, out int sceneIdx) || sceneIdx < 1 || sceneIdx > 124 || sceneIdx == 102)
                        {
                            SendChatToPlayer(sender, "Usage: /map <1-124> (102 excluded — non-MP scene). Use /listmaps to browse.");
                            break;
                        }
                        bool valid = false;
                        foreach (var m in _allLandfallMaps) if (m == sceneIdx) { valid = true; break; }
                        if (!valid)
                        {
                            SendChatToPlayer(sender, $"Scene {sceneIdx} isn't in the playable Landfall set.");
                            break;
                        }
                        _currentSceneIndex = sceneIdx;
                        SendChatToPlayer(sender, $"Map set to scene {sceneIdx}. Switching now...");
                        Log.LogInfo($"[chat] /map {sceneIdx} by slot={sender.Slot}");
                        // Reuse AdvanceRound's MapChange + StartMatch chain via the pending timer.
                        // AdvanceRound picks a random map though, so we need a direct call shape.
                        _pendingRoundAdvanceAt = -1f;
                        BroadcastMapChange(_currentSceneIndex);
                        _pendingStartMatchAt = Time.realtimeSinceStartup + NextMatchDelaySec;
                        ScheduleOracleReloadCurrentMap("chat-/map");
                        foreach (var kv in _sfClients) kv.Value.Spawned = false;
                        break;
                    }
                    case "/listmaps":
                    case "/maps":
                    {
                        var sb = new System.Text.StringBuilder("Maps (1-124, 102 excluded): ");
                        int shown = 0;
                        foreach (var m in _allLandfallMaps)
                        {
                            if (shown > 0) sb.Append(",");
                            sb.Append(m);
                            shown++;
                            if (shown >= 40) { sb.Append("..."); break; }
                        }
                        SendChatToPlayer(sender, sb.ToString());
                        break;
                    }
                    case "/players":
                        int up = 0, sp = 0;
                        foreach (var ckv in _sfClients) { up++; if (ckv.Value.Spawned) sp++; }
                        SendChatToPlayer(sender, $"Players: {up} connected, {sp} spawned, rigs={SlotToRig.Count}");
                        break;
                    case "/lobbies":
                        SendChatToPlayer(sender, ListOtherLobbiesFromRegistry());
                        break;
                    case "/kick":
                    {
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim());
                        if (string.IsNullOrEmpty(arg) || !int.TryParse(arg, out int targetSlot) || targetSlot < 0 || targetSlot > 3)
                        {
                            SendChatToPlayer(sender, "Usage: /kick <slot 0-3>. Use /players to see slots.");
                            break;
                        }
                        if (targetSlot == sender.Slot)
                        {
                            SendChatToPlayer(sender, "Can't kick yourself. Use Steam's Disconnect.");
                            break;
                        }
                        // Send PktKickPlayer to everyone (including the victim, who'll
                        // disconnect on receipt). Body = single byte slot.
                        byte[] kickBody = new byte[1] { (byte)targetSlot };
                        BroadcastSfPacket(PktKickPlayer, kickBody, 0uL, 0);
                        Log.LogInfo($"[chat] /kick slot={targetSlot} by slot={sender.Slot}");
                        SendChatToPlayer(sender, $"Kicked slot {targetSlot}.");
                        break;
                    }
                    case "/anticheat":
                    {
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim()).ToLowerInvariant();
                        if (arg == "on" || arg == "1" || arg == "true" || arg == "enforce")
                        {
                            AnticheatEnforce = true;
                            SendChatToPlayer(sender, "Anticheat: ENFORCE (rate-limited packets will be dropped)");
                        }
                        else if (arg == "off" || arg == "0" || arg == "false" || arg == "observe")
                        {
                            AnticheatEnforce = false;
                            SendChatToPlayer(sender, "Anticheat: observe-only (offending packets logged, not dropped)");
                        }
                        else
                        {
                            SendChatToPlayer(sender, $"Anticheat: {(AnticheatEnforce ? "ENFORCE" : "observe-only")}. Toggle: /anticheat on|off");
                        }
                        break;
                    }
                    case "/weapons":
                    {
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim()).ToLowerInvariant();
                        if (string.IsNullOrEmpty(arg))
                        {
                            if (_allowedWeaponIds.Count == 0)
                            {
                                SendChatToPlayer(sender, "Weapons: all (default 0-7 round-robin). Set: /weapons 0,1,3");
                            }
                            else
                            {
                                var arr = new int[_allowedWeaponIds.Count];
                                _allowedWeaponIds.CopyTo(arr);
                                System.Array.Sort(arr);
                                SendChatToPlayer(sender, $"Weapons allow-list: {string.Join(",", System.Array.ConvertAll(arr, i => i.ToString()))}");
                            }
                        }
                        else if (arg == "all" || arg == "clear" || arg == "default")
                        {
                            _allowedWeaponIds.Clear();
                            _allowedWeaponCycleIdx = 0;
                            SendChatToPlayer(sender, "Weapons reset to default (all).");
                        }
                        else
                        {
                            var parts = arg.Split(',');
                            var newList = new System.Collections.Generic.List<int>();
                            foreach (var part in parts)
                            {
                                if (int.TryParse(part.Trim(), out int idx) && idx >= 0 && idx <= 31) newList.Add(idx);
                            }
                            if (newList.Count == 0)
                            {
                                SendChatToPlayer(sender, "Usage: /weapons <0-31 comma list> | all");
                            }
                            else
                            {
                                _allowedWeaponIds.Clear();
                                foreach (var i in newList) _allowedWeaponIds.Add(i);
                                _allowedWeaponCycleIdx = 0;
                                SendChatToPlayer(sender, $"Weapons set to: {string.Join(",", newList.ConvertAll(i => i.ToString()).ToArray())}");
                            }
                        }
                        break;
                    }
                    case "/tickrate":
                    case "/tick":
                    {
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim());
                        if (string.IsNullOrEmpty(arg))
                        {
                            float fd = Time.fixedDeltaTime;
                            int hz = (fd > 0f) ? (int)System.Math.Round(1.0 / fd) : 0;
                            SendChatToPlayer(sender, $"Server physics tickrate: {hz}Hz (fixedDeltaTime={fd:0.0000}s). Snapshot broadcast: 30Hz.");
                        }
                        else
                        {
                            int hz;
                            if (!int.TryParse(arg, out hz) || hz < 20 || hz > 240)
                            {
                                SendChatToPlayer(sender, "Usage: /tickrate <20-240>. Default 50.");
                            }
                            else
                            {
                                float newFd = 1.0f / hz;
                                float oldFd = Time.fixedDeltaTime;
                                Time.fixedDeltaTime = newFd;
                                Log.LogInfo($"[chat] /tickrate {hz}Hz — Time.fixedDeltaTime: {oldFd:0.0000} → {newFd:0.0000}");
                                SendChatToPlayer(sender, $"Server physics tickrate set to {hz}Hz. (was {(int)System.Math.Round(1.0/oldFd)}Hz). Snapshot broadcast still 30Hz — client FPS is independent.");
                            }
                        }
                        break;
                    }
                    case "/help":
                        SendChatToPlayer(sender, "Commands: /code /ping /start /restart /next /map /listmaps /players /lobbies /tickrate /weapons /kick /anticheat /version /help");
                        break;
                    default:
                        SendChatToPlayer(sender, "Unknown command. Type /help");
                        break;
                }
            }
            catch (Exception ex) { Log.LogWarning($"[chat parse] {ex.Message}"); }
        }

        // Telemetry for the chat-command research effort (notes/phase6/14-
        // chat-commands.md). The patched DLL sends '/start', '/code', etc.
        // via PktPlayerTalked on channel (slot*2)+3 — body format is raw UTF-8
        // (confirmed from NetworkPlayer.OnTalked decompile). We log the first
        // 20 packets' hex+ASCII as a redundant capture so we can confirm
        // format if the parser misbehaves on edge cases.
        private int _playerTalkedLogged;
        private void LogPlayerTalkedTelemetry(SfClient cli, byte[] data, int off, int len, byte channel)
        {
            if (_playerTalkedLogged >= 20) return;
            _playerTalkedLogged++;
            int dumpLen = System.Math.Min(len, 32);
            var hex = new System.Text.StringBuilder(dumpLen * 3);
            for (int i = 0; i < dumpLen; i++) hex.Append(data[off + i].ToString("X2")).Append(' ');
            // Best-effort UTF-8 with non-printable as '.'
            var ascii = new System.Text.StringBuilder(dumpLen);
            for (int i = 0; i < dumpLen; i++)
            {
                byte b = data[off + i];
                ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            Log.LogInfo($"[telemetry chat] slot={cli.Slot} ch={channel} len={len} hex={hex} ascii='{ascii}'");
        }

        // === ALKA-style anticheat — observation + optional rate-limit ===
        // Per-client sliding window of packet timestamps. By default we only
        // observe + log; set SF_ANTICHEAT_ENFORCE=1 to actually drop excess
        // packets (and return true from AnticheatObserve to signal "drop"
        // — caller in SfDispatch then returns early).
        //
        // Ported from server-go/anticheat.go but tuned conservatively (3-4x
        // typical vanilla SF traffic) so it surfaces real anomalies, not
        // legitimate gameplay bursts.
        private static bool? _enforceCache;
        private static bool AnticheatEnforce
        {
            get
            {
                if (_enforceCache.HasValue) return _enforceCache.Value;
                _enforceCache = Environment.GetEnvironmentVariable("SF_ANTICHEAT_ENFORCE") == "1";
                return _enforceCache.Value;
            }
            set { _enforceCache = value; }  // /anticheat chat command writes here
        }
        private class RateGuard
        {
            public Queue<float> All        = new Queue<float>();
            public Queue<float> PlayerUpd  = new Queue<float>();
            public Queue<float> Damage     = new Queue<float>();
            public Queue<float> Object     = new Queue<float>();
            public int Violations;
            public float LastViolationLog;
        }
        private const int MaxAllPerSec        = 240;   // vanilla ≈ 80-100
        private const int MaxPlayerUpdPerSec  = 120;   // vanilla ≈ 60
        private const int MaxDamagePerSec     = 30;    // vanilla bursts <10
        private const int MaxObjectPerSec     = 480;   // boxes/chains can be chatty
        private readonly Dictionary<string, RateGuard> _rateGuards = new Dictionary<string, RateGuard>();
        // Returns true if the packet should be DROPPED (only under
        // SF_ANTICHEAT_ENFORCE=1). Always observes regardless.
        private bool AnticheatObserve(IPEndPoint from, byte msgType)
        {
            bool overLimit = false;
            try
            {
                string key = from.ToString();
                if (!_rateGuards.TryGetValue(key, out var g))
                {
                    g = new RateGuard();
                    _rateGuards[key] = g;
                }
                float now = Time.realtimeSinceStartup;
                RotateQueue(g.All, now);
                g.All.Enqueue(now);
                if (g.All.Count > MaxAllPerSec) { ReportViolation(g, key, "total", g.All.Count); overLimit = true; }

                if (msgType == PktPlayerUpdate)
                {
                    RotateQueue(g.PlayerUpd, now);
                    g.PlayerUpd.Enqueue(now);
                    if (g.PlayerUpd.Count > MaxPlayerUpdPerSec) { ReportViolation(g, key, "playerUpdate", g.PlayerUpd.Count); overLimit = true; }
                }
                else if (msgType == PktPlayerTookDamage)
                {
                    RotateQueue(g.Damage, now);
                    g.Damage.Enqueue(now);
                    if (g.Damage.Count > MaxDamagePerSec) { ReportViolation(g, key, "damage", g.Damage.Count); overLimit = true; }
                }
                else if (msgType == PktObjectUpdate
                      || msgType == PktObjectSpawned
                      || msgType == PktObjectDestructionCollision
                      || msgType == PktObjectSimpleDestruction
                      || msgType == PktObjectInvokeDestructionEvent
                      || msgType == PktObjectHello)
                {
                    RotateQueue(g.Object, now);
                    g.Object.Enqueue(now);
                    if (g.Object.Count > MaxObjectPerSec) { ReportViolation(g, key, "object", g.Object.Count); overLimit = true; }
                }
            }
            catch { /* observation only — never let it crash the dispatch */ }
            return overLimit && AnticheatEnforce;
        }
        private static void RotateQueue(Queue<float> q, float now)
        {
            while (q.Count > 0 && now - q.Peek() > 1.0f) q.Dequeue();
        }
        private void ReportViolation(RateGuard g, string key, string label, int rate)
        {
            g.Violations++;
            float now = Time.realtimeSinceStartup;
            if (now - g.LastViolationLog < 5f) return;
            g.LastViolationLog = now;
            Log.LogWarning($"[anticheat] {key} exceeded {label} rate ({rate}/s) — violation #{g.Violations}. Observation only; not dropping.");
        }

        // Re-broadcast body to all v25 clients except the sender. Used for
        // pure-relay gameplay msgTypes (force, falloff, etc).
        private void RelayBodyToOthers(SfClient sender, byte msgType, byte[] data, int off, int len, byte channel)
        {
            if (len <= 0) return;
            byte[] body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (cli == sender) continue;
                if (!cli.Initialized) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (Verbose && sent > 0)
                Log.LogDebug($"[SF] relay msgType={msgType} bodyLen={len} → {sent} other client(s)");
        }

        // Re-broadcast body to ALL v25 clients including sender. Used for
        // msgTypes that the sender's own client expects to receive back
        // (PlayerTookDamage carries the killing-blow signal; without the
        // echo, the sender never dies).
        private void RelayBodyToAll(byte msgType, byte[] data, int off, int len, byte channel)
        {
            if (len <= 0) return;
            byte[] body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (!cli.Initialized) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (sent > 0)
            {
                // Sample-log so we can see this firing without flooding.
                if (_relayAllCount++ < 5 || _relayAllCount % 30 == 0)
                    Log.LogInfo($"[SF] relay-to-all msgType={msgType} bodyLen={len} → {sent} client(s) (#{_relayAllCount})");
            }

            // Detect killing-blow for round-advance. PlayerTookDamage body
            // format (from NetworkPlayer.UnitWasDamaged): byte attacker, float
            // damage, bool playParticles, [particle dir bytes], byte dmgType.
            // damage == 666.666f signals "this hit kills."
            if (msgType == PktPlayerTookDamage && len >= 5)
            {
                float dmg = BitConverter.ToSingle(body, 1);
                if (System.Math.Abs(dmg - 666.666f) < 0.01f)
                {
                    if (!_matchStarted)
                        FireMatchStart($"lobby-kill dmg={dmg:0.###}");
                    else
                        ScheduleRoundAdvanceOnDeath($"killing-blow dmg={dmg:0.###}");
                }
            }
        }
        private int _relayAllCount;
        private float _pendingRoundAdvanceAt = -1f;

        private int CountInitializedSfClients()
        {
            int n = 0;
            foreach (var kv in _sfClients)
                if (kv.Value.Initialized) n++;
            return n;
        }

        /// <summary>One connected player in an active match — solo physics/map QA.</summary>
        private bool IsSoloTestLobby() => _matchStarted && CountInitializedSfClients() == 1;

        private float _pendingRearmCombatAt = -1f;
        private float _lastPeriodicRearmAt = -1f;

        private void TickPeriodicWeaponRearm()
        {
            if (!_matchStarted) return;
            float now = Time.realtimeSinceStartup;
            if (_lastPeriodicRearmAt < 0f) _lastPeriodicRearmAt = now;
            if (now - _lastPeriodicRearmAt < 4f) return;
            _lastPeriodicRearmAt = now;
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) return;
                object gmInst = null;
                var ig = AccessTools.PropertyGetter(gmType, "Instance");
                if ((object)ig != null) gmInst = ig.Invoke(null, null);
                if ((object)gmInst == null) return;
                var inFightF = AccessTools.Field(gmType, "inFight");
                bool inFight = (object)inFightF != null && (bool)inFightF.GetValue(gmInst);
                if (inFight) return;
                Log.LogInfo("[P6.5] Periodic rearm: inFight was false mid-match.");
                RearmOracleCombatLoop("periodic");
            }
            catch { }
        }

        /// <summary>Clears post-map gate so death can schedule round advance immediately.</summary>
        internal void ClearRoundAdvanceBlockedGate(string reason)
        {
            _roundAdvanceBlockedUntil = Time.realtimeSinceStartup;
            Log.LogInfo($"[DEATH] Round advance gate cleared ({reason}).");
        }

        private static bool TryReadHealthHandlerIsDead(object healthHandler)
        {
            if ((object)healthHandler == null) return false;
            try
            {
                var ciF = AccessTools.Field(healthHandler.GetType(), "characterInformation");
                if ((object)ciF == null)
                    ciF = AccessTools.Field(healthHandler.GetType(), "mCharacterInformation");
                if ((object)ciF == null) return true;
                var ci = ciF.GetValue(healthHandler);
                if ((object)ci == null) return true;
                var deadField = AccessTools.Field(ci.GetType(), "isDead");
                if ((object)deadField != null) return (bool)deadField.GetValue(ci);
                var deadProp = AccessTools.Property(ci.GetType(), "isDead");
                if ((object)deadProp != null) return (bool)deadProp.GetValue(ci, null);
                return true;
            }
            catch { return true; }
        }

        /// <summary>Death signal — clears gate and schedules next map (queues during map load).</summary>
        internal void ScheduleRoundAdvanceOnDeath(string reason)
        {
            AcResetRound();
            ClearRoundAdvanceBlockedGate(reason);
            TryScheduleRoundAdvance(reason);
        }

        internal void OnOraclePlayerDied(object healthHandler, string reason)
        {
            ScheduleRoundAdvanceOnDeath(reason);
        }

        private void TryPatchHealthHandlerDieForRoundAdvance(Harmony harmony)
        {
            try
            {
                var hhType = AccessTools.TypeByName("HealthHandler");
                if ((object)hhType == null) { Log.LogWarning("[DEATH] HealthHandler type not found."); return; }
                var dieMethod = AccessTools.Method(hhType, "Die");
                if ((object)dieMethod == null) dieMethod = AccessTools.Method(hhType, "OnDeath");
                if ((object)dieMethod == null) { Log.LogWarning("[DEATH] HealthHandler.Die not found."); return; }
                var postfix = AccessTools.Method(typeof(Plugin), nameof(HealthHandlerDiePostfix));
                harmony.Patch(dieMethod, postfix: new HarmonyMethod(postfix));
                Log.LogInfo("[DEATH] Patched HealthHandler.Die — schedules round advance when isDead.");
            }
            catch (Exception e) { Log.LogWarning($"[DEATH] HealthHandler patch failed: {e.Message}"); }
        }

        private static void HealthHandlerDiePostfix(object __instance)
        {
            if ((object)Instance == null) return;
            Instance.OnOraclePlayerDied(__instance, "HealthHandler.Die");
        }

        private void TryScheduleRoundAdvance(string reason)
        {
            if (_pendingRoundAdvanceAt >= 0f) return;
            float now = Time.realtimeSinceStartup;
            if (_roundAdvanceBlockedUntil > 0f && now < _roundAdvanceBlockedUntil)
            {
                Log.LogInfo($"[SF] Round advance ignored ({reason}): map grace {(_roundAdvanceBlockedUntil - now):0.0}s left.");
                return;
            }
            if (IsOracleMapLoadInProgress())
            {
                _roundAdvanceQueuedAfterMapLoad = true;
                Log.LogInfo($"[SF] Round advance queued ({reason}): map load in progress.");
                return;
            }
            if (!_matchStarted)
            {
                Log.LogDebug($"[SF] Round advance ignored ({reason}): match not started.");
                return;
            }
            _pendingRoundAdvanceAt = now + RoundEndDelaySec;
            Log.LogInfo($"[SF] Round advance scheduled ({reason}) in {RoundEndDelaySec:0.0}s — clients={CountInitializedSfClients()} soloTest={IsSoloTestLobby()}");
        }

        internal void FlushQueuedRoundAdvanceAfterMapLoad(string reason)
        {
            if (!_roundAdvanceQueuedAfterMapLoad || _pendingRoundAdvanceAt >= 0f) return;
            _roundAdvanceQueuedAfterMapLoad = false;
            if (!_matchStarted) return;
            _pendingRoundAdvanceAt = Time.realtimeSinceStartup + RoundEndDelaySec;
            Log.LogInfo($"[SF] Round advance scheduled ({reason}) after map load in {RoundEndDelaySec:0.0}s.");
        }

        private static bool TryGetRigIsDead(GameObject rig)
        {
            if ((object)rig == null) return false;
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType == null) return false;
                var ctrl = rig.GetComponent(ctrlType);
                if ((object)ctrl == null) return false;
                var infoF = AccessTools.Field(ctrlType, "info");
                if ((object)infoF == null) return false;
                var infoVal = infoF.GetValue(ctrl);
                if ((object)infoVal == null) return false;
                var deadF = AccessTools.Field(infoVal.GetType(), "isDead");
                if ((object)deadF != null) return (bool)deadF.GetValue(infoVal);
                var deadP = AccessTools.Property(infoVal.GetType(), "isDead");
                if ((object)deadP != null) return (bool)deadP.GetValue(infoVal, null);
            }
            catch { }
            return false;
        }

        private void TickAuthRigDeathCheck()
        {
            if (SlotToRig.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now - _authDeathCheckAt < 0.25f) return;
            _authDeathCheckAt = now;
            foreach (var kv in SlotToRig)
            {
                if (_deathSlotsHandled.Contains(kv.Key)) continue;
                if ((object)kv.Value == null) continue;
                if (!TryGetRigIsDead(kv.Value)) continue;
                _deathSlotsHandled.Add(kv.Key);
                Log.LogInfo($"[DEATH] Auth rig slot {kv.Key} isDead — scheduling round advance.");
                ScheduleRoundAdvanceOnDeath($"auth-rig-dead slot={kv.Key}");
            }
        }

        private void ResetDeathTrackingForNewRound()
        {
            _deathSlotsHandled.Clear();
            _roundAdvanceQueuedAfterMapLoad = false;
        }

        /// <summary>Solo-only: death without scoring still reloads oracle map logic for QA.</summary>
        private void TryScheduleSoloTestRoundAdvance(string reason)
        {
            if (!IsSoloTestLobby()) return;
            TryScheduleRoundAdvance(reason);
        }
        private float _pendingStartMatchAt = -1f;
        private int _roundCounter;

        // All 123 dumped Landfall map scene indices.
        // Range 1-124 minus 102 (the stats / non-MP scene). Some early scenes
        // (1-5) may be menu / lobby — they're left in; user can re-die if one
        // doesn't load. SF's stock GetNextLevel uses MapSelectionHandler UI
        // which isn't initialized on the oracle, so we can't call it directly.
        private static readonly int[] _allLandfallMaps;
        private static readonly System.Random _mapRng = new System.Random();
        static Plugin()
        {
            // Build the playable scene set. Excludes:
            //  - 1-5  : menu / lobby scenes
            //  - 102  : the stats / non-MP scene
            //  - SF_EXCLUDE_MAPS : a comma list of extra scene indices to drop —
            //           this is how you remove the LEVEL EDITOR scene (and any
            //           other map that bugs the round logic) from the rotation
            //           WITHOUT a recompile, e.g. SF_EXCLUDE_MAPS="7,118".
            var excluded = new HashSet<int> { 102 };
            try
            {
                string ex = System.Environment.GetEnvironmentVariable("SF_EXCLUDE_MAPS");
                if (!string.IsNullOrEmpty(ex))
                    foreach (var tok in ex.Split(','))
                    {
                        int v;
                        if (int.TryParse(tok.Trim(), out v)) excluded.Add(v);
                    }
            }
            catch { }
            var list = new List<int>();
            for (int i = 6; i <= 124; i++) { if (!excluded.Contains(i)) list.Add(i); }
            _allLandfallMaps = list.ToArray();
        }
        // Exposed so a chat command / log can show what's excluded.
        internal static string ExcludedMapsInfo()
        {
            string ex = System.Environment.GetEnvironmentVariable("SF_EXCLUDE_MAPS");
            return string.IsNullOrEmpty(ex) ? "102 (stats)" : ("102 (stats), " + ex);
        }
        // Recently-played history so we don't revisit the same map back-to-back
        // (or within the last few rounds).
        private static readonly Queue<int> _recentMaps = new Queue<int>();
        private const int _recentMapsAvoidWindow = 6;

        private void AdvanceRound()
        {
            ResetDeathTrackingForNewRound();
            _roundCounter++;
            int nextScene;
            if (FixedMap >= 0)
            {
                // Pin every round to one scene (RL training wants a CONSISTENT
                // environment — random maps each episode make learning the
                // map geometry / not falling off impossible). SF_FIXED_MAP=N.
                nextScene = FixedMap;
            }
            else
            {
                // Pick a random scene we haven't visited in the last few rounds.
                nextScene = _allLandfallMaps[_mapRng.Next(_allLandfallMaps.Length)];
                for (int attempt = 0; attempt < 8 && _recentMaps.Contains(nextScene); attempt++)
                    nextScene = _allLandfallMaps[_mapRng.Next(_allLandfallMaps.Length)];
            }
            _recentMaps.Enqueue(nextScene);
            while (_recentMaps.Count > _recentMapsAvoidWindow) _recentMaps.Dequeue();
            _currentSceneIndex = nextScene;
            _roundAdvanceBlockedUntil = Time.realtimeSinceStartup + RoundMinPlaySec;
            bool solo = IsSoloTestLobby();
            Log.LogInfo($"[SF] Round advance #{_roundCounter}: MapChange → scene {nextScene} (winner=255, soloTest={solo})");
            // ChangeMap body: [byte winnerIndex=255 (no winner)][byte mapType=0 (Landfall)][int32 sceneIndex LE]
            byte[] body = new byte[1 + 1 + 4];
            body[0] = 255;
            body[1] = 0;
            WriteU32LE(body, 2, (uint)nextScene);
            BroadcastSfPacket(PktMapChange, body, 0, 0);
            // SF's host normally follows MapChange with StartMatch after
            // clients re-ready up. Stock SF uses k_MAX_SECONDS_UNTIL_AUTO_START=3s
            // but the client's map-load animation eats most of that. Defaulting
            // to 2s; configurable via SF_NEXT_MATCH_DELAY env var.
            _pendingStartMatchAt = Time.realtimeSinceStartup + NextMatchDelaySec;
            _pendingRearmCombatAt = -1f;
            _oracleCountDownAt = -1f;
            _oracleCountDownFired = false;
            ResetOracleStateForRoundAdvance();
            ClearMapDataObjectsOnOracle();
            _cachedGroundWeaponsBody = null;
            _groundWeaponsEntryCount = 0;
            _skyWeaponSpawnCount = 0;
            _oracleNextSkyWeaponAt = -1f;
            ScheduleOracleReloadCurrentMap("AdvanceRound");
            // Reset Spawned flags so next ClientRequestingToSpawn is honored.
            foreach (var kv in _sfClients) kv.Value.Spawned = false;
        }

        /// <summary>Let the next round re-register map sync, NSOs, and auth rigs on the new scene.</summary>
        private void ResetOracleStateForRoundAdvance()
        {
            _authSpawnDone = false;
            _authSpawnAt = -1f;
            _nsoInventoryDone = false;
            _nsoInventoryAt = -1f;
            // Bots are destroyed with the auth rigs below (they live in SlotToRig)
            // and are NOT respawned by SpawnAuthoritativePlayersForAllClients
            // (that path is per-connected-client; bots have no client). Re-arm
            // the one-shot auto-spawn so AutoSpawnBots re-fires on the new map —
            // the NSO-inventory block reschedules _botAutoSpawnAt each round.
            _botAutoSpawnDone = false;
            _botLastInFight = false;
            _mapSyncObjectsRegistered = 0;
            _oraclePreCombatReadyAt = -1f;
            _oraclePreCombatSceneIndex = -1;
            // NOTE: _nsoLastBroadcastPos and _nsoLastMovedAt are NOT cleared here.
            // They're keyed by NSO ushort Index which gets reassigned per scene;
            // stale entries for retired indices are harmless because
            // CollectActiveNsoSnapshot finds live components fresh via
            // FindObjectsOfType each tick. PRIOR BEHAVIOR (clearing them) caused
            // a permanent post-round-advance lockout where nsos=0 between
            // keyframes because no per-NSO entry could ever satisfy
            // `recentlyActive` again — see Bug B in
            // notes/bug-investigations/2026-05-24_v0.3.4-session-bugs.md.
            _nsoSpawnPos.Clear();
            _nsoByIndexCache.Clear();
            _nsoCacheLastRebuildAt = -1f;
            ClearAuthoritativeRigsForRoundAdvance();
        }

        private void ClearAuthoritativeRigsForRoundAdvance()
        {
            if (SlotToRig.Count == 0) return;
            int destroyed = 0;
            foreach (var kv in SlotToRig)
            {
                if ((object)kv.Value != null)
                {
                    UnityEngine.Object.Destroy(kv.Value);
                    destroyed++;
                }
            }
            SlotToRig.Clear();
            Log.LogInfo($"[SF] Round advance: cleared {destroyed} authoritative rig(s) for next map.");
        }

        // Pickup: re-broadcast incoming ClientRequestingWeaponPickUp body as
        // WeaponWasPickedUp to ALL clients (including the sender, so their
        // local game updates the weapon-attached state). Body is identical.
        private void HandlePickupRequest(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 3) { Log.LogWarning($"[SF] pickup request too short ({len} bytes)"); return; }
            byte[] body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            byte playerIdx = body[0];
            ushort weaponNetId = (ushort)(body[1] | (body[2] << 8));
            Log.LogInfo($"[SF] Pickup: player={playerIdx} weapon={weaponNetId} → broadcasting WeaponWasPickedUp");
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktWeaponWasPickedUp, body, 0uL, 1); // channel 1 (weapon-events)
            }
        }

        // Drop: client sends ClientRequestWeaponDrop with [playerIdx][posY i16][posZ i16][velY i8][velZ i8].
        // SF host appends GetNextWeaponSpawnID() + GetNextSyncableObjectSpawnID()
        // and broadcasts as WeaponDropped. We mirror that — the IDs are just
        // counters, no state lookup required.
        private ushort _droppedWeaponNextId = 32768;       // give drops a distinct range to avoid colliding with spawn IDs
        private ushort _droppedSyncableNextId = 32768;
        private void HandleDropRequest(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 7) { Log.LogWarning($"[SF] drop request too short ({len} bytes)"); return; }
            byte[] body = new byte[len + 4];
            Buffer.BlockCopy(data, off, body, 0, len);
            ushort wid = _droppedWeaponNextId++;
            ushort sid = _droppedSyncableNextId++;
            body[len + 0] = (byte)(wid & 0xFF);
            body[len + 1] = (byte)((wid >> 8) & 0xFF);
            body[len + 2] = (byte)(sid & 0xFF);
            body[len + 3] = (byte)((sid >> 8) & 0xFF);
            Log.LogInfo($"[SF] Drop: assigning weaponSpawnID={wid} syncableID={sid}");
            // WeaponDropped is in P2PPackageHandler.CheckMessageType (line 268),
            // dispatched on channel 0 in the patched-DLL routing.
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktWeaponDropped, body, 0uL, 0);
            }
        }

        // Throw: client sends RequestingWeaponThrow (21) — same shape as drop
        // structurally: SF's OnPlayerThrowWeapon appends weaponSpawnID +
        // syncableObjectSpawnID and broadcasts as WeaponThrown (20).
        // WeaponThrown is NOT in CheckMessageType; it's dispatched via
        // NetworkPlayer.ListenForEventPackages on the SENDER's mEventChannel
        // (= slot*2 + 3). Wrong channel → packet arrives but nothing listens.
        private void HandleThrowRequest(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 1) { Log.LogWarning($"[SF] throw request too short ({len} bytes)"); return; }
            byte[] body = new byte[len + 4];
            Buffer.BlockCopy(data, off, body, 0, len);
            ushort wid = _droppedWeaponNextId++;
            ushort sid = _droppedSyncableNextId++;
            body[len + 0] = (byte)(wid & 0xFF);
            body[len + 1] = (byte)((wid >> 8) & 0xFF);
            body[len + 2] = (byte)(sid & 0xFF);
            body[len + 3] = (byte)((sid >> 8) & 0xFF);
            byte throwChannel = (byte)(sender.Slot * 2 + 3);
            Log.LogInfo($"[SF] Throw: assigning weaponSpawnID={wid} syncableID={sid} (incoming bodyLen={len}, slot={sender.Slot} → channel={throwChannel})");
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktWeaponThrown, body, 0uL, throwChannel);
            }
        }

        // === packet handlers ===

        private void HandlePing(SfClient cli, byte[] data, int off, int len)
        {
            // Echo the body back as PingResponse.
            byte[] body = new byte[len];
            if (len > 0) System.Buffer.BlockCopy(data, off, body, 0, len);
            SendSfPacket(cli.Addr, PktPingResponse, body, cli.SteamID, 0);
        }

        private void HandleClientRequestingAccepting(SfClient cli)
        {
            Log.LogInfo($"[SF] ClientRequestingAccepting from {cli.Addr}; sending ClientAccepted.");
            SendSfPacket(cli.Addr, PktClientAccepted, new byte[0], cli.SteamID, 0);
            cli.Accepted = true;
        }

        // ClientRequestingIndex → ClientInit. Per reference_patched_dll_protocol.md:
        // Response body (50 bytes for solo Landfall-0):
        //   byte accept (1)
        //   byte playerIndex (assigned slot)
        //   byte maxPlayers (4)
        //   byte mapType (0 = Landfall)
        //   i32 mapSize (4)
        //   i32 sceneIndex
        //   for slot 0..3: u64 slotSteamID + (stats if non-joiner non-empty)
        //   u16 weaponCount (0)
        //   4 bytes networkOptions (mapToggle, health, regen, weaponSpawnRate)
        private void HandleClientRequestingIndex(SfClient cli, byte[] data, int off, int len)
        {
            // Body layout (per patched DLL's OnPlayerRequestingIndex):
            //   u64 SteamID  +  u8 clientPlayerCount  (+ optional protocol-version byte)
            // The SteamID here is the AUTHORITATIVE client identity — the
            // wrapper-tail steamID is 0 on join. Without this, our ClientInit
            // tells the client slot 0 has SteamID 0 → client doesn't match
            // it against its local Steam ID → ControlledLocally stays false
            // → no ClientRequestingToSpawn → stuck in lobby.
            if (len >= 8)
            {
                ulong newSid = ReadU64LE(data, off);
                if (cli.SteamID != 0 && cli.SteamID != newSid)
                    Log.LogWarning($"[SF DEBUG] cli {cli.Addr} slot={cli.Slot} SteamID CHANGING {cli.SteamID} → {newSid} (in HandleClientRequestingIndex)");
                cli.SteamID = newSid;
            }
            byte playerCount = (len >= 9) ? data[off + 8] : (byte)1;

            // Evict any prior _sfClients entry with the same SteamID — this
            // is a reconnect, and we want to reuse the original slot so the
            // client's view of "I am slot N" matches the oracle's view.
            // Without this, slot AllocSlot picks the next free slot (1, 2, …)
            // and the client's channel-routed packets (e.g. throw on
            // slot*2+3) go to wrong channels.
            if (cli.SteamID != 0)
            {
                List<string> evict = null;
                foreach (var kv in _sfClients)
                {
                    var other = kv.Value;
                    if (other == cli) continue;
                    if (other.SteamID == cli.SteamID)
                    {
                        if (evict == null) evict = new List<string>();
                        evict.Add(kv.Key);
                        Log.LogInfo($"[SF] Evicting stale reconnect: SteamID={other.SteamID} was on {kv.Key} slot={other.Slot}; new conn from {cli.Addr} reusing slot {other.Slot}.");
                        cli.Slot = other.Slot;
                    }
                }
                if (evict != null) foreach (var k in evict) _sfClients.Remove(k);
            }

            // Assign a slot only if eviction didn't reuse one.
            int slot = cli.Slot >= 0 ? cli.Slot : AllocSlot(cli);
            cli.Slot = slot;
            Log.LogInfo($"[SF] ClientRequestingIndex from {cli.Addr} steamID={cli.SteamID} players={playerCount}; assigning slot {slot}; building ClientInit.");

            // The chosen Landfall scene to push (matches what oracle has loaded).
            int sceneIndex = 0; // MainScene first; the lobby UI flows from there.

            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write((byte)1);            // accept
                bw.Write((byte)slot);         // playerIndex
                bw.Write((byte)4);            // maxPlayers — patched-only field
                bw.Write((byte)0);            // mapType 0=Landfall
                bw.Write((int)4);             // mapSize
                bw.Write((int)sceneIndex);    // mapData (sceneIndex)

                // 4-slot loop
                for (int s = 0; s < 4; s++)
                {
                    if (s == slot)
                    {
                        bw.Write(cli.SteamID);          // u64 — joiner's own steamID
                    }
                    else
                    {
                        // Find any other connected client in slot s.
                        SfClient other = null;
                        foreach (var kv in _sfClients) if (kv.Value.Slot == s) { other = kv.Value; break; }
                        if (other != null)
                        {
                            bw.Write(other.SteamID);
                            // 13 × int32 stats (zeros for now)
                            for (int i = 0; i < 13; i++) bw.Write((int)0);
                            bw.Write((int)0); // colorCount (patched-only) — 0 = default
                        }
                        else
                        {
                            bw.Write((ulong)0);
                        }
                    }
                }
                bw.Write((ushort)0);          // weaponCount
                bw.Write((byte)0);            // mapToggle
                bw.Write((byte)100);          // health
                bw.Write((byte)0);            // regen — DSF comp ruleset: no regen
                bw.Write((byte)1);            // weaponSpawnRate

                byte[] body = ms.ToArray();
                SendSfPacket(cli.Addr, PktClientInit, body, cli.SteamID, 0);
                cli.Initialized = true;
            }

            // Post-init bundle. Per sfdsrv comment: without these (specifically
            // OptionsChanged) the client never sends ClientRequestingToSpawn
            // and the user gets stuck at a black/lobby screen.
            // WorkshopMapsLoaded: u16 count + count×u64 workshopIDs. We send 0 maps.
            SendSfPacket(cli.Addr, PktWorkshopMapsLoaded, new byte[] { 0, 0 }, cli.SteamID, 1);
            // OptionsChanged: 4 bytes [maps, health, regen, weaponSpawnRate].
            // weaponSpawnRate=2 stops the client from requesting weapon spawns.
            // regen=0 — DSF comp ruleset (and matches host-side OptionsHolder).
            SendSfPacket(cli.Addr, PktOptionsChanged, new byte[] { 0, 100, 0, 2 }, cli.SteamID, 0);
            Log.LogInfo($"[SF] Post-init bundle sent (WorkshopMapsLoaded + OptionsChanged).");
        }

        // After first player spawns in the lobby, auto-start a match. The
        // stock SF lobby requires 2+ players to walk under the ready-hat
        // trigger; for solo testing that never fires. So we schedule the
        // match-start ourselves a few seconds after first spawn.
        private float _autoStartAt = -1f;

        // ClientRequestingToSpawn → ClientSpawned broadcast.
        // Incoming body: byte playerIndex + 6 × float32 (pos + euler) = 25 bytes
        // Outgoing body: byte playerIndex + 6×f32 + bool spawnFlag + i32 colorCount = 30 bytes
        private void HandleClientRequestingToSpawn(SfClient cli, byte[] data, int off, int len)
        {
            if (len < 25) { Log.LogWarning($"[SF] short spawn body len={len}"); return; }
            byte pIdx  = data[off];
            float px = ReadF32LE(data, off + 1);
            float py = ReadF32LE(data, off + 5);
            float pz = ReadF32LE(data, off + 9);
            float rx = ReadF32LE(data, off + 13);
            float ry = ReadF32LE(data, off + 17);
            float rz = ReadF32LE(data, off + 21);
            Log.LogInfo($"[SF] ClientRequestingToSpawn slot={pIdx} pos=({px:0.0},{py:0.0},{pz:0.0})");

            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write(pIdx);
                bw.Write(px); bw.Write(py); bw.Write(pz);
                bw.Write(rx); bw.Write(ry); bw.Write(rz);
                bw.Write((byte)0);    // spawnFlag false = RevivePlayer at pos
                bw.Write((int)0);     // colorCount (patched-only)
                byte[] spawnBody = ms.ToArray();

                // ORDER MATTERS: existing clients need PktClientJoined BEFORE
                // PktClientSpawned so their mConnectedClients[slot] is
                // populated before OnPlayerSpawned reads it (line 1623 of
                // MultiplayerManager.cs decompile: reads slot then accesses
                // .ControlledLocally; null slot → NullRef → broken rig).
                //
                // Body: u8 slot + u64 steamID LE.
                byte[] joinBody = new byte[9];
                joinBody[0] = (byte)cli.Slot;
                ulong sid = cli.SteamID;
                for (int b = 0; b < 8; b++) joinBody[1 + b] = (byte)(sid >> (8 * b));
                int notified = 0;
                foreach (var kv in _sfClients)
                {
                    if (kv.Value == cli) continue;
                    SendSfPacket(kv.Value.Addr, PktClientJoined, joinBody, cli.SteamID, 0);
                    notified++;
                }
                if (notified > 0)
                    Log.LogInfo($"[SF] step1: sent PktClientJoined slot={cli.Slot} steamID={cli.SteamID} → {notified} existing client(s)");

                // Now safe to broadcast ClientSpawned. New client gets their
                // own echo; existing clients have mConnectedClients[slot]
                // populated so OnPlayerSpawned can read it cleanly.
                BroadcastSfPacket(PktClientSpawned, spawnBody, cli.SteamID, 0);
                Log.LogInfo($"[SF] step2: broadcast PktClientSpawned slot={cli.Slot} pos=({px:0.0},{py:0.0},{pz:0.0}) to all {_sfClients.Count} client(s)");
            }
            cli.Spawned = true;
            SendCachedGroundWeaponsToClient(cli);

            // Match no longer auto-starts. Players spawn into the lobby and
            // wait for /start in chat. Host can type /start to begin.

            // Phase 6.15.1 — welcome message via chat. Sent once per spawn so
            // the player knows the server's identity + commands available.
            // ALKA's sendJoinHelpMessages does the same on the Go server side.
            if (!cli.SentWelcome)
            {
                cli.SentWelcome = true;
                string code = Environment.GetEnvironmentVariable("SF_LOBBY_CODE") ?? "?";
                SendChatToPlayer(cli, $"Welcome to lobby {code}. Type /help for commands.");
            }
        }

        // ClientReadyUp from client (walked through the ready hat in lobby).
        // Body: byte playerCount + playerCount × byte playerIndex.
        // Response: broadcast MapChange (load Landfall scene) + StartMatch.
        // Once both go out, clients drop the lobby map, load the new scene,
        // and send ClientRequestingToSpawn for it — we reply with ClientSpawned.
        private bool _matchStarted = false;
        private int _currentSceneIndex = 6; // Desert3 — known-good Landfall map
        private void HandleClientReadyUp(SfClient cli, byte[] data, int off, int len)
        {
            // Match no longer auto-starts on ClientReadyUp. Host types /start
            // in chat. ClientReadyUp is still logged so we can see the
            // ready-button-walk-through.
            Log.LogInfo($"[SF] ClientReadyUp from {cli.Addr} bodyLen={len} — ignored; waiting for /start chat command.");
            if (_matchStarted && _pendingClientStartMatchFired)
            {
                Log.LogInfo($"[SF] Match already started; re-sending StartMatch to {cli.Addr} only.");
                SendSfPacket(cli.Addr, PktStartMatch, new byte[0], 0, 0);
            }
        }

        // Consolidated match-start sequence. Called by /start chat or by
        // anything else that wants to begin a match. Idempotent — second call
        // while a match is in progress just logs and returns.
        private void FireMatchStart(string source)
        {
            if (_matchStarted)
            {
                Log.LogInfo($"[SF] FireMatchStart({source}) — already started, no-op.");
                return;
            }
            ResetDeathTrackingForNewRound();
            Log.LogInfo($"[SF] FireMatchStart({source}) — MapChange now; StartMatch to clients after load window.");
            BroadcastMapChange(_currentSceneIndex);
            _pendingClientStartMatchAt = Time.realtimeSinceStartup + Mathf.Max(5f, NextMatchDelaySec + 2f);
            _pendingClientStartMatchFired = false;
            _matchStarted = true;
            _roundAdvanceBlockedUntil = Time.realtimeSinceStartup + RoundMinPlaySec;
            _sceneLoadRealtime = Time.realtimeSinceStartup;
            try
            {
                var mhType = AccessTools.TypeByName("MatchmakingHandler");
                if ((object)mhType != null)
                {
                    var setNetMatch = AccessTools.Method(mhType, "SetNetworkMatch");
                    if ((object)setNetMatch != null)
                    {
                        setNetMatch.Invoke(null, new object[] { true });
                        Log.LogInfo("[P6.5] MatchmakingHandler.SetNetworkMatch(true).");
                    }
                }
                ScheduleOracleReloadCurrentMap("FireMatchStart");
            }
            catch (Exception e) { Log.LogError($"[P6.5] FireMatchStart scheduling failed: {e}"); }
        }

        // MapChange body: byte winnerIndex + byte mapType + mapData.
        // For a fresh start: winnerIndex=255 (no winner), mapType=0 (Landfall),
        // mapData=i32 sceneIndex LE.
        private void BroadcastMapChange(int sceneIndex)
        {
            byte[] body = new byte[1 + 1 + 4];
            body[0] = 255;             // winnerIndex (no winner)
            body[1] = 0;               // mapType Landfall
            WriteU32LE(body, 2, (uint)sceneIndex);
            BroadcastSfPacket(PktMapChange, body, 0, 0);
            Log.LogInfo($"[SF] Broadcast MapChange → scene {sceneIndex}");
        }

        private void BroadcastStartMatch()
        {
            BroadcastSfPacket(PktStartMatch, new byte[0], 0, 0);
            // Clear per-round spawn flag so next ClientRequestingToSpawn
            // is treated as a fresh round-start rather than a respawn.
            foreach (var kv in _sfClients) kv.Value.Spawned = false;
            Log.LogInfo("[SF] Broadcast StartMatch");
            // Weapons/combat re-arm only after PostMapLoad pre-combat grace (RunOraclePreCombatStart).
            _pendingRearmCombatAt = -1f;
        }

        // Phase 6.12 — inbound v26 PktPlayerInput from SFClientRecon plugin.
        // Body layout:
        //   u32 sequenceNum (LE)
        //   u8  slot
        //   f32 stickX (LE)
        //   f32 stickY (LE)
        //   f32 aimX (LE)
        //   f32 aimY (LE)
        //   u32 buttons (LE)  — bit0=jump, bit1=fire, bit2=block, bit3=throw
        //
        // We trust the slot byte for now (no anti-cheat enforcement). Phase
        // 6.13+ will validate slot ↔ SteamID. Populated InputFrame feeds
        // InjectInputPrefix → Movement.cs on the server-side authoritative
        // rig, producing real authoritative motion that's then broadcast back
        // via PktWorldStateSnapshot.
        // Per-slot v26 endpoint — where to send WorldStateSnapshot. Discovered
        // from the source IP+port of each client's first PlayerInput packet,
        // so multi-instance same-machine testing works (no more hardcoded port
        // collision when two clients on same host both want 1339).
        private readonly Dictionary<int, IPEndPoint> _slotV26Endpoint = new Dictionary<int, IPEndPoint>();

        private uint _inputPacketsRx;
        private uint _inputPacketsDropped;
        private void HandlePlayerInput(byte[] data, int off, int len, IPEndPoint from)
        {
            if (len < 25) return;
            uint seq    = (uint)(data[off] | (data[off + 1] << 8) | (data[off + 2] << 16) | (data[off + 3] << 24));
            byte slot   = data[off + 4];
            float sx    = BitConverter.ToSingle(data, off + 5);
            float sy    = BitConverter.ToSingle(data, off + 9);
            float ax    = BitConverter.ToSingle(data, off + 13);
            float ay    = BitConverter.ToSingle(data, off + 17);
            uint btns   = (uint)(data[off + 21] | (data[off + 22] << 8) | (data[off + 23] << 16) | (data[off + 24] << 24));

            // Defensive validation — drop obvious garbage / cheaty inputs so
            // they don't poison InjectInputPrefix → Movement.cs. Conservative:
            // accept slightly-over-1.0 magnitudes (analog stick noise) but
            // reject NaN/Inf/huge values. Phase 6.16+ slot↔SteamID validation
            // would also live here.
            if (slot > 3
                || float.IsNaN(sx) || float.IsInfinity(sx) || sx < -1.5f || sx > 1.5f
                || float.IsNaN(sy) || float.IsInfinity(sy) || sy < -1.5f || sy > 1.5f
                || float.IsNaN(ax) || float.IsInfinity(ax) || ax < -1.5f || ax > 1.5f
                || float.IsNaN(ay) || float.IsInfinity(ay) || ay < -1.5f || ay > 1.5f)
            {
                _inputPacketsDropped++;
                if (_inputPacketsDropped == 1 || _inputPacketsDropped % 50 == 0)
                    Log.LogWarning($"[P6.12] Dropped malformed PlayerInput: slot={slot} stick=({sx:0.00},{sy:0.00}) aim=({ax:0.00},{ay:0.00}) — total dropped {_inputPacketsDropped}");
                return;
            }
            // Clamp stick magnitudes to canonical [-1,1] so SF's Movement
            // doesn't see sub-noise > 1.0 values that bypass its own clamps.
            sx = Mathf.Clamp(sx, -1f, 1f);
            sy = Mathf.Clamp(sy, -1f, 1f);
            ax = Mathf.Clamp(ax, -1f, 1f);
            ay = Mathf.Clamp(ay, -1f, 1f);

            SlotInputs[slot] = new InputFrame
            {
                StickX  = sx,
                StickY  = sy,
                AimX    = ax,
                AimY    = ay,
                Buttons = (int)btns,
            };
            // Find the SfClient owning this slot to stamp LastInputSeq AND refresh
            // LastSeen. Without the LastSeen update, a client that finished the
            // lobby handshake and is streaming v26 PlayerInput at 60Hz still got
            // swept as "stale" (the game-socket LastSeen went cold) → the player
            // dropped from the server seconds after connecting. v26 input IS proof
            // of life.
            foreach (var kv in _sfClients)
            {
                if (kv.Value.Slot == slot)
                {
                    kv.Value.LastInputSeq = seq;
                    kv.Value.LastSeen = Time.realtimeSinceStartup;
                    break;
                }
            }
            // Record this client's v26 source addr — server snapshots get sent
            // back to this same IP:port (client uses single bidirectional socket).
            if ((object)from != null)
            {
                if (!_slotV26Endpoint.TryGetValue(slot, out var existing) || !existing.Equals(from))
                {
                    _slotV26Endpoint[slot] = from;
                    Log.LogInfo($"[P6.12] Slot {slot} v26 endpoint → {from}");
                    // P0-13 — send a full-keyframe snapshot to this new
                    // endpoint so it learns the current position of every
                    // NSO, not just the ones currently moving. The regular
                    // snapshot stream filters at-rest NSOs; without this
                    // keyframe a late-joining client would never learn the
                    // box positions until something pushed them.
                    if (_matchStarted)
                    {
                        try { SendKeyframeSnapshotToEndpoint(from); }
                        catch (Exception ex) { Log.LogWarning($"[P0-13] keyframe send failed: {ex.Message}"); }
                    }
                }
            }
            _inputPacketsRx++;
            if (_inputPacketsRx == 1 || _inputPacketsRx % 300 == 0)
                Log.LogInfo($"[P6.12] PlayerInput #{_inputPacketsRx} slot={slot} seq={seq} stick=({sx:0.00},{sy:0.00}) btns=0x{btns:X}");
        }

        // PlayerUpdate from client → broadcast to all OTHER clients (so they
        // render this player) AND teleport this client's auth ghost rig to
        // the reported position so it can physically push boxes server-side.
        //
        // CHANNEL ROUTING IS CRITICAL: SF's NetworkPlayer.InitNetworkSpawnID
        // assigns `mUpdateChannel = slot * 2 + 2`, and incoming packets get
        // dispatched to the matching NetworkPlayer by channel. Forwarding on
        // channel 0 (our old behavior) means the receiving client doesn't
        // route the update to the sender's NetworkPlayer — the remote player
        // appears frozen. We must forward on the SAME channel we received on.
        //
        // The incoming channel encodes the sender's slot, so we don't need
        // to look it up — just preserve the byte.
        //
        // Body format (from NetworkPlayer.SyncClientState): first 4 bytes
        // are posY + posZ as int16 / 100.
        private void HandlePlayerUpdate(SfClient cli, byte[] data, int off, int len, byte channel)
        {
            byte[] body = new byte[len];
            if (len > 0) System.Buffer.BlockCopy(data, off, body, 0, len);
            foreach (var kv in _sfClients)
            {
                if (kv.Value == cli) continue;
                // Gate on Initialized (set after ClientInit) — NOT on Spawned,
                // which BroadcastStartMatch resets to false at /start. Stock SF
                // expects re-spawn via new ClientRequestingToSpawn, but during
                // the gap PlayerUpdates would otherwise stop forwarding. Causes
                // the 'movement syncs in lobby but not after /start' bug.
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktPlayerUpdate, body, cli.SteamID, channel);
            }

            if (len < 4 || cli.Slot < 0) return;
            short rawY = (short)(body[0] | (body[1] << 8));
            short rawZ = (short)(body[2] | (body[3] << 8));
            float py = rawY / 100f;
            float pz = rawZ / 100f;
            UpdateGhostRigPosition(cli.Slot, new Vector3(0f, py, pz));
        }

        // === Phase 6.9 — authoritative server-side player rigs ===
        // Spawn one real NetworkPlayer per connected client on the oracle.
        // The rig is the server's authoritative copy of the player; eventually
        // it'll be driven from client inputs (Phase 6.12 v26 protocol) and its
        // position will be broadcast back to all clients as the source of
        // truth (Phase 6.10 snapshots + Phase 6.11 client reconciliation).
        //
        // For now (post-mirror-rig rip), the rig is instantiated via
        // TrySpawnPlayer (real Player prefab + Controller + Movement + NSO
        // children) and left at its spawn position. The SlotInputs buffer
        // is empty until inputs start flowing, so Movement.cs has nothing to
        // act on. This is intentional: clean foundation, no fake teleport.
        private float _authSpawnAt = -1f;
        private bool _authSpawnDone;

        private void SpawnAuthoritativePlayersForAllClients()
        {
            Log.LogInfo($"[P6.9] SpawnAuthoritativePlayers: iterating {_sfClients.Count} clients.");
            // [SF DEBUG] dump every SfClient's state before we spawn — this
            // is the ground-truth snapshot of who-is-who at this moment.
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                Log.LogInfo($"[SF DEBUG]   _sfClients[{kv.Key}] → Slot={c.Slot} SteamID={c.SteamID} Spawned={c.Spawned} Initialized={c.Initialized}");
            }
            int considered = 0, spawned = 0, skipped = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                considered++;
                if (!cli.Initialized)
                {
                    Log.LogInfo($"[P6.9] Skip {kv.Key}: not Initialized.");
                    skipped++;
                    continue;
                }
                if (SlotToRig.ContainsKey(cli.Slot))
                {
                    Log.LogInfo($"[P6.9] Skip {kv.Key}: rig already exists for slot {cli.Slot}.");
                    skipped++;
                    continue;
                }
                Vector3 startPos = new Vector3(0f, 8f, 0f);
                bool ok = TrySpawnPlayer(cli.Slot, startPos, out string err);
                if (ok)
                {
                    Log.LogInfo($"[P6.9] Spawned authoritative rig for client slot={cli.Slot} steamID={cli.SteamID}.");
                    ConfigureAuthoritativeRig(cli.Slot);
                    spawned++;
                }
                else
                {
                    Log.LogError($"[P6.9] Failed to spawn authoritative rig for slot {cli.Slot}: {err}");
                }
            }
            Log.LogInfo($"[P6.9] SpawnAuthoritativePlayers done: considered={considered} spawned={spawned} skipped={skipped}");
        }

        // ════════════════════════════════════════════════════════════════
        //  In-process scripted-bot pipeline (SFGYM_BOT_SLOTS)
        //  Goal: 2 server-side bots fight each other with NO external client,
        //  using the stock SF spawn/move/revive/death flow 1:1.
        // ════════════════════════════════════════════════════════════════

        // Spawn each requested bot slot at the loaded map's real spawn point
        // (currentMapInfo.spawnPoints[slot]) — matching GameManager.StartMapSequence.
        private void AutoSpawnBots()
        {
            if (AutoSpawnBotSlots == null || AutoSpawnBotSlots.Count == 0) return;
            // Resolve map spawn points (Transform[] currentMapInfo.spawnPoints).
            Transform[] spawnPoints = null;
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType != null)
                {
                    var gm = UnityEngine.Object.FindObjectOfType(gmType);
                    if ((object)gm != null)
                    {
                        var cmiField = AccessTools.Field(gmType, "currentMapInfo");
                        var cmi = ((object)cmiField != null) ? cmiField.GetValue(gm) : null;
                        if ((object)cmi != null)
                        {
                            var spF = AccessTools.Field(cmi.GetType(), "spawnPoints");
                            if ((object)spF != null) spawnPoints = spF.GetValue(cmi) as Transform[];
                        }
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[bot-spawn] spawnPoint lookup threw: {e.Message}"); }
            if (spawnPoints == null || spawnPoints.Length == 0)
            {
                Log.LogWarning("[bot-spawn] No currentMapInfo.spawnPoints yet — map not loaded; aborting auto-spawn this tick.");
                return;
            }
            Log.LogInfo($"[bot-spawn] found {spawnPoints.Length} map spawn points.");

            int spawned = 0;
            foreach (var slot in AutoSpawnBotSlots)
            {
                if (SlotToRig.ContainsKey(slot))
                {
                    Log.LogInfo($"[bot-spawn] slot {slot} already has a rig, skipping.");
                    continue;
                }
                int spIdx = (slot >= 0 && slot < spawnPoints.Length) ? slot : 0;
                var sp = spawnPoints[spIdx];
                // Stock reads .localPosition (StartMapSequence). Spawn points are
                // children of MapInfo so local != world; mirror exactly.
                Vector3 pos = ((object)sp != null) ? sp.localPosition : new Vector3(0f, 3f, 0f);
                // Wrap each slot's full spawn sequence: some scenes (LevelEditor,
                // boss/minigame maps) lack a proper player/map setup and make
                // TrySpawnPlayer/ConfigureBotRig throw. One bad slot must not
                // abort the whole tick or leave Update with an uncaught NRE.
                try
                {
                    if (TrySpawnPlayer(slot, pos, out string err))
                    {
                        Log.LogInfo($"[bot-spawn] spawned bot rig for slot {slot} at map spawnPoint[{spIdx}]={pos}.");
                        ConfigureBotRig(slot);
                        RegisterRigWithControllerHandler(slot);
                        ReviveSpawnedBot(slot);
                        MoveBotToSpawnPoint(slot, pos);
                        spawned++;
                    }
                    else
                    {
                        Log.LogError($"[bot-spawn] FAILED to spawn slot {slot}: {err}");
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning($"[bot-spawn] slot {slot} spawn sequence threw ({e.GetType().Name}: {e.Message}) — likely an unsupported scene.");
                }
            }
            Log.LogInfo($"[bot-spawn] done — spawned {spawned}/{AutoSpawnBotSlots.Count} bots.");
            _botScriptedDriveActive = (spawned >= 2);
            // Self-heal: a proper 1v1 needs 2 bots. If the current scene can't
            // host them (bad/non-combat map), skip it by advancing the round to
            // a different scene instead of sitting idle forever.
            if (spawned < 2)
            {
                Log.LogWarning($"[bot-spawn] only {spawned} bot(s) spawned on this scene — advancing round to find a playable map.");
                // Tear down any partial rig so the next scene starts clean.
                try { ClearAuthoritativeRigsForRoundAdvance(); } catch { }
                ScheduleRoundAdvanceOnDeath("bot-spawn-insufficient");
            }
        }

        // 1:1 with GameManager.StartMapSequence: it calls the private MovePlayer
        // coroutine on the Hip rigidbody. We invoke that same coroutine via
        // reflection so all its side effects (kinematic toggle, DisarmPlayers,
        // SetMovementAbility.Reset, animated move, ReviveAllPlayers) run exactly
        // as stock — then flip kinematic back to dynamic afterward (stock only
        // does that for non-network matches; we're flagged network).
        private void MoveBotToSpawnPoint(int slot, Vector3 target)
        {
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
                var hipType = AccessTools.TypeByName("Hip");
                Rigidbody hipRb = null;
                if ((object)hipType != null)
                {
                    var hipComp = rig.GetComponentInChildren(hipType) as Component;
                    if ((object)hipComp != null) hipRb = hipComp.GetComponent<Rigidbody>();
                }
                if ((object)hipRb == null) { Log.LogWarning($"[bot-move] slot {slot}: no Hip rigidbody found."); return; }
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) { Log.LogWarning("[bot-move] no GameManager type"); return; }
                var gm = UnityEngine.Object.FindObjectOfType(gmType) as MonoBehaviour;
                if ((object)gm == null) { Log.LogWarning("[bot-move] no GameManager instance"); return; }
                var movePlayer = AccessTools.Method(gmType, "MovePlayer",
                    new System.Type[] { typeof(Rigidbody), typeof(Vector3) });
                if ((object)movePlayer == null) { Log.LogWarning("[bot-move] MovePlayer(Rigidbody,Vector3) reflection lookup failed."); return; }
                var enumerator = movePlayer.Invoke(gm, new object[] { hipRb, target }) as System.Collections.IEnumerator;
                if ((object)enumerator == null) { Log.LogWarning("[bot-move] MovePlayer returned null enumerator."); return; }
                gm.StartCoroutine(enumerator);
                _botKinematicFlipAt[slot] = Time.realtimeSinceStartup + 2.5f;
                Log.LogInfo($"[bot-move] slot {slot}: stock MovePlayer coroutine started toward {target}; kinematic flip scheduled in 2.5s.");
            }
            catch (Exception e) { Log.LogWarning($"[bot-move] slot {slot} threw: {e.Message}"); }
        }

        // Death detection for bots. In a network match HealthHandler.TakeDamage
        // routes the killing blow through NetworkPlayer.UnitWasDamaged (Steamworks
        // P2P) and only flips isDead on the *remote* receiver. Headless has no
        // peer + Steamworks is uninitialized, so calling stock Die() throws on the
        // broadcast before reaching KillPlayer. Replicate the LOCAL outcome of
        // Die() instead: flip isDead + drop weapon. The canonical auth-rig death
        // monitor (TickAuthRigDeathCheck) then schedules the round advance —
        // exactly the path a real client's relayed killing-blow takes.
        private void DetectAndApplyDeath()
        {
            try
            {
                var hhType = AccessTools.TypeByName("HealthHandler");
                var ciType = AccessTools.TypeByName("CharacterInformation");
                if ((object)hhType == null || (object)ciType == null) return;
                var hpF = AccessTools.Field(hhType, "health");
                var isDeadF = AccessTools.Field(ciType, "isDead");
                if ((object)hpF == null || (object)isDeadF == null) return;
                foreach (var slot in AutoSpawnBotSlots)
                {
                    if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) continue;
                    var hh = rig.GetComponentInChildren(hhType);
                    var ci = rig.GetComponentInChildren(ciType);
                    if ((object)hh == null || (object)ci == null) continue;
                    float hp = (float)hpF.GetValue(hh);
                    bool isDead = (bool)isDeadF.GetValue(ci);
                    if (hp <= 0f && !isDead)
                    {
                        isDeadF.SetValue(ci, true);
                        try
                        {
                            var fightingType = AccessTools.TypeByName("Fighting");
                            if ((object)fightingType != null)
                            {
                                var fighting = rig.GetComponentInChildren(fightingType);
                                if ((object)fighting != null)
                                {
                                    var drop = AccessTools.Method(fightingType, "DropWeapon", new System.Type[] { typeof(bool) });
                                    if ((object)drop != null) drop.Invoke(fighting, new object[] { true });
                                }
                            }
                        }
                        catch { }
                        Log.LogInfo($"[bot-death] slot {slot}: hp={hp:F1} → isDead set + weapon dropped (Steamworks-safe death).");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[bot-death] threw: {e.Message}"); }
        }

        private bool _botLastInFight;
        // Re-arm bots at the start of each new round. Stock StartMapSequence
        // (network-match path) re-runs MovePlayer on every player in
        // controllerHandler.players when a new map loads — but for IsNetworkMatch
        // it leaves the rigidbodies KINEMATIC (only the local-match path
        // un-kinematics them). Our bots ARE the local players in spirit, so we
        // restore the local-match behavior: when inFight transitions false→true
        // (countdown finished, new round live), reactivate + un-kinematic the
        // bot rigs so they can move and fight again. This is what makes the
        // round loop actually repeat.
        private void TickBotRoundRearm()
        {
            if (AutoSpawnBotSlots == null || AutoSpawnBotSlots.Count == 0) return;
            bool inFight = false;
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType != null)
                {
                    var f = AccessTools.Field(gmType, "inFight");
                    if ((object)f != null) inFight = (bool)f.GetValue(null);
                }
            }
            catch { return; }
            if (inFight && !_botLastInFight)
            {
                // false → true: new round just went live.
                foreach (var slot in AutoSpawnBotSlots)
                {
                    if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) continue;
                    try
                    {
                        if (!rig.activeSelf) rig.SetActive(true);
                        int n = 0;
                        foreach (var rb in rig.GetComponentsInChildren<Rigidbody>(true))
                        {
                            if ((object)rb == null) continue;
                            rb.isKinematic = false;
                            n++;
                        }
                        Log.LogInfo($"[bot-rearm] slot {slot}: new round live — reactivated + {n} rigidbodies set dynamic.");
                    }
                    catch (Exception e) { Log.LogWarning($"[bot-rearm] slot {slot}: {e.Message}"); }
                }
                _botScriptedDriveActive = true;
                // Reset combat-stall tracking for the new round.
                _botStallSumHp = -1f;
                _botStallLastChangeAt = Time.realtimeSinceStartup;
            }
            _botLastInFight = inFight;

            // Combat-stall timeout. The simple walk-at-target bot can't navigate
            // every map (gaps, dividers, ledges) and may never reach the
            // opponent → no death → the round would hang forever. If total HP
            // across both bots hasn't changed in _botStallSecs while inFight,
            // force a round advance so the fleet keeps cycling (acts like a
            // comp round timer / draw).
            if (inFight && _botScriptedDriveActive)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _botStallCheckAt >= 1.0f)
                {
                    _botStallCheckAt = now;
                    float sumHp = 0f; int counted = 0;
                    try
                    {
                        var hhType = AccessTools.TypeByName("HealthHandler");
                        var hpF = ((object)hhType != null) ? AccessTools.Field(hhType, "health") : null;
                        if ((object)hpF != null)
                        {
                            foreach (var slot in AutoSpawnBotSlots)
                            {
                                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) continue;
                                var hh = rig.GetComponentInChildren(hhType);
                                if ((object)hh == null) continue;
                                sumHp += (float)hpF.GetValue(hh); counted++;
                            }
                        }
                    }
                    catch { counted = 0; }
                    if (counted > 0)
                    {
                        if (_botStallSumHp < 0f || System.Math.Abs(sumHp - _botStallSumHp) > 0.01f)
                        {
                            _botStallSumHp = sumHp;
                            _botStallLastChangeAt = now;
                        }
                        else if (now - _botStallLastChangeAt > _botStallSecs)
                        {
                            Log.LogWarning($"[bot-stall] no HP change in {_botStallSecs:0}s (sumHp={sumHp:0.#}) — forcing round advance.");
                            _botStallLastChangeAt = now;           // debounce
                            _botStallSumHp = -1f;
                            ScheduleRoundAdvanceOnDeath("combat-stall-timeout");
                        }
                    }
                }
            }
        }
        private float _botStallSumHp = -1f;
        private float _botStallLastChangeAt;
        private float _botStallCheckAt;
        // Tunable via SF_BOT_STALL_SECS (default 30s). A real melee kill is well
        // under this; only genuine "can't reach each other" stalls hit it.
        private static float _botStallSecs = 30f;

        // Per-Update (~60fps render frames) bot driver: walk toward nearest other bot + periodic
        // swing, writing InputFrames into SlotInputs for InjectInputPrefix to
        // feed Movement.cs. Also runs death detection + the post-MovePlayer
        // kinematic flip.
        // Teacher-driver caches (reflection handles + 10Hz weapon scan).
        private static Type _tFightingT, _tWeaponPickUpT;
        private static FieldInfo _tWeaponF, _tCounterF, _tCantF;
        private static bool _tReflInit;
        private static readonly List<Vector3> _tWpCache = new List<Vector3>();
        private static int _tWpCacheTick = -999;
        private static float _tVoidZ = 17f;
        private void DriveScriptedBots()
        {
            if (!_botScriptedDriveActive || AutoSpawnBotSlots == null || AutoSpawnBotSlots.Count == 0) return;
            _botDriveTickCounter++;
            // Diagnostic heartbeat — fires every 180 ticks regardless of the
            // positions early-return below, so a stalled drive is visible.
            if (_botDriveTickCounter % 180 == 0)
            {
                int present = 0, active = 0;
                foreach (var s in AutoSpawnBotSlots)
                    if (SlotToRig.TryGetValue(s, out var r) && (object)r != null)
                    { present++; if (r.activeInHierarchy) active++; }
                Log.LogInfo($"[bot-hb] tick={_botDriveTickCounter} active={_botScriptedDriveActive} rigsPresent={present}/{AutoSpawnBotSlots.Count} activeInHierarchy={active}");
            }
            DetectAndApplyDeath();
            // Post-MovePlayer kinematic flip — see _botKinematicFlipAt notes.
            if (_botKinematicFlipAt.Count > 0)
            {
                List<int> dueSlots = null;
                float now = Time.realtimeSinceStartup;
                foreach (var kv in _botKinematicFlipAt)
                {
                    if (now < kv.Value) continue;
                    if (dueSlots == null) dueSlots = new List<int>();
                    dueSlots.Add(kv.Key);
                }
                if (dueSlots != null)
                {
                    foreach (var slot in dueSlots)
                    {
                        if (SlotToRig.TryGetValue(slot, out var rig) && (object)rig != null)
                        {
                            int n = 0;
                            foreach (var rb in rig.GetComponentsInChildren<Rigidbody>(true))
                            {
                                if ((object)rb == null) continue;
                                rb.isKinematic = false;
                                n++;
                            }
                            Log.LogInfo($"[bot-rig] Slot {slot}: post-MovePlayer kinematic flip — {n} rigidbodies set dynamic.");
                        }
                        _botKinematicFlipAt.Remove(slot);
                    }
                }
            }
            try
            {
                // Snapshot each bot's averaged body-part position (the root
                // transform of an SF character doesn't track the ragdoll).
                var positions = new Dictionary<int, Vector3>();
                foreach (var slot in AutoSpawnBotSlots)
                {
                    if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) continue;
                    Vector3 pos = rig.transform.position;
                    var rbs = rig.GetComponentsInChildren<Rigidbody>();
                    if (rbs != null && rbs.Length > 0)
                    {
                        Vector3 sum = Vector3.zero; int n = 0;
                        foreach (var rb in rbs) { if ((object)rb != null) { sum += rb.position; n++; } }
                        if (n > 0) pos = sum / n;
                    }
                    positions[slot] = pos;
                }
                if (positions.Count < 2) return;
                // ── Teacher driver (2026-06-11, BC demo collection) ──
                // Ports the TEACHABLE core of the mod's 13-subsystem ScriptedBot
                // (mod/StickFightGym/ScriptedBot) into the host: weapon fetching
                // (WeaponManager), engage-range keeping (EngageController), fire
                // timing, and void-edge veto (HazardAvoidance). Aim solving /
                // blocking / throws are deliberately absent — the RL student's
                // action space is MultiDiscrete(move,jump,fire) with env-side
                // auto-aim, so those skills are not expressible and the teacher
                // must act within the same envelope it is teaching.
                if (!_tReflInit)
                {
                    _tFightingT = AccessTools.TypeByName("Fighting");
                    _tWeaponPickUpT = AccessTools.TypeByName("WeaponPickUp");
                    _tWeaponF = ((object)_tFightingT != null) ? AccessTools.Field(_tFightingT, "weapon") : null;
                    _tCounterF = ((object)_tWeaponPickUpT != null) ? AccessTools.Field(_tWeaponPickUpT, "counter") : null;
                    _tCantF = ((object)_tWeaponPickUpT != null) ? AccessTools.Field(_tWeaponPickUpT, "cantBePickledUpFor") : null;
                    float vz0;
                    if (float.TryParse(Environment.GetEnvironmentVariable("SF_VOID_Z"), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out vz0) && vz0 > 1f)
                        _tVoidZ = vz0;
                    _tReflInit = true;
                }
                // Ground-weapon scan at 10Hz (shared across slots, same filters
                // as the snapshot "wps" block: settled + pickup-able).
                if (_botDriveTickCounter - _tWpCacheTick >= 5)
                {
                    _tWpCacheTick = _botDriveTickCounter;
                    _tWpCache.Clear();
                    try
                    {
                        if ((object)_tWeaponPickUpT != null)
                        {
                            var warr2 = UnityEngine.Object.FindObjectsOfType(_tWeaponPickUpT);
                            for (int i = 0; i < warr2.Length; i++)
                            {
                                var w = warr2[i] as Component;
                                if ((object)w == null) continue;
                                try
                                {
                                    if ((object)_tCounterF != null && (float)_tCounterF.GetValue(w) <= 0.3f) continue;
                                    if ((object)_tCantF != null && (float)_tCantF.GetValue(w) >= 0f) continue;
                                }
                                catch { }
                                _tWpCache.Add(w.transform.position);
                            }
                        }
                    }
                    catch { }
                }
                foreach (var slot in AutoSpawnBotSlots)
                {
                    if (!positions.TryGetValue(slot, out var me)) continue;
                    int targetSlot = -1; float bestD = float.MaxValue;
                    foreach (var kv in positions)
                    {
                        if (kv.Key == slot) continue;
                        float d = Vector3.Distance(me, kv.Value);
                        if (d < bestD) { bestD = d; targetSlot = kv.Key; }
                    }
                    if (targetSlot < 0) continue;
                    var target = positions[targetSlot];
                    float dz = target.z - me.z;
                    float dy = target.y - me.y;
                    float dist = Vector3.Distance(me, target);
                    // Skip RL-controlled slots: an external policy writes their
                    // SlotInputs via setBotAction; the scripted driver must not
                    // overwrite those each tick.
                    if (RlControlledSlots.Contains(slot)) continue;
                    // armed?
                    bool armed = false;
                    try
                    {
                        if (SlotToRig.TryGetValue(slot, out var rigA) && (object)rigA != null && (object)_tFightingT != null)
                        {
                            var fg = rigA.GetComponentInChildren(_tFightingT);
                            if ((object)fg != null && (object)_tWeaponF != null)
                                armed = (object)_tWeaponF.GetValue(fg) != null;
                        }
                    }
                    catch { }
                    // movement target: nearest gun when unarmed (WeaponManager),
                    // else the opponent at an engage band (EngageController).
                    float tdz = dz, tdy = dy;
                    float stopAt = armed ? 6.0f : 1.0f;   // armed: hold mid range
                    bool retreat = false;
                    if (!armed && _tWpCache.Count > 0)
                    {
                        float bw = float.MaxValue; Vector3 bwp = Vector3.zero; bool anyW = false;
                        for (int i = 0; i < _tWpCache.Count; i++)
                        {
                            // Skip weapons sitting in the edge band — chasing
                            // them is how the teacher slid into the void (~40%
                            // of episodes in the first collection).
                            if (Mathf.Abs(_tWpCache[i].z) > _tVoidZ - 4.0f) continue;
                            float d2 = Vector3.Distance(me, _tWpCache[i]);
                            if (d2 < bw) { bw = d2; bwp = _tWpCache[i]; anyW = true; }
                        }
                        if (anyW)
                        {
                            tdz = bwp.z - me.z; tdy = bwp.y - me.y;
                            stopAt = 0.2f;  // stand ON it — pickup is on contact
                        }
                    }
                    else if (armed && Mathf.Abs(dz) < 2.5f)
                    {
                        retreat = true;    // too close with a gun — back off
                    }
                    // SF z-axis is horizontal gameplay axis; +mx → -z.
                    float stickX;
                    if (retreat)
                        stickX = (dz > 0f) ? 1.0f : -1.0f;             // step away
                    else
                        stickX = (tdz > stopAt) ? -1.0f : (tdz < -stopAt ? 1.0f : 0.0f);
                    // Void veto (HazardAvoidance), two bands. On ice a stopped
                    // rig keeps sliding, so the old stop-only 2.5m veto still
                    // lost ~40% of episodes to the void. Soft band (4.5m): veto
                    // outward input. Hard band (3.0m): actively step INWARD
                    // regardless of intent — fights slide momentum. (+mx walks
                    // toward -z, so stickX>0 risks the -z edge.)
                    float hardM = _tVoidZ - 3.0f;
                    float softM = _tVoidZ - 4.5f;
                    if (me.z < -hardM) stickX = -1.0f;
                    else if (me.z > hardM) stickX = 1.0f;
                    else
                    {
                        if (stickX > 0f && me.z < -softM) stickX = 0f;
                        if (stickX < 0f && me.z > softM) stickX = 0f;
                    }
                    // jump: target clearly above, or cadence nudge when stuck on
                    // a lip while wanting to move.
                    bool jump = (tdy > 1.2f && Mathf.Abs(tdz) < 8.0f && (_botDriveTickCounter % 25) == 0)
                                || ((_botDriveTickCounter % 60) == 0 && tdy > 0.8f);
                    // fire (gun) when plausibly on target — pulsed 2-on/1-off so
                    // edge-triggered (semi-auto) weapons re-press WasPressed
                    // instead of being held forever; melee swing cadence when
                    // unarmed and adjacent (the fallback brawl).
                    bool fire;
                    if (armed)
                        fire = dist < 18f && Mathf.Abs(dy) < 7f && (_botDriveTickCounter % 3) != 0;
                    else
                        fire = (_botDriveTickCounter % 7) == 0 && Mathf.Abs(dz) < 2.5f;
                    if (_botDriveTickCounter % 180 == 0)
                        Log.LogInfo($"[bot-drive] slot{slot} me=({me.y:F1},{me.z:F1}) opp dz={dz:F2} dist={dist:F1} armed={armed} wps={_tWpCache.Count} stickX={stickX:F1} fire={fire}");
                    // aim at the opponent — same convention as the RL env's
                    // auto-aim (stock applies world aim z=-AimX, y=+AimY).
                    float mag = Mathf.Sqrt(dz * dz + dy * dy);
                    var inp = new InputFrame();
                    inp.StickX = stickX;
                    inp.StickY = 0f;
                    inp.AimX = (mag > 1e-3f) ? (-dz / mag) : 1.0f;
                    inp.AimY = (mag > 1e-3f) ? (dy / mag) : 0f;
                    inp.Buttons = (fire ? 2 : 0) | (jump ? 1 : 0);  // bit0=jump, bit1=fire
                    SlotInputs[slot] = inp;
                }
            }
            catch (Exception e) { Log.LogWarning($"[bot-drive] {e.Message}"); }
        }

        // Configure a bot rig as a fully-dynamic, input-driven player (unlike
        // ConfigureAuthoritativeRig's kinematic ghost). Mirrors what stock
        // CreatePlayer does (SetCollision(true), playerID) plus re-enabling
        // Standing/Fighting and disabling NSO broadcasters.
        private void ConfigureBotRig(int slot)
        {
            if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType != null)
                {
                    var ctrl = rig.GetComponent(ctrlType);
                    if ((object)ctrl != null)
                    {
                        var hasCtrlF = AccessTools.Field(ctrlType, "mHasControl");
                        if ((object)hasCtrlF != null)
                        {
                            hasCtrlF.SetValue(ctrl, true);
                            Log.LogInfo($"[bot-rig] Slot {slot}: Controller.mHasControl set true.");
                        }
                        // Stock CreatePlayer calls SetCollision(true) — without it
                        // body-part colliders are disabled and nothing can hit.
                        var setColl = AccessTools.Method(ctrlType, "SetCollision");
                        if ((object)setColl != null)
                        {
                            setColl.Invoke(ctrl, new object[] { true });
                            Log.LogInfo($"[bot-rig] Slot {slot}: SetCollision(true) invoked.");
                        }
                        var pidF = AccessTools.Field(ctrlType, "playerID");
                        if ((object)pidF != null) pidF.SetValue(ctrl, slot);
                    }
                }
                var standingType = AccessTools.TypeByName("Standing");
                if ((object)standingType != null)
                {
                    var standing = rig.GetComponentInChildren(standingType) as Behaviour;
                    if ((object)standing != null) { standing.enabled = false; standing.enabled = true; Log.LogInfo($"[bot-rig] Slot {slot}: Standing component re-enabled."); }
                }
                var fightingType = AccessTools.TypeByName("Fighting");
                if ((object)fightingType != null)
                {
                    var fighting = rig.GetComponentInChildren(fightingType) as Behaviour;
                    if ((object)fighting != null) { fighting.enabled = false; fighting.enabled = true; Log.LogInfo($"[bot-rig] Slot {slot}: Fighting component re-enabled."); }
                }
                var rbsDiag = rig.GetComponentsInChildren<Rigidbody>();
                int rbCount = 0, kinCount = 0, constraintCount = 0;
                foreach (var rb in rbsDiag)
                {
                    if ((object)rb == null) continue;
                    rbCount++;
                    if (rb.isKinematic) kinCount++;
                    if (rb.constraints != RigidbodyConstraints.None) constraintCount++;
                }
                Log.LogInfo($"[bot-rig] Slot {slot}: {rbCount} rigidbodies, {kinCount} kinematic, {constraintCount} with constraints");
                // Disable NSO broadcasters on the bot rig (it isn't a synced object).
                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType != null)
                {
                    foreach (var comp in rig.GetComponentsInChildren(nsoType))
                    {
                        var b = comp as Behaviour;
                        if ((object)b != null) b.enabled = false;
                    }
                }
                Log.LogInfo($"[bot-rig] Slot {slot}: dynamic-physics bot rig ready.");
            }
            catch (Exception e) { Log.LogWarning($"[bot-rig] slot {slot}: {e.Message}"); }
        }

        // Call GameManager.RevivePlayer(controller, true) — initializes
        // HealthHandler.health=100, isDead=false, adds to playersAlive.
        private void ReviveSpawnedBot(int slot)
        {
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
                var ctrlType = AccessTools.TypeByName("Controller");
                var ctrl = rig.GetComponent(ctrlType);
                if ((object)ctrl == null) { Log.LogWarning($"[bot-revive] slot {slot}: no Controller"); return; }
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) { Log.LogWarning("[bot-revive] no GameManager type"); return; }
                var instProp = AccessTools.Property(gmType, "Instance");
                object gm = ((object)instProp != null) ? instProp.GetValue(null, null) : null;
                if ((object)gm == null) gm = UnityEngine.Object.FindObjectOfType(gmType);
                if ((object)gm == null) { Log.LogWarning("[bot-revive] no GameManager instance"); return; }
                var revive = AccessTools.Method(gmType, "RevivePlayer", new System.Type[] { ctrlType, typeof(bool) });
                if ((object)revive == null) { Log.LogWarning("[bot-revive] RevivePlayer(Controller,bool) not found"); return; }
                revive.Invoke(gm, new object[] { ctrl, true });
                var hhType = AccessTools.TypeByName("HealthHandler");
                if ((object)hhType != null)
                {
                    var hh = rig.GetComponentInChildren(hhType);
                    if ((object)hh != null)
                    {
                        var hpF = AccessTools.Field(hhType, "health");
                        float hp = ((object)hpF != null) ? (float)hpF.GetValue(hh) : -1f;
                        Log.LogInfo($"[bot-revive] slot {slot} revived; HealthHandler.health={hp:F1}");
                    }
                    else Log.LogWarning($"[bot-revive] slot {slot} has no HealthHandler in children");
                }
            }
            catch (Exception e) { Log.LogWarning($"[bot-revive] slot {slot} threw: {e.Message}"); }
        }

        // Add the bot's Controller to ControllerHandler's player list (field is
        // named 'mPlayers' in this build; fall back to any List<Controller>),
        // then RevivePlayer it. Required so StartMapSequence relocates + revives
        // the bot on subsequent rounds.
        private void RegisterRigWithControllerHandler(int slot)
        {
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) { Log.LogWarning($"[bot-spawn] reg slot {slot}: no rig in SlotToRig"); return; }
                var chType = AccessTools.TypeByName("ControllerHandler");
                if ((object)chType == null) { Log.LogWarning($"[bot-spawn] reg slot {slot}: ControllerHandler type not found"); return; }
                var chInst = UnityEngine.Object.FindObjectOfType(chType);
                if ((object)chInst == null) { Log.LogWarning($"[bot-spawn] reg slot {slot}: no ControllerHandler instance in any scene"); return; }
                var playersField = AccessTools.Field(chType, "players");
                if ((object)playersField == null)
                {
                    var allF = chType.GetFields(System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic);
                    foreach (var fi in allF)
                    {
                        if (!fi.FieldType.IsGenericType) continue;
                        var args = fi.FieldType.GetGenericArguments();
                        if (args.Length == 1 && args[0].Name == "Controller")
                        {
                            playersField = fi;
                            Log.LogInfo($"[bot-spawn] reg slot {slot}: using fallback field '{fi.Name}'.");
                            break;
                        }
                    }
                    if ((object)playersField == null) { Log.LogWarning($"[bot-spawn] reg slot {slot}: no List<Controller> field found"); return; }
                }
                var list = playersField.GetValue(chInst);
                if ((object)list == null) { Log.LogWarning($"[bot-spawn] reg slot {slot}: players list null"); return; }
                var ctrlType = AccessTools.TypeByName("Controller");
                var ctrl = rig.GetComponent(ctrlType);
                if ((object)ctrl == null) { Log.LogWarning($"[bot-spawn] reg slot {slot}: rig has no Controller component"); return; }
                var addMethod = list.GetType().GetMethod("Add");
                if ((object)addMethod == null) return;
                var containsMethod = list.GetType().GetMethod("Contains");
                if ((object)containsMethod != null && (bool)containsMethod.Invoke(list, new object[] { ctrl }))
                {
                    Log.LogInfo($"[bot-spawn] slot {slot} already in ControllerHandler.players");
                    return;
                }
                addMethod.Invoke(list, new object[] { ctrl });
                Log.LogInfo($"[bot-spawn] registered slot {slot} controller with ControllerHandler.players");
                // Stock CreatePlayer finishes with gameManager.RevivePlayer(ctrl).
                try
                {
                    var gmField = AccessTools.Field(chType, "gameManager");
                    object gmInst = ((object)gmField != null) ? gmField.GetValue(chInst) : null;
                    if ((object)gmInst == null)
                    {
                        var gmType = AccessTools.TypeByName("GameManager");
                        if ((object)gmType != null) gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                    }
                    if ((object)gmInst != null)
                    {
                        var reviveMethod = AccessTools.Method(gmInst.GetType(), "RevivePlayer", new System.Type[] { ctrlType, typeof(bool) });
                        if ((object)reviveMethod != null)
                        {
                            reviveMethod.Invoke(gmInst, new object[] { ctrl, true });
                            Log.LogInfo($"[bot-spawn] RevivePlayer invoked for slot {slot}.");
                        }
                        else Log.LogWarning("[bot-spawn] RevivePlayer(Controller,bool) method not found.");
                    }
                    else Log.LogWarning("[bot-spawn] could not locate GameManager instance for RevivePlayer.");
                }
                catch (Exception re) { Log.LogWarning($"[bot-spawn] RevivePlayer({slot}) threw: {re.Message}"); }
            }
            catch (Exception e) { Log.LogWarning($"[bot-spawn] RegisterRigWithControllerHandler({slot}) threw: {e.Message}"); }
        }

        // Configure a freshly-spawned rig as the server's authoritative copy
        // of that player. Per-instance HasControl=true on the Controller so
        // SF's host-side gates (destructible piece OnCollisionEnter, etc.)
        // accept this rig as a legitimate authority source.
        //
        // Also configure as a "physics ghost" — kinematic body parts that
        // get teleported to client position each PlayerUpdate. The ghost
        // pushes NSOs (boxes/crates) via kinematic sweep so box collisions
        // happen server-side, then NSO snapshots broadcast box positions
        // back to all clients. NSO components on the rig itself are
        // disabled so they don't broadcast wrong indices.
        //
        // This is "mirror rig 2.0" — real NetworkPlayer with per-instance
        // HasControl, behaving as a ghost until v26 PlayerInput properly
        // drives Movement.cs. The transition will be: when inputs are
        // verified flowing reliably, un-kinematic the root and the rig
        // becomes input-driven (no more position-from-client mirror).
        private void ConfigureAuthoritativeRig(int slot)
        {
            if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType != null)
                {
                    var ctrl = rig.GetComponent(ctrlType);
                    if ((object)ctrl != null)
                    {
                        var hasCtrlF = AccessTools.Field(ctrlType, "mHasControl");
                        if ((object)hasCtrlF != null)
                        {
                            hasCtrlF.SetValue(ctrl, true);
                            Log.LogInfo($"[P6.9] Slot {slot}: Controller.mHasControl set true (per-instance).");
                        }
                    }
                }

                // Make all body part rigidbodies kinematic — no gravity, no
                // Movement-driven forces, just position-driven sweeps.
                var rbs = rig.GetComponentsInChildren<Rigidbody>();
                int kinSet = 0;
                foreach (var rb in rbs)
                {
                    if ((object)rb == null) continue;
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    kinSet++;
                }

                // Disable NSO components on the rig — they'd otherwise
                // broadcast ObjectUpdate with whatever Index the rig parts
                // carry, potentially corrupting scene-object indices on
                // clients.
                int nsoOff = 0;
                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType != null)
                {
                    var nsos = rig.GetComponentsInChildren(nsoType);
                    foreach (var nso in nsos)
                    {
                        var beh = nso as Behaviour;
                        if ((object)beh != null) { beh.enabled = false; nsoOff++; }
                    }
                }
                Log.LogInfo($"[P6.9 ghost] Slot {slot}: {kinSet} rbs kinematic, {nsoOff} NSO components disabled.");
            }
            catch (Exception e) { Log.LogWarning($"[P6.9 ConfigureAuthoritativeRig] {e.Message}"); }
        }

        // Phase 6.9 ghost mode — teleport the auth rig to client position
        // each PlayerUpdate. With body kinematic, Rigidbody.MovePosition does
        // a swept collision check which can push dynamic rigidbodies (boxes/
        // crates) it overlaps. The auth rig is invisible to clients (we
        // don't broadcast ClientSpawned for it) so the player only sees
        // their own player + others; the ghost is a server-side physical
        // body for collision purposes only.
        private static int _ghostMoveLogCount;
        private static int _ghostWakeLogCount;
        private static Type _wakeDpType;
        private static FieldInfo _wakeDpSimpleField;
        private static FieldInfo _wakeDpEventField;

        private void UpdateGhostRigPosition(int slot, Vector3 target)
        {
            if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            var rootPos = rig.transform.position;
            var delta = target - rootPos;
            if (delta.sqrMagnitude < 0.0001f) return;
            // BOXES FIX v3: large jumps (first update from spawn-point to client's
            // real position, or scene transition) use direct rb.position writes
            // so we DON'T sweep through box stacks and knock them off platforms.
            // Subsequent small deltas use MovePosition's swept collision so the
            // rig CAN push boxes as it walks into them.
            bool teleport = delta.magnitude > 5f;
            rig.transform.position = target;
            var rbs = rig.GetComponentsInChildren<Rigidbody>();
            foreach (var rb in rbs)
            {
                if ((object)rb == null) continue;
                if (teleport)
                {
                    rb.position = rb.position + delta;
                    if (!rb.isKinematic) rb.velocity = Vector3.zero;
                }
                else if (rb.isKinematic)
                    rb.MovePosition(rb.position + delta);
                else
                    rb.position += delta;
            }
            if (!teleport)
                WakeNsosNearGhostSweep(rootPos, target);
            if (_ghostMoveLogCount < 5 || _ghostMoveLogCount % 600 == 0)
                Log.LogInfo($"[P6.9 ghost] slot={slot} moved to {target} (delta={delta.magnitude:0.00} {(teleport?"TELEPORT":"sweep")})");
            _ghostMoveLogCount++;
        }

        // After ghost-rig sweep, wake pushable map NSOs so CollectActiveNsoSnapshot
        // sees dynamic motion and v26 clients get box positions (not ghost-through).
        private void WakeNsosNearGhostSweep(Vector3 sweepFrom, Vector3 sweepTo)
        {
            try
            {
                float dist = Vector3.Distance(sweepFrom, sweepTo);
                if (dist < 0.05f) return;
                Vector3 mid = (sweepFrom + sweepTo) * 0.5f;
                float radius = dist * 0.5f + 1.25f;
                var hits = Physics.OverlapSphere(mid, radius);
                if (hits == null || hits.Length == 0) return;

                EnsureNsoTypeCache();
                if ((object)_nsoType == null) return;

                int woken = 0;
                var seen = new HashSet<Component>();
                foreach (var col in hits)
                {
                    if ((object)col == null) continue;
                    var nsoComp = col.GetComponentInParent(_nsoType) as Component;
                    if ((object)nsoComp == null || !seen.Add(nsoComp)) continue;
                    if (IsChainStyleDestructibleRoot(nsoComp.gameObject)) continue;

                    ushort id = GetNsoIndex(nsoComp);
                    var nsoRbs = nsoComp.GetComponentsInChildren<Rigidbody>();
                    foreach (var rb in nsoRbs)
                    {
                        if ((object)rb == null) continue;
                        if (rb.isKinematic)
                        {
                            rb.isKinematic = false;
                            rb.WakeUp();
                            woken++;
                        }
                    }
                    var p = nsoComp.transform.position;
                    _nsoLastBroadcastPos[id] = p;
                    _nsoLastMovedAt[id] = Time.realtimeSinceStartup;
                }
                if (woken > 0 && (_ghostWakeLogCount < 8 || _ghostWakeLogCount % 120 == 0))
                    Log.LogInfo($"[BOXES] Ghost sweep woke {woken} crate RB(s) near ({mid.x:0.0},{mid.y:0.0},{mid.z:0.0}) r={radius:0.00}");
                if (woken > 0) _ghostWakeLogCount++;
            }
            catch (Exception ex) { Log.LogWarning($"[BOXES] WakeNsosNearGhostSweep: {ex.Message}"); }
        }

        private void EnsureNsoTypeCache()
        {
            if ((object)_nsoType != null) return;
            _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
            if ((object)_nsoType != null)
            {
                _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
            }
        }

        private ushort GetNsoIndex(Component nsoComp)
        {
            ushort id = 0;
            if ((object)_nsoIndexProp != null)
                id = (ushort)_nsoIndexProp.GetValue(nsoComp, null);
            else if ((object)_nsoIndexField != null)
                id = (ushort)_nsoIndexField.GetValue(nsoComp);
            return id;
        }

        private bool IsChainStyleDestructibleRoot(GameObject root)
        {
            if ((object)_wakeDpType == null)
            {
                _wakeDpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_wakeDpType != null)
                {
                    _wakeDpSimpleField = AccessTools.Field(_wakeDpType, "simpleDestruction");
                    _wakeDpEventField = AccessTools.Field(_wakeDpType, "eventDestruction");
                }
            }
            if ((object)_wakeDpType == null) return false;
            var dps = root.GetComponentsInChildren(_wakeDpType);
            if (dps == null || dps.Length == 0) return false;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                bool simple = (object)_wakeDpSimpleField != null && (bool)_wakeDpSimpleField.GetValue(dp);
                bool ev = (object)_wakeDpEventField != null && (bool)_wakeDpEventField.GetValue(dp);
                if (!simple && !ev) return true;
            }
            return false;
        }

        /// <summary>Simple-destruction crate (not chain/ice pillar).</summary>
        private bool IsPushableCrateNso(GameObject root)
        {
            if (IsChainStyleDestructibleRoot(root) || IsWeaponNsoRoot(root)) return false;
            if ((object)_wakeDpType == null)
            {
                _wakeDpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_wakeDpType != null)
                {
                    _wakeDpSimpleField = AccessTools.Field(_wakeDpType, "simpleDestruction");
                    _wakeDpEventField = AccessTools.Field(_wakeDpType, "eventDestruction");
                }
            }
            if ((object)_wakeDpType == null) return false;
            var dps = root.GetComponentsInChildren(_wakeDpType);
            if (dps == null || dps.Length == 0) return false;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                bool simple = (object)_wakeDpSimpleField != null && (bool)_wakeDpSimpleField.GetValue(dp);
                bool ev = (object)_wakeDpEventField != null && (bool)_wakeDpEventField.GetValue(dp);
                if (simple && !ev) return true;
            }
            return false;
        }

        private static Type _weaponPickUpType;
        private bool IsWeaponNsoRoot(GameObject root)
        {
            if ((object)root == null) return false;
            if ((object)_weaponPickUpType == null)
            {
                try { _weaponPickUpType = AccessTools.TypeByName("WeaponPickUp"); } catch { }
            }
            return (object)_weaponPickUpType != null
                && root.GetComponentInChildren(_weaponPickUpType, true) != null;
        }

        // P0-16 — spawn positions captured post-settle; used to reset fallthrough.
        private readonly Dictionary<ushort, Vector3> _nsoSpawnPos = new Dictionary<ushort, Vector3>();
        private float _nsoFallGuardNextAt = -1f;
        private float _nsoPeriodicKeyframeNextAt = -1f;
        private float _sceneLoadRealtime = -1f;
        private int _nsoFallthroughResetCount;
        private readonly Dictionary<ushort, int> _nsoVoidResetCount = new Dictionary<ushort, int>();
        private const float NsoFallResetY = -32f;
        private const int NsoFallMaxResetPerTick = 16;
        // After this many void rescues, the crate clearly has no floor under
        // its spawn (e.g. lobby storage objects high above the map). Freeze it
        // kinematic at spawn so it stops churning the snapshot every tick.
        private const int NsoVoidFreezeAfter = 3;
        private const float NsoPeriodicKeyframeSec = 2f;

        private void TickNsoFallGuard()
        {
            if (_nsoFallGuardNextAt < 0f) _nsoFallGuardNextAt = Time.realtimeSinceStartup + 2f;
            if (Time.realtimeSinceStartup < _nsoFallGuardNextAt) return;
            _nsoFallGuardNextAt = Time.realtimeSinceStartup + 1.0f;
            if (_nsoSpawnPos.Count == 0) return;
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                float now = Time.realtimeSinceStartup;
                int resetsThisTick = 0;
                foreach (var kv in _nsoSpawnPos)
                {
                    ushort id = kv.Key;
                    if (!_nsoByIndexCache.TryGetValue(id, out var comp) || (object)comp == null)
                    {
                        if (_nsoCacheLastRebuildAt < 0f || now - _nsoCacheLastRebuildAt > 2f)
                        {
                            RebuildNsoIndexCache();
                            _nsoCacheLastRebuildAt = now;
                        }
                        if (!_nsoByIndexCache.TryGetValue(id, out comp) || (object)comp == null)
                            continue;
                    }
                    if (!IsPushableCrateNso(comp.gameObject)) continue;
                    var p = comp.transform.position;
                    // Only act on crates that have left the playable area. A crate
                    // below the void threshold is never in legitimate play (no
                    // player push or throw arc lives down there) — it tunneled the
                    // floor due to server physics. The previous version SKIPPED
                    // these: a falling crate is "recently moved" every frame and
                    // has fast downward velocity, so the old recent-push +
                    // downward-velocity guards fired on exactly the crates we
                    // needed to rescue, and the guard never did anything.
                    if (p.y >= NsoFallResetY) continue;
                    if (resetsThisTick >= NsoFallMaxResetPerTick) break;

                    Vector3 spawn = kv.Value;
                    int voidCount = _nsoVoidResetCount.TryGetValue(id, out var vc) ? vc + 1 : 1;
                    _nsoVoidResetCount[id] = voidCount;

                    // Restore to the on-map spawn the crate had after settle and
                    // keep it DYNAMIC so it behaves like vanilla (pushable, stacks,
                    // can be knocked around). A real map crate has a floor under
                    // its spawn, so it lands and stays — no churn.
                    comp.transform.position = spawn;
                    comp.transform.rotation = Quaternion.identity;
                    bool freeze = voidCount > NsoVoidFreezeAfter;
                    var rbs = comp.GetComponentsInChildren<Rigidbody>();
                    foreach (var rb in rbs)
                    {
                        if ((object)rb == null) continue;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        // Only objects with NO floor under their spawn keep
                        // re-falling. After several rescues, stop the per-second
                        // teleport churn by parking them kinematic — these are
                        // never gameplay crates (e.g. lobby storage at y~70), so
                        // this never freezes a real map box.
                        rb.isKinematic = freeze;
                    }
                    _nsoLastBroadcastPos[id] = spawn;
                    _nsoLastMovedAt[id] = now;
                    _nsoFallthroughResetCount++;
                    resetsThisTick++;
                    if (_nsoFallthroughResetCount <= 5 || _nsoFallthroughResetCount % 20 == 0)
                        Log.LogInfo($"[BOXES] Reset fallthrough idx={id} Y={p.y:0.1} -> spawn ({spawn.y:0.1}) freeze={freeze} (#{_nsoFallthroughResetCount})");
                }
            }
            catch (Exception e) { Log.LogWarning($"[P0-16 fall guard] {e.Message}"); }
        }

        private bool TryGetNsoWorldPosition(ushort idx, out Vector3 pos)
        {
            pos = default;
            if (_nsoByIndexCache.Count == 0 || Time.realtimeSinceStartup - _nsoCacheLastRebuildAt > 5f)
            {
                RebuildNsoIndexCache();
                _nsoCacheLastRebuildAt = Time.realtimeSinceStartup;
            }
            if (!_nsoByIndexCache.TryGetValue(idx, out var comp) || (object)comp == null)
                return false;
            pos = comp.transform.position;
            return true;
        }

        private bool ShouldSkipServerOriginatedDestruction(byte[] data, int len)
        {
            if (data == null || len < 2) return true;
            ushort idx = (ushort)(data[0] | (data[1] << 8));
            if (!TryGetNsoWorldPosition(idx, out var pos))
                return true;
            if (pos.y < -30f) return true;
            if (_sceneLoadRealtime > 0f
                && (Time.realtimeSinceStartup - _sceneLoadRealtime) < 5f
                && TryGetNsoRoot(idx, out var root)
                && IsChainStyleDestructibleRoot(root))
                return true;
            return false;
        }

        private bool TryGetNsoRoot(ushort idx, out GameObject root)
        {
            root = null;
            if (!_nsoByIndexCache.TryGetValue(idx, out var comp) || (object)comp == null)
                return false;
            root = comp.gameObject;
            return true;
        }

        // === Phase 6.10 — server-authoritative snapshots ===
        // 30Hz broadcast of the oracle's view of every authoritative player rig's
        // position. The wire format is intentionally simple for now — Phase 6.11
        // will ship the client-side reconciliation plugin that consumes these,
        // and Phase 6.12 adds the playerInput inbound side + tighter packing
        // (compressed int16 + delta encoding + the lastInputSeq field that drives
        // reconciliation rollback).
        //
        // Body (v26 draft):
        //   u32 serverTick (LE)
        //   u8  playerCount
        //   for each player:
        //     u8  slot
        //     f32 posX (LE)
        //     f32 posY (LE)
        //     f32 posZ (LE)
        //
        // Stock clients ignore msgType 39 (their MsgType enum stops at 38) so
        // this is wire-safe to broadcast even before the client plugin lands.
        // Port the v26 client plugin (SFClientRecon) binds on. Hardcoded for
        // now; env-var override comes when we add multi-client-on-same-host
        // testing (e.g. two SF instances on the dev machine).
        private const int V26_CLIENT_PORT = 1339;
        private float _lastSnapshotAt = -1f;
        // Authoritative-state broadcast rate. Bumped 30→60Hz so server-driven
        // NSOs (boxes) update twice as often → far smoother on clients (combined
        // with client-side velocity extrapolation). Physics already runs 60Hz.
        private const float SnapshotHz = 30f;
        private uint  _serverTick;

        // === Phase 6.17 v0.1 — server-side projectile registry ===
        // When a client fires (Weapon.ActuallyShoot on their side), the
        // SFClientRecon plugin emits PktClientFireWeapon (41). We register
        // a virtual projectile, advance it each frame, expire after a
        // configured lifetime, and broadcast positions in the snapshot.
        // Hit registration is v0.2 — for now this is observability +
        // visual consistency (all clients see the same bullet trajectory).
        private class Projectile
        {
            public uint     Id;
            public byte     OwnerSlot;
            public byte     WeaponType;     // 0=generic, 1=pistol, … (TBD)
            public Vector3  Position;
            public Vector3  Velocity;
            public float    BornAt;
            public float    LifetimeSec;    // max time of flight before expire
        }
        private readonly List<Projectile> _projectiles = new List<Projectile>();
        private uint _nextProjId = 1;
        private const float DefaultProjectileSpeed = 60f;     // SF-units/s for pistol
        private const float DefaultProjectileLifetime = 3f;   // 3s before expire

        // PktClientFireWeapon body (v26.3 — client → server):
        //   u8  ownerSlot
        //   u8  weaponType  (passthrough byte; meaning is whatever the client sends)
        //   f32 originX     (world position of muzzle)
        //   f32 originY
        //   f32 originZ
        //   f32 dirX        (normalized direction)
        //   f32 dirY
        //   f32 dirZ
        //   f32 speed       (units/sec — 0 → use DefaultProjectileSpeed)
        // Total: 2 + 24 + 4 = 30 bytes.
        private void HandleClientFireWeapon(byte[] data, int off, int len, IPEndPoint from)
        {
            if (len < 30) return;
            byte ownerSlot  = data[off];
            byte weaponType = data[off + 1];
            float ox = BitConverter.ToSingle(data, off + 2);
            float oy = BitConverter.ToSingle(data, off + 6);
            float oz = BitConverter.ToSingle(data, off + 10);
            float dx = BitConverter.ToSingle(data, off + 14);
            float dy = BitConverter.ToSingle(data, off + 18);
            float dz = BitConverter.ToSingle(data, off + 22);
            float speed = BitConverter.ToSingle(data, off + 26);
            if (ownerSlot > 3) { Log.LogWarning($"[P6.17] Fire reject — bad slot {ownerSlot}"); return; }
            if (speed <= 0f || float.IsNaN(speed) || float.IsInfinity(speed)) speed = DefaultProjectileSpeed;
            var dir = new Vector3(dx, dy, dz);
            if (dir.sqrMagnitude < 0.01f) { Log.LogWarning($"[P6.17] Fire reject — zero/NaN direction"); return; }
            dir.Normalize();
            var p = new Projectile
            {
                Id          = _nextProjId++,
                OwnerSlot   = ownerSlot,
                WeaponType  = weaponType,
                Position    = new Vector3(ox, oy, oz),
                Velocity    = dir * speed,
                BornAt      = Time.realtimeSinceStartup,
                LifetimeSec = DefaultProjectileLifetime,
            };
            _projectiles.Add(p);
            Log.LogInfo($"[P6.17] Fire registered: id={p.Id} slot={ownerSlot} w={weaponType} pos={p.Position} vel={p.Velocity.magnitude:0.0}u/s");
        }

        // Advance every live projectile each frame. Expires by age.
        // Hit registration is v0.2: when projectile passes within ~1u of a
        // player rig (excluding owner), emit a PktPlayerTookDamage.
        private void TickProjectiles()
        {
            if (_projectiles.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            float dt = Time.deltaTime;
            for (int i = _projectiles.Count - 1; i >= 0; i--)
            {
                var p = _projectiles[i];
                if (now - p.BornAt > p.LifetimeSec)
                {
                    _projectiles.RemoveAt(i);
                    continue;
                }
                // Phase 6.17 v0.2 — server-side hit registration. Advance,
                // then test the new position against every active rig
                // (excluding the owner). Sphere-sphere ~1.2u radius — coarse
                // but matches the lazy hit-feel of stock SF reasonably well
                // and is much cheaper than per-bone raycast. Emit server-
                // authoritative PktPlayerTookDamage on hit; the relay
                // path validates + fans out to all clients.
                //
                // v0.3 — wall occlusion via Physics.Linecast from prev to
                // new position. If the line is intersected by a collider
                // whose root isn't a player rig (Controller component),
                // expire the projectile silently (bullet hit a wall). If
                // first hit IS a player, fall through to the sphere check
                // so the existing hit emit applies.
                Vector3 prev = p.Position;
                p.Position += p.Velocity * dt;
                if (TryProjectileWallHit(prev, p.Position, out var wallHit))
                {
                    if (IsExplosiveWeaponType(p.WeaponType, p.Velocity.magnitude))
                        ApplyExplosiveBlastAt(wallHit, 5f, 900f);
                    _projectiles.RemoveAt(i);
                    continue;
                }
                int hitSlot = TestProjectileHit(p, prev);
                if (hitSlot >= 0)
                {
                    EmitServerDamage(hitSlot, p.OwnerSlot, p.WeaponType, p.Velocity);
                    _projectiles.RemoveAt(i);
                }
            }
        }

        // v0.3 — wall occlusion. Linecast from prev to new along the segment
        // the projectile traveled this tick. If the first hit collider
        // belongs to scene geometry (no Controller component in its root),
        // it's a wall — the bullet expires without damage. Player rigs are
        // intentionally excluded because TestProjectileHit handles them
        // (and SF's player rigs span many bones; a raycast might hit a
        // hand collider while the sphere check finds the torso).
        private static bool IsExplosiveWeaponType(byte weaponType, float speed)
        {
            return speed < 50f || weaponType == 5 || weaponType == 6 || weaponType == 7 || weaponType == 8;
        }

        // P6.17 — server-side explosion physics. Applies AddExplosionForce to
        // nearby dynamic rigidbodies + calls DestructiblePiece.Collide on
        // destructibles in radius.
        //
        // Two bugs fixed 2026-05-24 (see notes/bug-investigations/2026-05-24_OPEN-3_chains_break_root_cause.md):
        //
        // 1. Chains/ice were being randomly destroyed by any nearby explosion.
        //    Vanilla SF chains have forceThreshold=0 (any non-zero force breaks
        //    them) and ice has forceThreshold=15 (this method's effective force
        //    of 15*10=150 trivially exceeds it). The blanket OverlapSphere +
        //    blanket Collide() invocation triggered destruction on every
        //    chain/ice in range. Vanilla bullets that DON'T break chains/ice
        //    are raycasts that hit specific targets — they don't blanket-blast.
        //    Filter added below to skip vanilla-fragile destructibles.
        //
        // 2. networkForce=true was being passed, which makes Collide bypass
        //    its network branch (SendDestructMessage). Destruction was applied
        //    locally on the server only, and the destruction event was never
        //    sent to clients — clients only saw the break via the subsequent
        //    NSO position-sync (ice falls below world, position lerps down).
        //    networkForce=false now lets vanilla broadcast the destruction
        //    event properly, so all clients see the same break at the same time.
        //
        // 3. Added LoS check via Physics.Linecast — explosions should not
        //    blast through walls.
        private void ApplyExplosiveBlastAt(Vector3 center, float radius, float blastForce)
        {
            try
            {
                var cols = Physics.OverlapSphere(center, radius);
                if (cols == null) return;
                var dpType = AccessTools.TypeByName("DestructiblePiece");
                var collideM = (object)dpType != null ? AccessTools.Method(dpType, "Collide") : null;
                var fThreshF = (object)dpType != null ? AccessTools.Field(dpType, "forceThreshold") : null;
                var simpleF  = (object)dpType != null ? AccessTools.Field(dpType, "simpleDestruction") : null;
                var eventF   = (object)dpType != null ? AccessTools.Field(dpType, "eventDestruction") : null;
                int affected = 0, skippedChain = 0, skippedIce = 0, skippedLoS = 0;
                foreach (var col in cols)
                {
                    if ((object)col == null) continue;

                    // Always apply explosion impulse (visual feedback for any dynamic body)
                    var rb = col.attachedRigidbody;
                    if ((object)rb != null && !rb.isKinematic)
                        rb.AddExplosionForce(blastForce, center, radius, 0.5f);

                    // For destructibles: filter before calling Collide
                    if ((object)collideM == null || (object)dpType == null) continue;
                    var dp = col.GetComponent(dpType) ?? col.GetComponentInParent(dpType);
                    if ((object)dp == null) continue;

                    // Skip vanilla-fragile destructibles: chains (simpleDestruction with threshold≈0)
                    // and ice (both flags false). Vanilla bullets break these via direct raycast
                    // hit calls; blanket explosion damage is OUR bug, not vanilla behavior.
                    bool simple = (object)simpleF  != null && (bool)simpleF.GetValue(dp);
                    bool eventD = (object)eventF   != null && (bool)eventF.GetValue(dp);
                    float thresh = (object)fThreshF != null ? (float)fThreshF.GetValue(dp) : 0f;

                    if (simple && thresh < 0.01f) { skippedChain++; continue; }   // chains
                    if (!simple && !eventD)       { skippedIce++;   continue; }   // ice

                    // LoS check: don't blast through walls
                    Vector3 dpPos = ((Component)dp).transform.position;
                    if (Physics.Linecast(center, dpPos, out var hit) && hit.collider != col &&
                        (hit.transform.root != ((Component)dp).transform.root))
                    {
                        skippedLoS++; continue;
                    }

                    // Pass networkForce=false so SendDestructMessage broadcasts to all clients.
                    // Previously this was `true` which suppressed the network destruction event.
                    collideM.Invoke(dp, new object[] { Vector3.up * 15f, 10f, false });
                    affected++;
                }
                if (affected > 0 || skippedChain > 0 || skippedIce > 0 || skippedLoS > 0)
                    Log.LogInfo($"[P6.17] Explosion at {center} r={radius}: affected={affected} skipChain={skippedChain} skipIce={skippedIce} skipLoS={skippedLoS}");
            }
            catch (Exception e) { Log.LogWarning($"[P6.17 explosion] {e.Message}"); }
        }

        private bool ProjectileHitWall(Vector3 from, Vector3 to) =>
            TryProjectileWallHit(from, to, out _);

        private bool TryProjectileWallHit(Vector3 from, Vector3 to, out Vector3 hitPoint)
        {
            hitPoint = to;
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.001f) return false;
            if (Physics.Linecast(from, to, out var hit))
            {
                if ((object)hit.collider == null) return false;
                hitPoint = hit.point;
                var root = hit.collider.transform.root;
                if ((object)root == null) return false;
                if (root.GetComponent("Controller") != null) return false;
                return true;
            }
            return false;
        }

        // Returns the slot of the first rig (other than the owner) whose
        // position is within HitRadiusSq of the projectile's new position
        // OR whose segment-from-prev-to-new-position passes within
        // HitRadius of the rig (swept sphere check). -1 if none.
        private const float ProjectileHitRadius   = 1.2f;
        private const float ProjectileHitRadiusSq = ProjectileHitRadius * ProjectileHitRadius;
        private int TestProjectileHit(Projectile p, Vector3 prevPos)
        {
            foreach (var kv in SlotToRig)
            {
                if (kv.Key == p.OwnerSlot) continue;
                var rig = kv.Value;
                if ((object)rig == null) continue;
                Vector3 rigPos = rig.transform.position;
                // Cheap end-point sphere check first.
                if ((rigPos - p.Position).sqrMagnitude <= ProjectileHitRadiusSq) return kv.Key;
                // Swept: closest point on segment prev→new to rigPos.
                Vector3 seg = p.Position - prevPos;
                float segLenSq = seg.sqrMagnitude;
                if (segLenSq < 0.0001f) continue;
                float t = Mathf.Clamp01(Vector3.Dot(rigPos - prevPos, seg) / segLenSq);
                Vector3 closest = prevPos + seg * t;
                if ((rigPos - closest).sqrMagnitude <= ProjectileHitRadiusSq) return kv.Key;
            }
            return -1;
        }

        // Build a PktPlayerTookDamage body and broadcast it as if it had come
        // from the victim's own client. Standard 25 damage + dmgType=0
        // tracks vanilla pistol behavior. weaponType byte is reserved for
        // when we differentiate pistol/sniper/etc.; logged but not used yet.
        //
        // v0.3 — particle direction included. Body format (from
        // NetworkPlayer.SyncClienthealth parser at line 649):
        //   byte attackerIdx          (1)
        //   f32  damage               (4)
        //   bool playParticles        (1)
        //   f32  particleDir.y        (4)  if playParticles
        //   f32  particleDir.z        (4)  if playParticles
        //   byte dmgType              (1)
        // Total = 15 bytes with particles. Client renders the spray
        // direction from particleDir; we use the projectile's velocity
        // direction so it sprays backward from the hit point.
        private void EmitServerDamage(int victimSlot, byte attackerSlot, byte weaponType, Vector3 projVelocity)
        {
            byte[] body = new byte[15];
            int off = 0;
            body[off++] = attackerSlot;
            byte[] dmgBytes = BitConverter.GetBytes(25.0f);
            Buffer.BlockCopy(dmgBytes, 0, body, off, 4); off += 4;
            body[off++] = 1;  // playParticles=true
            // particleDir.y / .z — the receiver uses Quaternion.LookRotation
            // on Vector3(0, y, z). Pointing along the projectile velocity
            // means the particle system orients along the hit direction.
            // Normalize so magnitude doesn't affect particle behavior.
            Vector3 dir = projVelocity.sqrMagnitude > 0.0001f ? projVelocity.normalized : Vector3.forward;
            byte[] yBytes = BitConverter.GetBytes(dir.y);
            byte[] zBytes = BitConverter.GetBytes(dir.z);
            Buffer.BlockCopy(yBytes, 0, body, off, 4); off += 4;
            Buffer.BlockCopy(zBytes, 0, body, off, 4); off += 4;
            body[off++] = 0;  // dmgType=0 generic
            byte channel = (byte)(victimSlot * 2 + 3);
            BroadcastSfPacket(PktPlayerTookDamage, body, 0uL, channel);
            Log.LogInfo($"[P6.17v3] Server hit: attacker={attackerSlot} victim={victimSlot} w={weaponType} dir=({dir.y:0.00},{dir.z:0.00}) → 25 dmg on chan={channel}");
        }

        // Phase 6.14.5 — tick-history ring buffer for lag-comp.
        // Records per-slot positions at each server tick so a future damage-
        // event handler can rewind to validate. We just RECORD here; the
        // VALIDATE step needs the damage packet to carry a tick reference,
        // which requires a patched-DLL extension (not yet shipped). Until
        // then the buffer feeds telemetry only — but having it built means
        // when we add `clientLastAckedServerTick` to the damage protocol,
        // validation is a 30-line addition. See notes/phase6/13-rewind-buffer.md.
        private class TickSample
        {
            public uint Tick;
            public Vector3[] Positions = new Vector3[4];
            public bool[]    Alive     = new bool[4];
        }
        private readonly Queue<TickSample> _tickHistory = new Queue<TickSample>(64);
        private const int MaxHistoryTicks = 60;  // ~2s at 30Hz snapshot

        private void RecordTickSample()
        {
            var s = new TickSample { Tick = _serverTick };
            foreach (var kv in SlotToRig)
            {
                if (kv.Key < 0 || kv.Key > 3) continue;
                var rig = kv.Value;
                if ((object)rig == null) continue;
                s.Positions[kv.Key] = rig.transform.position;
                s.Alive[kv.Key]     = true;
            }
            _tickHistory.Enqueue(s);
            while (_tickHistory.Count > MaxHistoryTicks) _tickHistory.Dequeue();
        }

        // Lookup positions at a given server tick. Returns null if tick is
        // outside the buffer window.
        private TickSample LookupTickSample(uint tick)
        {
            foreach (var s in _tickHistory) if (s.Tick == tick) return s;
            return null;
        }
        private void TickWorldStateSnapshot()
        {
            if (!_matchStarted) return;
            if (_sfClients.Count == 0) return;
            if (Time.realtimeSinceStartup - _lastSnapshotAt < (1.0f / SnapshotHz)) return;
            _lastSnapshotAt = Time.realtimeSinceStartup;
            _serverTick++;
            RecordTickSample();        // Phase 6.14.5 — history before broadcast
            BroadcastWorldStateSnapshot();
        }

        // Cached NSO Index field — looked up lazily once a scene has NSOs.
        private static FieldInfo _nsoIndexField;
        private static System.Reflection.PropertyInfo _nsoIndexProp;
        private static Type _nsoType;

        private void BroadcastWorldStateSnapshot()
        {
            try
            {
                int n = 0;
                foreach (var kv in SlotToRig) if ((object)kv.Value != null) n++;

                // Phase 6.14 — also pack NSO positions for server-determined
                // box / chain / ice-debris falling. Only include NSOs whose
                // rigidbody is non-kinematic (i.e. currently allowed to move).
                // Pre-placed static crates/chains stay kinematic until struck
                // and don't need bandwidth.
                bool periodicKeyframe = Time.realtimeSinceStartup >= _nsoPeriodicKeyframeNextAt;
                if (periodicKeyframe)
                    _nsoPeriodicKeyframeNextAt = Time.realtimeSinceStartup + NsoPeriodicKeyframeSec;
                var nsoEntries = periodicKeyframe
                    ? CollectAllNsoSnapshot()
                    : CollectActiveNsoSnapshot();

                // P0-14 — also pack MapInfoSyncableBase positions (moving
                // platforms, pressure pillars, ghost platforms) so the
                // oracle is authoritative for these too. Without this they
                // drift independently on each client.
                var mapSyncEntries = CollectMapSyncSnapshot();
                var mapStateEntries = CollectMapStateSnapshot();
                LogMapSyncDiagnostics(mapSyncEntries.Count, mapStateEntries.Count);

                if (n == 0 && nsoEntries.Count == 0 && mapSyncEntries.Count == 0 && mapStateEntries.Count == 0) return;

                // Body layout v26.6 (was v26.5):
                //   u32 serverTick
                //   u8  playerCount
                //   players: [u8 slot, f32 x, f32 y, f32 z, u32 lastInputSeq] × n  (17/each)
                //   u16 nsoCount
                //   NSOs:    [u16 id, f32 x, f32 y, f32 z, f32 rotZ]         × m  (18/each)
                //   u16 projCount                                                  (added v26.3)
                //   projs:   [u32 id, u8 slot, u8 wType, f32 x, f32 y, f32 z] × k  (18/each)
                //   u16 mapSyncCount (v26.5 positions)
                //   u16 mapStateCount (v26.6 GetData payloads — GhostPlatform isOn, etc.)
                int bodyLen = 4 + 1 + n * 17 + 2 + nsoEntries.Count * 18 + 2 + _projectiles.Count * 18
                              + 2 + mapSyncEntries.Count * 20 + MapStateSectionByteLen(mapStateEntries);
                byte[] body = new byte[bodyLen];
                int off = 0;
                WriteU32LE(body, off, _serverTick); off += 4;
                body[off++] = (byte)n;
                // Build a slot → LastInputSeq lookup once instead of an
                // O(n) scan of _sfClients per player (was O(n²) overall).
                var slotSeq = new Dictionary<int, uint>(_sfClients.Count);
                foreach (var ckv in _sfClients) if (ckv.Value.Slot >= 0) slotSeq[ckv.Value.Slot] = ckv.Value.LastInputSeq;
                foreach (var kv in SlotToRig)
                {
                    var rig = kv.Value;
                    if ((object)rig == null) continue;
                    body[off++] = (byte)kv.Key;
                    Vector3 p = rig.transform.position;
                    WriteF32LE(body, off, p.x); off += 4;
                    WriteF32LE(body, off, p.y); off += 4;
                    WriteF32LE(body, off, p.z); off += 4;
                    uint lastSeq = 0;
                    slotSeq.TryGetValue(kv.Key, out lastSeq);
                    WriteU32LE(body, off, lastSeq); off += 4;
                }
                WriteU16LE(body, off, (ushort)nsoEntries.Count); off += 2;
                foreach (var e in nsoEntries)
                {
                    WriteU16LE(body, off, e.Id); off += 2;
                    WriteF32LE(body, off, e.X); off += 4;
                    WriteF32LE(body, off, e.Y); off += 4;
                    WriteF32LE(body, off, e.Z); off += 4;
                    WriteF32LE(body, off, e.RotZ); off += 4;
                }
                // Phase 6.17 — projectile entries.
                WriteU16LE(body, off, (ushort)_projectiles.Count); off += 2;
                foreach (var p in _projectiles)
                {
                    WriteU32LE(body, off, p.Id); off += 4;
                    body[off++] = p.OwnerSlot;
                    body[off++] = p.WeaponType;
                    WriteF32LE(body, off, p.Position.x); off += 4;
                    WriteF32LE(body, off, p.Position.y); off += 4;
                    WriteF32LE(body, off, p.Position.z); off += 4;
                }
                // P0-14 — MapInfoSyncableBase entries (v26.5 section).
                WriteU16LE(body, off, (ushort)mapSyncEntries.Count); off += 2;
                foreach (var m in mapSyncEntries)
                {
                    WriteF32LE(body, off, m.StartX); off += 4;
                    WriteF32LE(body, off, m.StartY); off += 4;
                    WriteF32LE(body, off, m.X); off += 4;
                    WriteF32LE(body, off, m.Y); off += 4;
                    WriteF32LE(body, off, m.Z); off += 4;
                }
                off = WriteMapStateSection(body, off, mapStateEntries);

                // Broadcast to ALL spawned clients on their v26 endpoint. Once
                // a client has sent a PlayerInput packet we know its actual
                // v26 source addr (recorded in _slotV26Endpoint); before that
                // we fall back to clientIP:V26_CLIENT_PORT. Lets two clients
                // on the same machine use different v26 ports without colliding.
                foreach (var kv in _sfClients)
                {
                    if (!kv.Value.Initialized) continue;
                    IPEndPoint v26Ep;
                    if (!_slotV26Endpoint.TryGetValue(kv.Value.Slot, out v26Ep))
                        v26Ep = new IPEndPoint(kv.Value.Addr.Address, V26_CLIENT_PORT);
                    SendSfPacket(v26Ep, PktWorldStateSnapshot, body, 0, 0);
                }
                if (_serverTick == 1 || _serverTick % 90 == 0)
                    Log.LogInfo($"[P6.10/14/v26.6] Snapshot tick={_serverTick} players={n} nsos={nsoEntries.Count} mapSync={mapSyncEntries.Count} mapState={mapStateEntries.Count} fallResets={_nsoFallthroughResetCount} keyframe={periodicKeyframe} bytes={bodyLen}");
            }
            catch (Exception e) { Log.LogWarning($"[P6.10/14] {e.Message}"); }
        }

        private struct NsoSnap { public ushort Id; public float X, Y, Z, RotZ; }

        // P0-14 — MapInfoSyncableBase position snapshot entry.
        // Identified by Vector2 startPos (same key stock SF uses in its
        // mMapDataObjectToSync dictionary). We can't use transform.GetInstanceID()
        // because Unity assigns those per-process — server's IDs never
        // match client's. With P0-12 active, both sides quantize the
        // startPos to 0.01 precision so the Vector2 keys ARE stable
        // cross-process.
        private struct MapSyncSnap { public float StartX, StartY, X, Y, Z; }

        // P0-13 — full-keyframe variant of CollectActiveNsoSnapshot that
        // includes every NSO regardless of position-delta / activity. Used
        // exactly once per new v26 endpoint so newly-joining clients learn
        // the current resting position of at-rest NSOs. Still respects the
        // Y > -30 filter (don't ship killbox-fallen NSOs).
        private List<NsoSnap> CollectAllNsoSnapshot()
        {
            var result = new List<NsoSnap>();
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return result;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                if (all == null) return result;
                foreach (var nso in all)
                {
                    var comp = nso as Component;
                    if ((object)comp == null) continue;
                    if (IsWeaponNsoRoot(comp.gameObject)) continue;
                    ushort id = 0;
                    if ((object)_nsoIndexProp != null) id = (ushort)_nsoIndexProp.GetValue(nso, null);
                    else if ((object)_nsoIndexField != null) id = (ushort)_nsoIndexField.GetValue(nso);
                    var p = comp.transform.position;
                    if (p.y < -30f) continue;
                    var e = comp.transform.eulerAngles;
                    result.Add(new NsoSnap { Id = id, X = p.x, Y = p.y, Z = p.z, RotZ = e.z });
                }
            }
            catch (Exception ex) { Log.LogWarning($"[P0-13 keyframe collect] {ex.Message}"); }
            return result;
        }

        // P0-13 — build a v26 snapshot containing all current players +
        // every NSO and send it to a single endpoint. Wire format is
        // identical to BroadcastWorldStateSnapshot so existing client
        // parsers handle it without changes.
        private void SendKeyframeSnapshotToEndpoint(IPEndPoint target)
        {
            int n = 0;
            foreach (var kv in SlotToRig) if ((object)kv.Value != null) n++;
            var nsoEntries = CollectAllNsoSnapshot();
            var mapSyncEntries = CollectMapSyncSnapshot();
            var mapStateEntries = CollectMapStateSnapshot();
            int bodyLen = 4 + 1 + n * 17 + 2 + nsoEntries.Count * 18 + 2 + _projectiles.Count * 18
                          + 2 + mapSyncEntries.Count * 20 + MapStateSectionByteLen(mapStateEntries);
            byte[] body = new byte[bodyLen];
            int off = 0;
            WriteU32LE(body, off, _serverTick); off += 4;
            body[off++] = (byte)n;
            var slotSeq = new Dictionary<int, uint>(_sfClients.Count);
            foreach (var ckv in _sfClients) if (ckv.Value.Slot >= 0) slotSeq[ckv.Value.Slot] = ckv.Value.LastInputSeq;
            foreach (var kv in SlotToRig)
            {
                var rig = kv.Value;
                if ((object)rig == null) continue;
                body[off++] = (byte)kv.Key;
                Vector3 p = rig.transform.position;
                WriteF32LE(body, off, p.x); off += 4;
                WriteF32LE(body, off, p.y); off += 4;
                WriteF32LE(body, off, p.z); off += 4;
                uint lastSeq = 0;
                slotSeq.TryGetValue(kv.Key, out lastSeq);
                WriteU32LE(body, off, lastSeq); off += 4;
            }
            WriteU16LE(body, off, (ushort)nsoEntries.Count); off += 2;
            foreach (var e in nsoEntries)
            {
                WriteU16LE(body, off, e.Id); off += 2;
                WriteF32LE(body, off, e.X); off += 4;
                WriteF32LE(body, off, e.Y); off += 4;
                WriteF32LE(body, off, e.Z); off += 4;
                WriteF32LE(body, off, e.RotZ); off += 4;
            }
            WriteU16LE(body, off, (ushort)_projectiles.Count); off += 2;
            foreach (var p in _projectiles)
            {
                WriteU32LE(body, off, p.Id); off += 4;
                body[off++] = p.OwnerSlot;
                body[off++] = p.WeaponType;
                WriteF32LE(body, off, p.Position.x); off += 4;
                WriteF32LE(body, off, p.Position.y); off += 4;
                WriteF32LE(body, off, p.Position.z); off += 4;
            }
            WriteU16LE(body, off, (ushort)mapSyncEntries.Count); off += 2;
            foreach (var m in mapSyncEntries)
            {
                WriteF32LE(body, off, m.StartX); off += 4;
                WriteF32LE(body, off, m.StartY); off += 4;
                WriteF32LE(body, off, m.X); off += 4;
                WriteF32LE(body, off, m.Y); off += 4;
                WriteF32LE(body, off, m.Z); off += 4;
            }
            off = WriteMapStateSection(body, off, mapStateEntries);
            SendSfPacket(target, PktWorldStateSnapshot, body, 0, 0);
            Log.LogInfo($"[P0-13/v26.6] Sent keyframe snapshot to {target} — players={n} nsos={nsoEntries.Count} mapSync={mapSyncEntries.Count} mapState={mapStateEntries.Count} bytes={bodyLen}");
        }

        // Apply an incoming PktObjectUpdate (msgType 26) to the server's
        // local NSO scene state. Body layout (from
        // NetworkSyncableObject.SendNewObjectStatePackage decompile):
        //   u16 Index
        //   i16 PosY/100         (corresponds to Unity world Y)
        //   i16 PosZ/100         (corresponds to Unity world Z)
        //   i16 RotY/100         (unused here)
        //   i16 RotZ/100         (rotation about forward, applied as eulerZ)
        // Without this, every client's box positions diverged because the
        // server didn't know boxes had moved → its v26 snapshot broadcast
        // the boxes' spawn positions, snapping clients back.
        private readonly Dictionary<ushort, Component> _nsoByIndexCache = new Dictionary<ushort, Component>();
        private float _nsoCacheLastRebuildAt = -1f;
        private int _objectUpdateAppliedCount;
        private void ApplyClientObjectUpdate(byte[] data, int off, int len)
        {
            if (len < 10) return;
            ushort idx = (ushort)(data[off] | (data[off + 1] << 8));
            short rawY = (short)(data[off + 2] | (data[off + 3] << 8));
            short rawZ = (short)(data[off + 4] | (data[off + 5] << 8));
            short rawRotZ = (short)(data[off + 8] | (data[off + 9] << 8));
            float py    = rawY / 100f;
            float pz    = rawZ / 100f;
            float rotZ  = rawRotZ / 100f;

            // Rebuild the index→Component cache periodically. NSO Indexes
            // get re-assigned on every scene load, so a stale cache after
            // a map change would point at destroyed objects.
            float now = Time.realtimeSinceStartup;
            if (_nsoCacheLastRebuildAt < 0f || now - _nsoCacheLastRebuildAt > 5f || _nsoByIndexCache.Count == 0)
            {
                RebuildNsoIndexCache();
                _nsoCacheLastRebuildAt = now;
            }
            if (!_nsoByIndexCache.TryGetValue(idx, out var comp) || (object)comp == null)
                return;
            Vector3 pos = new Vector3(0f, py, pz);
            Quaternion rot = Quaternion.Euler(0f, 0f, rotZ);
            var rootRb = comp.GetComponent<Rigidbody>();
            if (RefOk(rootRb) && !rootRb.isKinematic)
            {
                rootRb.position = pos;
                rootRb.rotation = rot;
                rootRb.WakeUp();
            }
            else
            {
                comp.transform.position = pos;
                comp.transform.rotation = rot;
            }
            // Mark as recently-moved so CollectActiveNsoSnapshot will
            // include this NSO in subsequent broadcasts even after the
            // client stops sending updates.
            _nsoLastBroadcastPos[idx] = comp.transform.position;
            _nsoLastMovedAt[idx]      = now;
            _objectUpdateAppliedCount++;
            if (_objectUpdateAppliedCount == 1 || _objectUpdateAppliedCount % 60 == 0)
                Log.LogInfo($"[BOXES] Applied client ObjectUpdate #{_objectUpdateAppliedCount} idx={idx} → ({py:0.0},{pz:0.0})");
        }

        // Server-side per-NSO cache entry. Classification (pushable/weapon) and
        // the Rigidbody ref are computed ONCE here instead of per-tick in
        // CollectActiveNsoSnapshot — which previously did FindObjectsOfType +
        // 2× GetComponentsInChildren per NSO every snapshot. On box-heavy maps
        // (~90 crates) that tanked server FPS → snapshots slowed → boxes lagged
        // on clients, worse the more crates a map had.
        private class NsoSrvEntry { public ushort Id; public Component Comp; public Rigidbody Rb; public bool Pushable; public bool Weapon; }
        private readonly List<NsoSrvEntry> _nsoSrvEntries = new List<NsoSrvEntry>();

        private void RebuildNsoIndexCache()
        {
            _nsoByIndexCache.Clear();
            _nsoSrvEntries.Clear();
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                if (all == null) return;
                foreach (var nso in all)
                {
                    ushort id = 0;
                    if ((object)_nsoIndexProp != null)
                        id = (ushort)_nsoIndexProp.GetValue(nso, null);
                    else if ((object)_nsoIndexField != null)
                        id = (ushort)_nsoIndexField.GetValue(nso);
                    var c = nso as Component;
                    _nsoByIndexCache[id] = c;
                    if ((object)c == null) continue;
                    var go = c.gameObject;
                    _nsoSrvEntries.Add(new NsoSrvEntry
                    {
                        Id = id,
                        Comp = c,
                        Rb = c.GetComponent<Rigidbody>(),
                        Weapon = IsWeaponNsoRoot(go),
                        Pushable = IsPushableCrateNso(go)
                    });
                }
            }
            catch (Exception ex) { Log.LogWarning($"[BOXES NSO cache] {ex.Message}"); }
        }

        // Ensure the NSO cache is fresh enough for per-tick iteration.
        private void EnsureNsoSrvCache()
        {
            if (_nsoSrvEntries.Count == 0 || _nsoCacheLastRebuildAt < 0f
                || Time.realtimeSinceStartup - _nsoCacheLastRebuildAt > 2f)
            {
                RebuildNsoIndexCache();
                _nsoCacheLastRebuildAt = Time.realtimeSinceStartup;
            }
        }

        // Gather NSOs that need broadcasting this tick. Three include cases:
        //   1. Non-kinematic NSO with current velocity (boxes being pushed,
        //      crates falling).
        //   2. Kinematic NSO whose position changed since last snapshot
        //      (moving platforms, animator-driven kinematic bodies — these
        //      have isKinematic=true but their transform is being driven by
        //      Animator/script).
        //   3. NSO that was in case 1 or 2 within the last ~1s ("keepalive"
        //      so smoothing on the client catches the final settle frame
        //      after motion stops, not a stale snap from 33ms ago).
        //
        // Static crates that haven't moved skip — saves bandwidth.
        private readonly Dictionary<ushort, Vector3> _nsoLastBroadcastPos = new Dictionary<ushort, Vector3>();
        private readonly Dictionary<ushort, float>   _nsoLastMovedAt      = new Dictionary<ushort, float>();
        private const float NsoPosDeltaThreshold = 0.01f;   // ~1 cm
        private const float NsoKeepaliveSec      = 3.0f;
        private const float NsoCrateKeepaliveSec = 25.0f;

        private List<NsoSnap> CollectActiveNsoSnapshot()
        {
            var result = new List<NsoSnap>();
            try
            {
                EnsureNsoSrvCache();
                float now = Time.realtimeSinceStartup;
                bool needRebuild = false;
                foreach (var ent in _nsoSrvEntries)
                {
                    var comp = ent.Comp;
                    if (!comp) { needRebuild = true; continue; }
                    if (ent.Weapon) continue;

                    ushort id = ent.Id;
                    Vector3 p;
                    try { p = comp.transform.position; }
                    catch { needRebuild = true; continue; }

                    var rb = ent.Rb;
                    if (p.y < -30f) continue;

                    bool dynamicBody = rb && !rb.isKinematic;
                    if (dynamicBody && ent.Pushable)
                    {
                        _nsoLastMovedAt[id] = now;
                        _nsoLastBroadcastPos[id] = p;
                        var eDyn = comp.transform.eulerAngles;
                        result.Add(new NsoSnap { Id = id, X = p.x, Y = p.y, Z = p.z, RotZ = eDyn.z });
                        continue;
                    }

                    bool dynamicMoving = false;
                    if (dynamicBody)
                    {
                        try
                        {
                            dynamicMoving = rb.velocity.sqrMagnitude > 0.0001f
                                || rb.angularVelocity.sqrMagnitude > 0.0001f;
                        }
                        catch { needRebuild = true; continue; }
                    }

                    bool positionDrifted = !_nsoLastBroadcastPos.TryGetValue(id, out var lastPos)
                        || Vector3.Distance(p, lastPos) > NsoPosDeltaThreshold;

                    float keepAlive = ent.Pushable ? NsoCrateKeepaliveSec : NsoKeepaliveSec;
                    bool recentlyActive = _nsoLastMovedAt.TryGetValue(id, out var lastMovedAt)
                        && (now - lastMovedAt) < keepAlive;

                    if (!dynamicMoving && !positionDrifted && !recentlyActive) continue;

                    if (dynamicMoving || positionDrifted) _nsoLastMovedAt[id] = now;
                    _nsoLastBroadcastPos[id] = p;

                    var e = comp.transform.eulerAngles;
                    result.Add(new NsoSnap { Id = id, X = p.x, Y = p.y, Z = p.z, RotZ = e.z });
                }
                if (needRebuild)
                {
                    RebuildNsoIndexCache();
                    _nsoCacheLastRebuildAt = Time.realtimeSinceStartup;
                }
            }
            catch (Exception ex) { Log.LogWarning($"[P6.14 NSO collect] {ex.GetType().Name}: {ex.Message}"); }
            return result;
        }

        // Freeze NSO rigidbodies that fell out of the playable area.
        // Stock SF's host kills crates that cross the killbox (Y<-50);
        // we don't have that cleanup, so falling crates accelerate
        // forever, eventually slamming into destructibles (chains, ice)
        // and breaking them with no player input.
        // Fix: periodically scan all NSOs in scene; any with Y < -25
        // gets isKinematic=true. Stops the fall + the broadcast spam.
        private float _nsoFreezerNextAt = -1f;
        private void TickStaleNsoFreezer()
        {
            if (_nsoFreezerNextAt < 0f) _nsoFreezerNextAt = Time.realtimeSinceStartup + 5f;
            if (Time.realtimeSinceStartup < _nsoFreezerNextAt) return;
            _nsoFreezerNextAt = Time.realtimeSinceStartup + 3f;
            try
            {
                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType == null) return;
                var nsos = UnityEngine.Object.FindObjectsOfType(nsoType);
                if (nsos == null) return;
                int frozen = 0;
                foreach (var o in nsos)
                {
                    var comp = o as Component;
                    if ((object)comp == null) continue;
                    Vector3 pos = comp.transform.position;
                    if (pos.y > -25f) continue;
                    // Crates the fall-guard tracks (real map boxes with a known
                    // spawn) are rescued back to spawn and kept dynamic — the
                    // freezer must not steal them and park them kinematic in the
                    // void, or they'd never come back. Only freeze NSOs the
                    // fall-guard doesn't own (untracked runaway/non-gameplay).
                    if (_nsoSpawnPos.Count > 0)
                    {
                        ushort fid = GetNsoIndex(comp);
                        if (_nsoSpawnPos.ContainsKey(fid)) continue;
                    }
                    // Below playable area — freeze all its rigidbodies.
                    var rbs = comp.GetComponentsInChildren<Rigidbody>();
                    foreach (var rb in rbs)
                    {
                        if ((object)rb == null) continue;
                        if (!rb.isKinematic)
                        {
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                            rb.isKinematic = true;
                            frozen++;
                        }
                    }
                }
                if (frozen > 0)
                    Log.LogInfo($"[P6.7] Froze {frozen} runaway-fall rigidbodies (Y < -25).");
            }
            catch (Exception e) { Log.LogWarning($"[P6.7 freezer] {e.Message}"); }
        }

        // === helpers ===

        private int AllocSlot(SfClient cli)
        {
            if (cli.Slot >= 0) return cli.Slot;
            for (int s = 0; s < 4; s++)
            {
                bool taken = false;
                foreach (var kv in _sfClients) if (kv.Value.Slot == s) { taken = true; break; }
                if (!taken) return s;
            }
            return 0; // overflow — should reject in real impl
        }

        // Serialize a packet with the 14-byte wrapper and send to one client.
        private void SendSfPacket(IPEndPoint to, byte msgType, byte[] body, ulong steamID, byte channel)
        {
            if ((object)_sfServer == null) return;
            int totalLen = 5 + (body?.Length ?? 0) + 9;
            byte[] pkt = new byte[totalLen];
            uint ts = (uint)(System.DateTime.UtcNow - new System.DateTime(1970, 1, 1)).TotalSeconds;
            WriteU32LE(pkt, 0, ts);
            pkt[4] = msgType;
            if (body != null && body.Length > 0) System.Buffer.BlockCopy(body, 0, pkt, 5, body.Length);
            int tailOff = 5 + (body?.Length ?? 0);
            WriteU64LE(pkt, tailOff, steamID);
            pkt[tailOff + 8] = channel;
            try
            {
                _sfServer.Send(pkt, pkt.Length, to);
                _sfPacketsTx++;
            }
            catch (Exception e) { if (Verbose) Log.LogDebug($"[SF] send: {e.Message}"); }
        }

        private void BroadcastSfPacket(byte msgType, byte[] body, ulong steamID, byte channel)
        {
            foreach (var kv in _sfClients)
                SendSfPacket(kv.Value.Addr, msgType, body, steamID, channel);
        }

        // === codec primitives ===

        private static void WriteU32LE(byte[] buf, int off, uint v)
        {
            buf[off    ] = (byte)(v       & 0xFF);
            buf[off + 1] = (byte)(v >>  8 & 0xFF);
            buf[off + 2] = (byte)(v >> 16 & 0xFF);
            buf[off + 3] = (byte)(v >> 24 & 0xFF);
        }

        private static void WriteF32LE(byte[] buf, int off, float v)
        {
            // BitConverter.GetBytes is little-endian on x86/x64. We target
            // x64 Linux/Windows for the oracle; if we ever support PowerPC
            // or BE clients this needs an endian guard.
            var bytes = System.BitConverter.GetBytes(v);
            buf[off    ] = bytes[0];
            buf[off + 1] = bytes[1];
            buf[off + 2] = bytes[2];
            buf[off + 3] = bytes[3];
        }

        private static void WriteU16LE(byte[] buf, int off, ushort v)
        {
            buf[off    ] = (byte)(v       & 0xFF);
            buf[off + 1] = (byte)(v >>  8 & 0xFF);
        }
        private static void WriteU64LE(byte[] buf, int off, ulong v)
        {
            for (int i = 0; i < 8; i++) buf[off + i] = (byte)((v >> (i * 8)) & 0xFF);
        }
        private static ulong ReadU64LE(byte[] buf, int off)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= ((ulong)buf[off + i]) << (i * 8);
            return v;
        }
        private static float ReadF32LE(byte[] buf, int off)
        {
            return System.BitConverter.ToSingle(buf, off);
        }

        private void StartBridge()
        {
            try
            {
                // Loopback-only: bridge commands (loadMap/teleport/addForce/...)
                // mutate gameplay state with no auth. Co-located Go server is on
                // the same host, so 0.0.0.0 exposure is gratuitous network risk.
                _bridge = new UdpClient(new IPEndPoint(IPAddress.Loopback, BridgePort));
                _bridge.Client.Blocking = false;
                Log.LogInfo($"Bridge: listening on UDP 127.0.0.1:{BridgePort}.");
            }
            catch (Exception e)
            {
                Log.LogError($"Bridge: bind on 127.0.0.1:{BridgePort} failed: {e.Message}");
                _bridge = null;
            }
        }

        private void DrainBridgeCommands()
        {
            if ((object)_bridge == null) return;
            int processed = 0;
            while (processed++ < 16) // cap per frame
            {
                byte[] data;
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    if (_bridge.Available <= 0) return;
                    data = _bridge.Receive(ref remote);
                }
                catch (SocketException)
                {
                    return; // would-block / nothing to read
                }
                catch (Exception e)
                {
                    if (Verbose) Log.LogDebug($"Bridge recv: {e.Message}");
                    return;
                }
                _bridgePeer = remote;
                HandleBridgeCommand(data, remote);
            }
        }

        private void HandleBridgeCommand(byte[] data, IPEndPoint from)
        {
            string body = Encoding.UTF8.GetString(data);
            if (Verbose) Log.LogDebug($"Bridge ← {from}: {body}");
            // Tiny ad-hoc JSON parser — body shapes are trivial so we don't need a full lib.
            string cmd = ExtractStringField(body, "cmd");
            if (cmd == "ping")
            {
                SendBridgeJson(from, $"{{\"reply\":\"pong\",\"tick\":{_bridgeTick},\"scene\":\"{SceneManager.GetActiveScene().name}\"}}");
            }
            else if (cmd == "snapshot")
            {
                EmitStateSnapshotTo(from);
            }
            else if (cmd == "setBotAction")
            {
                // RL action injection. Body: {cmd:"setBotAction", slot:N,
                // mx:f, my:f, aimx:f, aimy:f, buttons:i}. Writes SlotInputs[slot]
                // which InjectInputPrefix feeds to Movement.cs. Only effective
                // for slots listed in SFGYM_RL_SLOTS (DriveScriptedBots skips
                // writing those, so it won't fight the injected action).
                int slot = ExtractIntField(body, "slot", -1);
                if (slot < 0 || slot > 3)
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"setBotAction\",\"ok\":false,\"err\":\"bad slot\"}");
                }
                else
                {
                    var inp = new InputFrame();
                    inp.StickX = ExtractFloatField(body, "mx");
                    inp.StickY = ExtractFloatField(body, "my");
                    inp.AimX = HasField(body, "aimx") ? ExtractFloatField(body, "aimx") : inp.StickX;
                    inp.AimY = HasField(body, "aimy") ? ExtractFloatField(body, "aimy") : 0f;
                    inp.Buttons = ExtractIntField(body, "buttons", 0);
                    SlotInputs[slot] = inp;
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"setBotAction\",\"ok\":true,\"slot\":{slot}}}");
                }
            }
            else if (cmd == "loadMap")
            {
                int scene = ExtractIntField(body, "scene", -1);
                if (scene >= 0)
                {
                    // Optional teleport-after-load coordinates. ALL THREE of
                    // x/y/z must be present, else we reject — silently
                    // defaulting missing coords to 0 was sending rigs to the
                    // origin (right above the killbox).
                    bool hasX = HasField(body, "x"), hasY = HasField(body, "y"), hasZ = HasField(body, "z");
                    bool hasTeleport = hasX || hasY || hasZ;
                    if (hasTeleport && !(hasX && hasY && hasZ))
                    {
                        SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":false,\"err\":\"partial teleport coords — need x,y,z together\"}");
                        return;
                    }
                    float tx = hasTeleport ? ExtractFloatField(body, "x") : 0f;
                    float ty = hasTeleport ? ExtractFloatField(body, "y") : 0f;
                    float tz = hasTeleport ? ExtractFloatField(body, "z") : 0f;
                    Log.LogInfo($"Bridge: loadMap({scene}) requested; teleport=({tx},{ty},{tz}) hasTeleport={hasTeleport}");
                    if (hasTeleport)
                    {
                        _pendingTeleport = new Vector3(tx, ty, tz);
                        _pendingTeleportArmed = true;
                        SceneManager.sceneLoaded -= OnSceneLoadedTeleport;
                        SceneManager.sceneLoaded += OnSceneLoadedTeleport;
                    }
                    // Track current scene so subsequent BroadcastMapChange
                    // reflects reality, not the hardcoded boot default.
                    _currentSceneIndex = scene;
                    SceneManager.LoadScene(scene, LoadSceneMode.Single);
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":true,\"scene\":{scene}}}");
                }
                else
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":false,\"err\":\"missing or invalid scene\"}");
                }
            }
            else if (cmd == "teleport")
            {
                // Direct teleport command — no scene load. Useful when you
                // want to re-park the rig (e.g. after it falls into a void).
                // Require all of x/y/z to be present so a malformed payload
                // doesn't park the rig at origin (killbox-adjacent).
                int slot = ExtractIntField(body, "slot", -1);
                bool hasX = HasField(body, "x"), hasY = HasField(body, "y"), hasZ = HasField(body, "z");
                if (!(hasX && hasY && hasZ))
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"teleport\",\"ok\":false,\"err\":\"missing x/y/z\"}");
                    return;
                }
                float tx = ExtractFloatField(body, "x");
                float ty = ExtractFloatField(body, "y");
                float tz = ExtractFloatField(body, "z");
                if (slot >= 0 && SlotToRig.TryGetValue(slot, out var rigGo) && (object)rigGo != null)
                {
                    TeleportRig(rigGo, new Vector3(tx, ty, tz));
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"teleport\",\"ok\":true,\"slot\":{slot}}}");
                }
                else
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"teleport\",\"ok\":false,\"err\":\"slot not found\"}");
                }
            }
            else if (cmd == "sub")
            {
                // Just record peer for stream; no-op response.
                SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"sub\",\"ok\":true}");
            }
            else if (cmd == "spawnPlayer")
            {
                int slot = ExtractIntField(body, "slot", 0);
                // Optional x/y/z to spawn directly at — useful when spawning
                // into a Landfall scene where the default (0,8,0) is below the
                // killbox and the rig dies before any teleport can save it.
                // Must be all-or-nothing so a partial payload doesn't park the
                // rig at origin.
                bool hasX = HasField(body, "x"), hasY = HasField(body, "y"), hasZ = HasField(body, "z");
                bool hasPos = hasX || hasY || hasZ;
                if (hasPos && !(hasX && hasY && hasZ))
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"spawnPlayer\",\"ok\":false,\"err\":\"partial spawn coords — need x,y,z together\"}");
                    return;
                }
                Vector3 pos = new Vector3(0f, 8f, 0f);
                if (hasPos) pos = new Vector3(ExtractFloatField(body, "x"), ExtractFloatField(body, "y"), ExtractFloatField(body, "z"));
                bool ok = TrySpawnPlayer(slot, pos, out string err);
                if (ok)
                {
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"spawnPlayer\",\"ok\":true,\"slot\":{slot}}}");
                }
                else
                {
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"spawnPlayer\",\"ok\":false,\"err\":\"{err}\"}}");
                }
            }
            else if (cmd == "addForce")
            {
                // Most direct possible test: pick the first BodyPart child and
                // AddForce on its Rigidbody manually. If the rig moves, we
                // know physics is healthy and the issue is upstream.
                int slot = ExtractIntField(body, "slot", 0);
                float fz = ExtractFloatField(body, "fz");
                string err;
                bool ok = TryAddForce(slot, fz, out err);
                SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"addForce\",\"ok\":{(ok?"true":"false")},\"err\":\"{err}\"}}");
            }
            else if (cmd == "forceMove")
            {
                // Diagnostic: directly call Movement.MoveRight() for one tick.
                // If position changes, Controller.Update isn't routing our
                // inputs to MoveRight. If it doesn't, MoveRight itself is broken.
                int slot = ExtractIntField(body, "slot", 0);
                string dir = ExtractStringField(body, "dir") ?? "right";
                bool ok = TryForceMove(slot, dir, out string err);
                SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"forceMove\",\"ok\":{(ok?"true":"false")},\"err\":\"{err}\"}}");
            }
            else if (cmd == "inspect")
            {
                int slot = ExtractIntField(body, "slot", 0);
                string info = InspectRig(slot);
                SendBridgeJson(from, $"{{\"reply\":\"inspect\",\"slot\":{slot},\"info\":\"{info.Replace("\\","\\\\").Replace("\"","\\\"")}\"}}");
            }
            else if (cmd == "applyInput")
            {
                int slot = ExtractIntField(body, "slot", -1);
                if (slot < 0)
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"applyInput\",\"ok\":false,\"err\":\"bad slot\"}");
                }
                else
                {
                    var frame = new InputFrame
                    {
                        StickX  = ExtractFloatField(body, "stickX"),
                        StickY  = ExtractFloatField(body, "stickY"),
                        AimX    = ExtractFloatField(body, "aimX"),
                        AimY    = ExtractFloatField(body, "aimY"),
                        Buttons = ExtractIntField(body, "buttons", 0),
                    };
                    SlotInputs[slot] = frame;
                    _applyInputCount++;
                    if (_applyInputCount == 1 || _applyInputCount % 60 == 0)
                        Log.LogInfo($"[INSTR1] applyInput#{_applyInputCount}: slot={slot} stick=({frame.StickX:0.00},{frame.StickY:0.00}) buttons={frame.Buttons} SlotInputs.Count={SlotInputs.Count}");
                    // No reply for applyInput — comes 60 times/sec from Go,
                    // we don't want to flood the network with acks.
                }
            }
            else
            {
                SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"{cmd}\",\"ok\":false,\"err\":\"unknown cmd\"}}");
            }
        }

        private void SendBridgeJson(IPEndPoint to, string json)
        {
            if ((object)_bridge == null) return;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                _bridge.Send(data, data.Length, to);
            }
            catch (Exception e)
            {
                if (Verbose) Log.LogDebug($"Bridge send: {e.Message}");
            }
        }

        // EmitStateSnapshot pushes the current world entity state to the most
        // recently active peer (for the 30Hz stream). When the bridge has never
        // been pinged we don't emit, avoiding wasted work.
        private void EmitStateSnapshot()
        {
            if ((object)_bridgePeer == null) return;
            EmitStateSnapshotTo(_bridgePeer);
        }

        private static readonly StringBuilder _sb = new StringBuilder(2048);

        private void EmitStateSnapshotTo(IPEndPoint to)
        {
            _bridgeTick++;
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                bool inFight = false; int round = _roundCounter;
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType != null)
                {
                    var f = AccessTools.Field(gmType, "inFight");
                    if ((object)f != null) { try { inFight = (bool)f.GetValue(null); } catch { } }
                }
                _sb.Length = 0;
                _sb.Append("{\"reply\":\"snapshot\",\"tick\":").Append(_bridgeTick);
                _sb.Append(",\"scene\":\"").Append(SceneManager.GetActiveScene().name).Append("\"");
                _sb.Append(",\"inFight\":").Append(inFight ? "true" : "false");
                _sb.Append(",\"round\":").Append(round);
                _sb.Append(",\"ents\":[");

                // Report only the rigs we spawned — slot-keyed via SlotToRig.
                // The root transform doesn't move under SF's physics model;
                // position comes from the hip BodyPart rigidbody. We also emit
                // the RL observation fields: hp, alive, velocity, armed.
                bool first = true;
                var bodyPartType = AccessTools.TypeByName("BodyPart");
                var hhType = AccessTools.TypeByName("HealthHandler");
                var ciType = AccessTools.TypeByName("CharacterInformation");
                var fightingType = AccessTools.TypeByName("Fighting");
                var blockType = AccessTools.TypeByName("BlockHandler");
                var aimHelperType = AccessTools.TypeByName("AimTargetHelper");
                var weaponTypeT = AccessTools.TypeByName("Weapon");
                var ctrlTypeT = AccessTools.TypeByName("Controller");
                var aimerF = ((object)ctrlTypeT != null) ? AccessTools.Field(ctrlTypeT, "aimer") : null;
                var hpF = ((object)hhType != null) ? AccessTools.Field(hhType, "health") : null;
                var deadF = ((object)ciType != null) ? AccessTools.Field(ciType, "isDead") : null;
                var weaponF = ((object)fightingType != null) ? AccessTools.Field(fightingType, "weapon") : null;
                // Tier 2/3 perception (2026-06-07): self/opp movement + combat state, mirroring
                // the scripted bot's BotContext (docs/BOT_SENSING.md). Field names verified vs
                // decompiled Assembly-CSharp. Every read is guarded → a missing field degrades to
                // a default and never breaks the snapshot.
                var sinceGroundedF = ((object)ciType != null) ? AccessTools.Field(ciType, "sinceGrounded") : null;
                var sinceFallenF   = ((object)ciType != null) ? AccessTools.Field(ciType, "sinceFallen") : null;
                var sinceJumpedF   = ((object)ciType != null) ? AccessTools.Field(ciType, "sinceJumped") : null;
                var sinceWallF     = ((object)ciType != null) ? AccessTools.Field(ciType, "sinceWall") : null;
                var isSwingingF    = ((object)fightingType != null) ? AccessTools.Field(fightingType, "isSwinging") : null;
                var bulletsLeftF   = ((object)fightingType != null) ? AccessTools.Field(fightingType, "bulletsLeft") : null;
                var isBlockingF    = ((object)blockType != null) ? AccessTools.Field(blockType, "isBlocking") : null;
                var sinceShotF     = ((object)weaponTypeT != null) ? AccessTools.Field(weaponTypeT, "sinceShot") : null;
                // World-geometry ray mask: exclude player/projectile/ignoreplayer
                // layers (8/9/10/11/18/29 per Pathfinder.cs) so the spatial fan
                // senses terrain (walls/platforms/gaps), not other rigs/bullets.
                int worldRayMask = ~((1 << 8) | (1 << 9) | (1 << 10) | (1 << 11) | (1 << 18) | (1 << 29));
                foreach (var kv in SlotToRig)
                {
                    var rig = kv.Value;
                    if ((object)rig == null) continue;
                    Vector3 p = rig.transform.position;
                    Vector3 vel = Vector3.zero;
                    if ((object)bodyPartType != null)
                    {
                        var bp = rig.GetComponentInChildren(bodyPartType) as Component;
                        if ((object)bp != null)
                        {
                            p = bp.transform.position;
                            var rb = bp.GetComponent<Rigidbody>();
                            if ((object)rb != null) vel = rb.velocity;
                        }
                    }
                    float hp = -1f; bool alive = true; bool armed = false;
                    bool grounded = false, ragdolled = false, swinging = false, blocking = false;
                    float sinceJumped = 9f, sinceWall = 9f, sinceShot = 9f; int bulletsLeft = -1;
                    float aimZ = 0f, aimY = 0f;
                    try
                    {
                        var cinf = ((object)ciType != null) ? rig.GetComponentInChildren(ciType) : null;
                        if ((object)cinf != null)
                        {
                            if ((object)deadF != null) alive = !(bool)deadF.GetValue(cinf);
                            if ((object)sinceGroundedF != null) grounded = (float)sinceGroundedF.GetValue(cinf) < 0.2f;
                            if ((object)sinceFallenF != null) ragdolled = (float)sinceFallenF.GetValue(cinf) < 0f;
                            if ((object)sinceJumpedF != null) sinceJumped = (float)sinceJumpedF.GetValue(cinf);
                            if ((object)sinceWallF != null) sinceWall = (float)sinceWallF.GetValue(cinf);
                        }
                        if ((object)hpF != null) { var hh = rig.GetComponentInChildren(hhType); if ((object)hh != null) hp = (float)hpF.GetValue(hh); }
                        var fg = ((object)fightingType != null) ? rig.GetComponentInChildren(fightingType) : null;
                        if ((object)fg != null)
                        {
                            object wpn = ((object)weaponF != null) ? weaponF.GetValue(fg) : null;
                            armed = (object)wpn != null;
                            if ((object)isSwingingF != null) swinging = (bool)isSwingingF.GetValue(fg);
                            if (armed && (object)bulletsLeftF != null) { try { bulletsLeft = (int)bulletsLeftF.GetValue(fg); } catch { } }
                            if (armed && (object)sinceShotF != null) { try { sinceShot = (float)sinceShotF.GetValue(wpn); } catch { } }
                        }
                        if ((object)blockType != null) { var bh = rig.GetComponentInChildren(blockType); if ((object)bh != null && (object)isBlockingF != null) blocking = (bool)isBlockingF.GetValue(bh); }
                        // Aim: prefer Controller.aimer — the transform stock
                        // UserAim() actually writes in BOTH the unarmed and the
                        // gradual-weapon branches (AimTargetHelper is only set on
                        // the gradual branch and goes stale for unarmed rigs).
                        bool gotAim = false;
                        if ((object)aimerF != null && (object)ctrlTypeT != null)
                        {
                            var ctrlComp = rig.GetComponentInChildren(ctrlTypeT) as Component;
                            if ((object)ctrlComp != null)
                            {
                                var aimT = aimerF.GetValue(ctrlComp) as Transform;
                                if ((object)aimT != null) { var fwd = aimT.forward; aimZ = fwd.z; aimY = fwd.y; gotAim = true; }
                            }
                        }
                        if (!gotAim && (object)aimHelperType != null) { var ah = rig.GetComponentInChildren(aimHelperType) as Component; if ((object)ah != null) { var fwd = ah.transform.forward; aimZ = fwd.z; aimY = fwd.y; } }
                    }
                    catch { }
                    if (!first) _sb.Append(",");
                    first = false;
                    _sb.Append("{\"slot\":").Append(kv.Key);
                    _sb.Append(",\"x\":").Append(p.x.ToString("0.000", ci));
                    _sb.Append(",\"y\":").Append(p.y.ToString("0.000", ci));
                    _sb.Append(",\"z\":").Append(p.z.ToString("0.000", ci));
                    _sb.Append(",\"vx\":").Append(vel.x.ToString("0.000", ci));
                    _sb.Append(",\"vy\":").Append(vel.y.ToString("0.000", ci));
                    _sb.Append(",\"vz\":").Append(vel.z.ToString("0.000", ci));
                    _sb.Append(",\"hp\":").Append(hp.ToString("0.0", ci));
                    _sb.Append(",\"alive\":").Append(alive ? "true" : "false");
                    _sb.Append(",\"armed\":").Append(armed ? "true" : "false");
                    _sb.Append(",\"grnd\":").Append(grounded ? "true" : "false");
                    _sb.Append(",\"rag\":").Append(ragdolled ? "true" : "false");
                    _sb.Append(",\"sws\":").Append(swinging ? "true" : "false");
                    _sb.Append(",\"blk\":").Append(blocking ? "true" : "false");
                    _sb.Append(",\"sj\":").Append(sinceJumped.ToString("0.00", ci));
                    _sb.Append(",\"sw\":").Append(sinceWall.ToString("0.00", ci));
                    _sb.Append(",\"ss\":").Append(sinceShot.ToString("0.00", ci));
                    _sb.Append(",\"bl\":").Append(bulletsLeft);
                    _sb.Append(",\"aimz\":").Append(aimZ.ToString("0.000", ci));
                    _sb.Append(",\"aimy\":").Append(aimY.ToString("0.000", ci));
                    // Tier-2 spatial ray fan: 16 rays in the YZ plane (x is locked
                    // in SF) from the body, world geometry only. Normalized hit
                    // distance (1.0 = clear to 20m). Original FrameBuilder used 64;
                    // 16 is enough resolution for an MLP + keeps the obs bounded.
                    _sb.Append(",\"rays\":[");
                    for (int ri = 0; ri < 16; ri++)
                    {
                        float ang = ri * 0.3926991f;   // 2*pi/16
                        var dir = new Vector3(0f, Mathf.Sin(ang), Mathf.Cos(ang));
                        // RaycastAll + skip any collider belonging to a PLAYER rig
                        // (own ragdoll body parts sit on non-masked layers and pinned
                        // every ray short; the opponent is already in the obs). Leaves
                        // only world geometry — walls/platforms/ground/edges.
                        float best = 20f;
                        var hits = Physics.RaycastAll(p, dir, 20f, worldRayMask);
                        for (int h = 0; h < hits.Length; h++)
                        {
                            var col = hits[h].collider;
                            if ((object)col == null) continue;
                            bool isRig = false;
                            foreach (var kv2 in SlotToRig)
                            {
                                if ((object)kv2.Value != null && col.transform.IsChildOf(kv2.Value.transform)) { isRig = true; break; }
                            }
                            if (isRig) continue;
                            if (hits[h].distance < best) best = hits[h].distance;
                        }
                        if (ri > 0) _sb.Append(",");
                        _sb.Append((best / 20f).ToString("0.000", ci));
                    }
                    _sb.Append("]");
                    // Current input frame for this slot (teacher-driven slots:
                    // the scripted driver's EXACT choice this tick — the BC
                    // demo label; RL slots: echo of the last setBotAction).
                    {
                        InputFrame curIn;
                        if (SlotInputs.TryGetValue(kv.Key, out curIn))
                            _sb.Append(",\"in\":[").Append(curIn.StickX.ToString("0.00", ci))
                               .Append(",").Append(curIn.Buttons).Append("]");
                    }
                    _sb.Append("}");
                }
                _sb.Append("]");   // close ents
                // Tier-3 threat sense: in-flight projectiles (RayCastForward) +
                // thrown blades (ThrownWeapon) as [z, y, fz, fy] (pos + unit dir),
                // up to 8. Empty at stage 0 (stationary dummy never shoots).
                _sb.Append(",\"proj\":[");
                try
                {
                    int pc = 0;
                    foreach (var tn in new[] { "RayCastForward", "ThrownWeapon" })
                    {
                        var pt = AccessTools.TypeByName(tn);
                        if ((object)pt == null) continue;
                        var arr = UnityEngine.Object.FindObjectsOfType(pt);
                        for (int i = 0; i < arr.Length && pc < 8; i++)
                        {
                            var comp = arr[i] as Component;
                            if ((object)comp == null) continue;
                            var pp = comp.transform.position; var fwd = comp.transform.forward;
                            if (pc > 0) _sb.Append(",");
                            _sb.Append("[").Append(pp.z.ToString("0.00", ci)).Append(",").Append(pp.y.ToString("0.00", ci))
                               .Append(",").Append(fwd.z.ToString("0.000", ci)).Append(",").Append(fwd.y.ToString("0.000", ci)).Append("]");
                            pc++;
                        }
                    }
                }
                catch { }
                _sb.Append("]");
                // Ground weapons: up to 6 settled, pickup-able WeaponPickUps
                // nearest to any spawned rig, as [z,y]. Gives the policy a
                // gradient toward arming itself — DSF rules have auto-pickup
                // OFF, so walking to a weapon is learned behavior; without
                // these the agent is blind to where weapons are.
                _sb.Append(",\"wps\":[");
                try
                {
                    var wpT2 = AccessTools.TypeByName("WeaponPickUp");
                    if ((object)wpT2 != null)
                    {
                        var counterF2 = AccessTools.Field(wpT2, "counter");
                        var cantF2 = AccessTools.Field(wpT2, "cantBePickledUpFor");
                        var warr = UnityEngine.Object.FindObjectsOfType(wpT2);
                        // Rig position = hip BodyPart, NOT the root transform —
                        // the root never moves under SF's physics (review fix:
                        // ranking against frozen spawn positions would drop the
                        // weapon actually nearest a roaming rig from the top-6).
                        var rigPos = new List<Vector3>();
                        foreach (var kv2 in SlotToRig)
                        {
                            if ((object)kv2.Value == null) continue;
                            Vector3 rpos = kv2.Value.transform.position;
                            if ((object)bodyPartType != null)
                            {
                                var rbp = kv2.Value.GetComponentInChildren(bodyPartType) as Component;
                                if ((object)rbp != null) rpos = rbp.transform.position;
                            }
                            rigPos.Add(rpos);
                        }
                        var cand = new List<KeyValuePair<float, Vector3>>();
                        for (int i = 0; i < warr.Length; i++)
                        {
                            var w = warr[i] as Component;
                            if ((object)w == null) continue;
                            try
                            {
                                if ((object)counterF2 != null && (float)counterF2.GetValue(w) <= 0.3f) continue;
                                if ((object)cantF2 != null && (float)cantF2.GetValue(w) >= 0f) continue;
                            }
                            catch { }
                            var wpp = w.transform.position;
                            float dmin = float.MaxValue;
                            for (int r = 0; r < rigPos.Count; r++)
                            {
                                float d = Vector3.Distance(rigPos[r], wpp);
                                if (d < dmin) dmin = d;
                            }
                            cand.Add(new KeyValuePair<float, Vector3>(dmin, wpp));
                        }
                        cand.Sort((a, b) => a.Key.CompareTo(b.Key));
                        int wn = Math.Min(6, cand.Count);
                        for (int i = 0; i < wn; i++)
                        {
                            if (i > 0) _sb.Append(",");
                            _sb.Append("[").Append(cand[i].Value.z.ToString("0.00", ci))
                               .Append(",").Append(cand[i].Value.y.ToString("0.00", ci)).Append("]");
                        }
                    }
                }
                catch { }
                _sb.Append("]}");
                SendBridgeJson(to, _sb.ToString());
            }
            catch (Exception e)
            {
                if (Verbose) Log.LogDebug($"EmitStateSnapshot: {e.Message}");
            }
        }

        // TryAddForce directly AddForces on the rig's first BodyPart Rigidbody.
        // If THIS doesn't move the rig, the Rigidbody is constrained somehow
        // (joints, freezeAll, mass=infinity, etc.) — not a force-routing issue.
        private bool TryAddForce(int slot, float fz, out string err)
        {
            err = "";
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) { err = "no rig"; return false; }
                var bp = rig.GetComponentInChildren(AccessTools.TypeByName("BodyPart")) as Component;
                if ((object)bp == null) { err = "no BodyPart"; return false; }
                var rb = bp.GetComponent<Rigidbody>();
                if ((object)rb == null) { err = "no Rigidbody on BodyPart"; return false; }
                rb.AddForce(new Vector3(0f, 0f, fz), ForceMode.Impulse);
                err = $"applied F=(0,0,{fz}) Imp to {bp.gameObject.name}; rb.mass={rb.mass} kinematic={rb.isKinematic} constraints={rb.constraints}";
                return true;
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        // TryForceMove directly calls Movement.MoveRight/MoveLeft on the rig
        // for diagnostic purposes — bypassing Controller.Update's input read.
        private bool TryForceMove(int slot, string dir, out string err)
        {
            err = "";
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) { err = "no rig"; return false; }
                var mov = rig.GetComponent(AccessTools.TypeByName("Movement"));
                if ((object)mov == null) { err = "no Movement"; return false; }
                string methodName = dir == "left" ? "MoveLeft" : "MoveRight";
                var m = AccessTools.Method(mov.GetType(), methodName);
                if ((object)m == null) { err = "no " + methodName; return false; }
                m.Invoke(mov, null);
                return true;
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        // InspectRig dumps the slot's rig state — useful to diagnose why a
        // freshly-spawned player isn't moving / falling / responding to input.
        private string InspectRig(int slot)
        {
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null)
                {
                    return "no rig";
                }
                var sb = new StringBuilder(512);
                sb.Append("active=").Append(rig.activeSelf).Append("/").Append(rig.activeInHierarchy);
                sb.Append("; pos=").Append(rig.transform.position.ToString("0.00"));

                var rb = rig.GetComponent<Rigidbody>();
                if ((object)rb != null)
                {
                    sb.Append("; rb.kinematic=").Append(rb.isKinematic);
                    sb.Append(" useGravity=").Append(rb.useGravity);
                    sb.Append(" vel=").Append(rb.velocity.ToString("0.00"));
                }
                else sb.Append("; no Rigidbody");

                var ctrl = rig.GetComponent(AccessTools.TypeByName("Controller"));
                if ((object)ctrl != null)
                {
                    var hasControl = AccessTools.Field(ctrl.GetType(), "mHasControl");
                    if ((object)hasControl != null) sb.Append("; hasControl=").Append(hasControl.GetValue(ctrl));
                    var inactive = AccessTools.Field(ctrl.GetType(), "inactive");
                    if ((object)inactive != null) sb.Append(" inactive=").Append(inactive.GetValue(ctrl));
                }
                else sb.Append("; no Controller");

                var mov = rig.GetComponent(AccessTools.TypeByName("Movement"));
                if ((object)mov != null)
                {
                    sb.Append("; Movement=").Append(((Behaviour)mov).enabled);
                    var fm = AccessTools.Field(mov.GetType(), "forceMultiplier");
                    if ((object)fm != null) sb.Append(" forceMultiplier=").Append(fm.GetValue(mov));
                }
                else sb.Append("; no Movement");

                var fighting = rig.GetComponent(AccessTools.TypeByName("Fighting"));
                if ((object)fighting != null)
                {
                    var mm = AccessTools.Field(fighting.GetType(), "movementMultiplier");
                    if ((object)mm != null) sb.Append("; movementMultiplier=").Append(mm.GetValue(fighting));
                }

                var info = rig.GetComponent(AccessTools.TypeByName("CharacterInformation"));
                if ((object)info != null)
                {
                    var sf = AccessTools.Field(info.GetType(), "sinceFallen");
                    if ((object)sf != null) sb.Append("; sinceFallen=").Append(sf.GetValue(info));
                    var dead = AccessTools.Field(info.GetType(), "isDead");
                    if ((object)dead != null) sb.Append(" isDead=").Append(dead.GetValue(info));
                }

                // Dump CharacterActions Movement.X / Y / Left / Right values
                // so we can see whether our injection is taking effect.
                var ctrl2 = rig.GetComponent(AccessTools.TypeByName("Controller"));
                if ((object)ctrl2 != null)
                {
                    var pa = AccessTools.Field(ctrl2.GetType(), "mPlayerActions")?.GetValue(ctrl2);
                    if ((object)pa != null)
                    {
                        var movement = AccessTools.Field(pa.GetType(), "Movement")?.GetValue(pa);
                        if ((object)movement != null)
                        {
                            float mx = (float)AccessTools.Property(movement.GetType(), "X").GetValue(movement, null);
                            float my = (float)AccessTools.Property(movement.GetType(), "Y").GetValue(movement, null);
                            sb.Append("; Movement.X=").Append(mx.ToString("0.00")).Append(" .Y=").Append(my.ToString("0.00"));
                        }
                        var leftPa = AccessTools.Field(pa.GetType(), "Left")?.GetValue(pa);
                        var rightPa = AccessTools.Field(pa.GetType(), "Right")?.GetValue(pa);
                        if ((object)leftPa != null && (object)rightPa != null)
                        {
                            var leftVal = AccessTools.Property(leftPa.GetType(), "Value")?.GetValue(leftPa, null);
                            var rightVal = AccessTools.Property(rightPa.GetType(), "Value")?.GetValue(rightPa, null);
                            sb.Append("; Left.Value=").Append(leftVal).Append(" Right.Value=").Append(rightVal);
                        }
                    }
                }

                sb.Append("; Time.timeScale=").Append(Time.timeScale.ToString("0.000"));
                sb.Append("; Time.deltaTime=").Append(Time.deltaTime.ToString("0.000"));
                sb.Append("; fixedDelta=").Append(Time.fixedDeltaTime.ToString("0.000"));

                var standing = rig.GetComponent(AccessTools.TypeByName("Standing"));
                if ((object)standing != null) sb.Append("; Standing=").Append(((Behaviour)standing).enabled);
                else sb.Append("; no Standing");

                return sb.ToString();
            }
            catch (Exception e) { return "exc: " + e.Message; }
        }

        // TrySpawnPlayer instantiates a Player rig in the active scene at the
        // slot's spawn point, by grabbing ControllerHandler.playerPrefab and
        // calling Object.Instantiate directly. This sidesteps the InputDevice
        // pairing path (which requires real input hardware) — the rig will
        // exist but won't move until we inject inputs.
        //
        // Returns (true, "") on success or (false, "reason") on failure.
        private void TryCachePlayerPrefab()
        {
            if ((object)_cachedPlayerPrefab != null) return;
            try
            {
                var chType = AccessTools.TypeByName("ControllerHandler");
                if ((object)chType == null) { Log.LogWarning("CachePrefab: ControllerHandler type missing"); return; }
                var chInst = UnityEngine.Object.FindObjectOfType(chType);
                if ((object)chInst == null) { Log.LogWarning("CachePrefab: no ControllerHandler instance in active scene"); return; }
                var pf = AccessTools.Field(chType, "playerPrefab");
                if ((object)pf == null) { Log.LogWarning("CachePrefab: playerPrefab field missing"); return; }
                var go = pf.GetValue(chInst) as GameObject;
                if ((object)go == null) { Log.LogWarning("CachePrefab: playerPrefab value is null"); return; }
                _cachedPlayerPrefab = go;
                Log.LogInfo($"CachePrefab: cached playerPrefab '{go.name}' for cross-scene spawns.");
            }
            catch (Exception e) { Log.LogError($"TryCachePlayerPrefab threw: {e.Message}"); }
        }

        private bool TrySpawnPlayer(int slot, Vector3 spawnPosOverride, out string err)
        {
            err = "";
            try
            {
                GameObject prefab = _cachedPlayerPrefab;
                if ((object)prefab == null)
                {
                    var chType = AccessTools.TypeByName("ControllerHandler");
                    if ((object)chType == null) { err = "ControllerHandler type not found"; return false; }
                    var chInst = UnityEngine.Object.FindObjectOfType(chType);
                    if ((object)chInst == null) { err = "ControllerHandler instance not in scene (and no cached prefab)"; return false; }
                    var prefabField = AccessTools.Field(chType, "playerPrefab");
                    if ((object)prefabField == null) { err = "playerPrefab field not found"; return false; }
                    prefab = prefabField.GetValue(chInst) as GameObject;
                    if ((object)prefab == null) { err = "playerPrefab is null"; return false; }
                    _cachedPlayerPrefab = prefab;
                    Log.LogInfo("Cached playerPrefab for cross-scene spawns.");
                }
                var spawnPos = spawnPosOverride; // caller-supplied; defaults to (0,8,0) in bridge handler
                var go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity) as GameObject;
                if ((object)go == null) { err = "Instantiate returned null"; return false; }
                go.name = $"OracleSpawn_Slot{slot}";
                // Survive SceneManager.LoadScene switches. Without this, the
                // rig is destroyed when we transition from MainScene (where
                // ControllerHandler lives, needed to spawn the rig) to a
                // Landfall scene (which has real platforms but no spawn
                // infrastructure).
                UnityEngine.Object.DontDestroyOnLoad(go);

                // Bind a fresh CharacterActions so the Controller has somewhere
                // to read input from. Without this, mPlayerActions is null and
                // the Controller.Update path early-returns / no movement.
                //
                // Stock ControllerHandler.CreatePlayer calls AssignNewDevice
                // (which requires a real InputDevice we can't synthesize),
                // but Controller also exposes TakeLocalControl(CharacterActions)
                // which doesn't need a device — perfect for our bridge-driven
                // input flow.
                var ctrlType = AccessTools.TypeByName("Controller");
                var caType = AccessTools.TypeByName("CharacterActions");
                if ((object)ctrlType != null && (object)caType != null)
                {
                    var ctrl = go.GetComponent(ctrlType);
                    if ((object)ctrl != null)
                    {
                        var createMethod = AccessTools.Method(caType, "CreateWithControllerBindings");
                        if ((object)createMethod != null)
                        {
                            var actions = createMethod.Invoke(null, null);
                            var takeMethod = AccessTools.Method(ctrlType, "TakeLocalControl");
                            if ((object)actions != null && (object)takeMethod != null)
                            {
                                takeMethod.Invoke(ctrl, new object[] { actions });
                                // Also assign a playerID so any code reading
                                // controller.playerID gets a sensible slot.
                                var pidField = AccessTools.Field(ctrlType, "playerID");
                                if ((object)pidField != null) pidField.SetValue(ctrl, slot);
                                Log.LogInfo($"Bound CharacterActions to slot {slot} via TakeLocalControl.");
                            }
                            else
                            {
                                Log.LogWarning("Could not bind CharacterActions: CreateWith* returned null or TakeLocalControl missing.");
                            }
                        }
                    }
                }

                SlotToRig[slot] = go;
                if (!SlotInputs.ContainsKey(slot))
                {
                    SlotInputs[slot] = new InputFrame();
                }

                // Clear regularBindings on every underlying PlayerAction in
                // this CharacterActions instance. InControl's PlayerAction.
                // UpdateBindings loops over regularBindings each frame and
                // calls UpdateWithValue(bindingSource.GetValue(Device), ...),
                // which writes 0 because we have no real device — that's what
                // clobbers our manually-injected values. With no bindings,
                // the loop is a no-op and our UpdateWithValue calls survive.
                ClearAllPlayerActionBindings(go);

                Log.LogInfo($"Spawned oracle player rig for slot {slot} at {spawnPos} (GO: {go.name})");
                return true;
            }
            catch (Exception e)
            {
                err = e.Message;
                return false;
            }
        }

        // ClearAllPlayerActionBindings walks the rig's CharacterActions and
        // clears each PlayerAction's regularBindings list. Required so our
        // per-frame UpdateWithValue calls aren't immediately overwritten by
        // InControl's UpdateBindings loop reading from null devices.
        private static void ClearAllPlayerActionBindings(GameObject rig)
        {
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType == null) return;
                var ctrl = rig.GetComponent(ctrlType);
                if ((object)ctrl == null) return;
                var actionsField = AccessTools.Field(ctrlType, "mPlayerActions");
                if ((object)actionsField == null) return;
                var actions = actionsField.GetValue(ctrl);
                if ((object)actions == null) return;

                var paType = AccessTools.TypeByName("InControl.PlayerAction");
                if ((object)paType == null) return;
                var bindingsField = AccessTools.Field(paType, "regularBindings");
                var visibleField  = AccessTools.Field(paType, "visibleBindings");
                if ((object)bindingsField == null) return;

                // Walk every field on the CharacterActions instance; any
                // PlayerAction we find, clear its bindings.
                int cleared = 0;
                foreach (var f in actions.GetType().GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var v = f.GetValue(actions);
                    if ((object)v == null) continue;
                    if (!paType.IsInstanceOfType(v)) continue;
                    var listObj = bindingsField.GetValue(v);
                    var clearMethod = listObj?.GetType().GetMethod("Clear");
                    clearMethod?.Invoke(listObj, null);
                    if ((object)visibleField != null)
                    {
                        var visObj = visibleField.GetValue(v);
                        visObj?.GetType().GetMethod("Clear")?.Invoke(visObj, null);
                    }
                    cleared++;
                }
                Log.LogInfo($"Cleared regularBindings on {cleared} PlayerActions.");
            }
            catch (Exception e)
            {
                Log.LogError($"ClearAllPlayerActionBindings: {e.Message}");
            }
        }

        // Lookup cache for InControl.PlayerAction.UpdateWithValue MethodInfo.
        private static MethodInfo _cachedUpdateWithValue;
        private static bool _loggedUpdateWithValue;
        private static MethodInfo GetUpdateWithValueMethod()
        {
            if ((object)_cachedUpdateWithValue != null) return _cachedUpdateWithValue;
            var paType = AccessTools.TypeByName("InControl.PlayerAction");
            if ((object)paType == null)
            {
                if (!_loggedUpdateWithValue) { Log.LogWarning("UpdateWithValue: no InControl.PlayerAction type"); _loggedUpdateWithValue = true; }
                return null;
            }
            _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue",
                new Type[] { typeof(float), typeof(ulong), typeof(float) });
            if ((object)_cachedUpdateWithValue == null && !_loggedUpdateWithValue)
            {
                Log.LogWarning("UpdateWithValue: method not found on PlayerAction. Trying without param-type filter…");
                _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue");
                if ((object)_cachedUpdateWithValue == null) Log.LogWarning("UpdateWithValue: not found even without filter");
                else Log.LogInfo($"UpdateWithValue: found via fallback, signature: {_cachedUpdateWithValue}");
                _loggedUpdateWithValue = true;
            }
            else if (!_loggedUpdateWithValue)
            {
                Log.LogInfo($"UpdateWithValue: found, signature: {_cachedUpdateWithValue}");
                _loggedUpdateWithValue = true;
            }
            return _cachedUpdateWithValue;
        }

        private static bool _loggedPushPath;
        private static readonly Dictionary<string, FieldInfo> _pushFieldCache = new Dictionary<string, FieldInfo>(64);
        private static readonly object[] _pushArgsBuffer = new object[3];
        // PushPlayerAction calls PlayerAction.UpdateWithValue(value, tick, dt)
        // on the named PlayerAction field of the given CharacterActions.
        // Mono 2.x: never compare MethodInfo/FieldInfo with != — use (object)x == null.
        private static void PushPlayerAction(object actions, string fieldName, float value)
        {
            if ((object)actions == null) return;
            var actionsType = actions.GetType();
            string cacheKey = actionsType.FullName + "|" + fieldName;
            FieldInfo f;
            if (!_pushFieldCache.TryGetValue(cacheKey, out f))
            {
                f = AccessTools.Field(actionsType, fieldName);
                _pushFieldCache[cacheKey] = f;
            }
            if ((object)f == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: field not found on type {actionsType}"); _loggedPushPath = true; }
                return;
            }
            var action = f.GetValue(actions);
            if ((object)action == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: field value is null"); _loggedPushPath = true; }
                return;
            }
            var m = GetUpdateWithValueMethod();
            if ((object)m == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: UpdateWithValue method lookup failed; action type={action.GetType()}"); _loggedPushPath = true; }
                return;
            }
            try
            {
                _pushArgsBuffer[0] = value;
                _pushArgsBuffer[1] = (ulong)0;
                _pushArgsBuffer[2] = Time.deltaTime;
                m.Invoke(action, _pushArgsBuffer);
                if (!_loggedPushPath) { Log.LogInfo($"PushPlayerAction[{fieldName}]: invoke ok, value={value}"); _loggedPushPath = true; }
            }
            catch (Exception e)
            {
                if (!_loggedPushPath) { Log.LogError($"PushPlayerAction[{fieldName}] invoke threw: {e}"); _loggedPushPath = true; }
            }
        }

        // WriteInputsToRigs pushes the most recent per-slot input frame into
        // each spawned rig's CharacterActions via reflection. The Controller
        // reads these every frame in Update — so by writing them right before
        // Controller.Update runs (we're called from Plugin.Update which Unity
        // schedules before MonoBehaviours by default), our values become the
        // effective input for that frame.
        //
        // CharacterActions is an InControl PlayerActionSet. Its Movement /
        // Aiming fields are TwoAxisInputControl with a settable RawValue.
        // Buttons are PlayerAction with a settable RawValue / IsPressed.
        private static bool _loggedFirstWrite;
        private static bool _loggedFirstWriteIter;
        private static float _writeInputsErrLogAt = -1f;
        private float _boxDiagLastAt = -1f;
        private void TickBoxDiagnostic()
        {
            if (Time.realtimeSinceStartup - _boxDiagLastAt < 5f) return;
            _boxDiagLastAt = Time.realtimeSinceStartup;
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                }
                var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                int total = all != null ? all.Length : 0;
                int voided = 0;
                float yMin = float.MaxValue, yMax = float.MinValue;
                if (all != null)
                {
                    foreach (var nso in all)
                    {
                        var comp = nso as Component;
                        if ((object)comp == null) continue;
                        float y = comp.transform.position.y;
                        if (y < yMin) yMin = y;
                        if (y > yMax) yMax = y;
                        if (y < -30f) voided++;
                    }
                }
                if (total == 0) yMin = yMax = 0f;
                Log.LogInfo($"[BOX-DIAG] nsos={total} void(y<-30)={voided} y=[{yMin:0.0},{yMax:0.0}] rigs={SlotToRig.Count} scene={SceneManager.GetActiveScene().name}");
            }
            catch (Exception e) { Log.LogWarning($"[BOX-DIAG] {e.Message}"); }
        }

        private void WriteInputsToRigs()
        {
            if (SlotToRig.Count == 0) return;
            if (!_loggedFirstWrite) { Log.LogInfo($"WriteInputsToRigs called for first time. SlotToRig.Count={SlotToRig.Count} SlotInputs.Count={SlotInputs.Count}"); _loggedFirstWrite = true; }
            try
            {
                foreach (var kv in SlotToRig)
                {
                    int slot = kv.Key;
                    GameObject rig = kv.Value;
                    if ((object)rig == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: rig null"); _loggedFirstWriteIter = true; } continue; }
                    if (!SlotInputs.TryGetValue(slot, out var input)) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: SlotInputs miss"); _loggedFirstWriteIter = true; } continue; }

                    var ctrlType = AccessTools.TypeByName("Controller");
                    if ((object)ctrlType == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no Controller type"); _loggedFirstWriteIter = true; } continue; }
                    var ctrl = rig.GetComponent(ctrlType);
                    if ((object)ctrl == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no Controller on rig"); _loggedFirstWriteIter = true; } continue; }
                    var actionsField = AccessTools.Field(ctrlType, "mPlayerActions");
                    if ((object)actionsField == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no mPlayerActions field"); _loggedFirstWriteIter = true; } continue; }
                    var actions = actionsField.GetValue(ctrl);
                    if ((object)actions == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: mPlayerActions is null"); _loggedFirstWriteIter = true; } continue; }

                    if (!_loggedFirstWriteIter) { Log.LogInfo($"WriteInputs iter: REACHED PushPlayerAction, actions type={actions.GetType().FullName}, stick=({input.StickX},{input.StickY})"); _loggedFirstWriteIter = true; }

                    // Feed the underlying L/R/U/D PlayerActions — that's
                    // what CharacterActions.Movement (a PlayerTwoAxisAction)
                    // computes its X/Y from. Setting Movement.thisValue
                    // directly gets overwritten next frame by
                    // PlayerTwoAxisAction.Update reading L/R/U/D.
                    PushPlayerAction(actions, "Left",  Mathf.Max(0f, -input.StickX));
                    PushPlayerAction(actions, "Right", Mathf.Max(0f,  input.StickX));
                    PushPlayerAction(actions, "Up",    Mathf.Max(0f,  input.StickY));
                    PushPlayerAction(actions, "Down",  Mathf.Max(0f, -input.StickY));

                    PushPlayerAction(actions, "AimLeft",  Mathf.Max(0f, -input.AimX));
                    PushPlayerAction(actions, "AimRight", Mathf.Max(0f,  input.AimX));
                    PushPlayerAction(actions, "AimUp",    Mathf.Max(0f,  input.AimY));
                    PushPlayerAction(actions, "AimDown",  Mathf.Max(0f, -input.AimY));

                    PushPlayerAction(actions, "Jump",         (input.Buttons & 0x01) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Jump2",        (input.Buttons & 0x01) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "PunchOrFire",  (input.Buttons & 0x02) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Block",        (input.Buttons & 0x04) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Throw",        (input.Buttons & 0x08) != 0 ? 1f : 0f);
                }
            }
            catch (Exception e)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _writeInputsErrLogAt >= 5f)
                {
                    _writeInputsErrLogAt = now;
                    Log.LogWarning($"WriteInputsToRigs: {e.Message}");
                }
            }
        }

        // SetTwoAxis writes (x, y) to the named TwoAxisInputControl on the
        // CharacterActions instance by poking its private `thisValue` Vector2
        // field directly. Stock InControl exposes Value as a getter only
        // and no setter API for "fake" input — we have to bypass.
        private static void SetTwoAxis(object actions, string fieldName, Vector2 v)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, v);
            // X / Y are protected properties; their backing fields are auto-
            // generated (<X>k__BackingField). Update them too so anything that
            // reads .X / .Y sees the new value.
            var xBacking = AccessTools.Field(t, "<X>k__BackingField");
            var yBacking = AccessTools.Field(t, "<Y>k__BackingField");
            if ((object)xBacking != null) xBacking.SetValue(ctrl, v.x);
            if ((object)yBacking != null) yBacking.SetValue(ctrl, v.y);
        }

        // SetOneAxisOrButton writes a button-press state by setting the
        // PlayerAction's private thisValue (float, 0.0 / 1.0).
        private static void SetOneAxisOrButton(object actions, string fieldName, bool pressed)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, pressed ? 1.0f : 0.0f);
        }

        // === tiny JSON field extractors (avoid dragging in JSON.NET) ===
        //
        // FindField returns the index of a `"field"` token where the preceding
        // character is a key boundary ({, ,, or whitespace). Without this, a
        // search for "x" matches "tx", "exit", etc. and a search for "slot"
        // matches "slotName". Returns -1 if no boundary-respecting match.
        private static int FindField(string json, string field)
        {
            string token = "\"" + field + "\"";
            int from = 0;
            while (from < json.Length)
            {
                int i = json.IndexOf(token, from);
                if (i < 0) return -1;
                bool boundaryOk = (i == 0);
                if (!boundaryOk)
                {
                    char prev = json[i - 1];
                    boundaryOk = prev == '{' || prev == ',' || prev == ' ' || prev == '\t' || prev == '\n' || prev == '\r';
                }
                if (boundaryOk) return i;
                from = i + 1;
            }
            return -1;
        }

        // HasField — quick presence check used by callers that need to
        // distinguish "field absent" from "field present with default value".
        private static bool HasField(string json, string field) => FindField(json, field) >= 0;

        private static string ExtractStringField(string json, string field)
        {
            int i = FindField(json, field);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static float ExtractFloatField(string json, string field)
        {
            int i = FindField(json, field);
            if (i < 0) return 0f;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return 0f;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '.' || json[end] == 'e' || json[end] == 'E' || json[end] == '+')) end++;
            if (end == start) return 0f;
            float f;
            if (float.TryParse(json.Substring(start, end - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) return f;
            return 0f;
        }

        private static int ExtractIntField(string json, string field, int fallback)
        {
            int i = FindField(json, field);
            if (i < 0) return fallback;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return fallback;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end == start) return fallback;
            int n;
            if (int.TryParse(json.Substring(start, end - start), out n)) return n;
            return fallback;
        }

        private void StartHost()
        {
            // Path A: oracle owns the patched DLL's wire protocol directly.
            // No Lidgren MatchMakingHandlerSockets.HostServer — the patched
            // DLL doesn't actually use Lidgren (its socket-mode receive is
            // commented out; P2PPackageHandler.Init opens a RAW UDP socket
            // via UDPClient(address, port)). We bind our OWN raw UDP socket
            // on BindPort and parse the 14-byte-wrapped v25 protocol that
            // sfdsrv speaks.
            try
            {
                _sfServer = new UdpClient(BindPort);
                _sfServer.Client.Blocking = false;
                Log.LogInfo($"SF server: listening on UDP {BindPort} (raw v25 protocol).");
            }
            catch (Exception e)
            {
                Log.LogError($"SF server bind on {BindPort} threw: {e}");
                return;
            }
            // Default tickrate: 60Hz on both server and client. NOTE: the
            // game's own TimeManager asset already ships 0.01666 (60Hz) — the
            // old "Unity stock 50Hz" belief across sf docs was wrong (verified
            // against the real ProjectSettings dump 2026-06-12), so this line
            // is a defensive no-op. Client plugin sets the same in
            // SFClientRecon.Awake. Operator can change live with /tickrate N.
            Time.fixedDeltaTime = 1f / 60f;
            {
                float fd = Time.fixedDeltaTime;
                int hz = (fd > 0f) ? (int)System.Math.Round(1.0 / fd) : 0;
                Log.LogInfo($"Server physics: {hz}Hz (Time.fixedDeltaTime={fd:0.0000}s). Snapshot broadcast: {SnapshotHz}Hz. Client FPS is independent.");
            }
            Log.LogInfo($"=== HEADLESS HOST READY on port {BindPort} ===");
        }

        private static void ReadEnv()
        {
            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_PORT"), out var p);
            if (p > 0 && p < 65536) BindPort = p;

            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_BRIDGEPORT"), out var bp);
            if (bp > 0 && bp < 65536) BridgePort = bp;

            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_SCENE"), out var s);
            if (s >= 0) InitialScene = s;

            Verbose = Environment.GetEnvironmentVariable("SFHEADLESS_DEBUG") == "1";

            float fv;
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_ROUND_END_DELAY"), out fv) && fv >= 0f && fv <= 10f)
                RoundEndDelaySec = fv;
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_NEXT_MATCH_DELAY"), out fv) && fv >= 0f && fv <= 10f)
                NextMatchDelaySec = fv;
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_ROUND_MIN_PLAY"), out fv) && fv >= 3f && fv <= 60f)
                RoundMinPlaySec = fv;
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_PRE_COMBAT_DELAY"), out fv) && fv >= 0.3f && fv <= 10f)
                OraclePreCombatGraceSec = fv;
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_TIMESCALE"), out fv) && fv >= 1f && fv <= 5f)
                TrainTimeScale = fv;
            int iv;
            if (int.TryParse(Environment.GetEnvironmentVariable("SF_STAGE_HP"), out iv) && iv >= 1 && iv <= 100)
                StageHP = iv;

            // SFGYM_BOT_SLOTS=0,1 → auto-spawn in-process scripted bots in those
            // player slots (0..3). Comma-separated. Empty/unset = disabled.
            string botSlots = Environment.GetEnvironmentVariable("SFGYM_BOT_SLOTS");
            if (!string.IsNullOrEmpty(botSlots))
            {
                AutoSpawnBotSlots = new List<int>();
                foreach (var tok in botSlots.Split(','))
                {
                    if (int.TryParse(tok.Trim(), out var botSlot) && botSlot >= 0 && botSlot <= 3)
                        AutoSpawnBotSlots.Add(botSlot);
                }
            }
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_BOT_STALL_SECS"), out var stallSecs) && stallSecs >= 5f && stallSecs <= 300f)
                _botStallSecs = stallSecs;
            FixedMap = -1;
            if (int.TryParse(Environment.GetEnvironmentVariable("SF_FIXED_MAP"), out var fm) && fm >= 0 && fm <= 124)
                FixedMap = fm;
            // SFGYM_RL_SLOTS=0,1 → those slots are driven by an external RL
            // policy (setBotAction); scripted driver skips their inputs.
            RlControlledSlots = new HashSet<int>();
            string rlSlots = Environment.GetEnvironmentVariable("SFGYM_RL_SLOTS");
            if (!string.IsNullOrEmpty(rlSlots))
                foreach (var tok in rlSlots.Split(','))
                    if (int.TryParse(tok.Trim(), out var rlSlot) && rlSlot >= 0 && rlSlot <= 3)
                        RlControlledSlots.Add(rlSlot);
            string botSlotStr = "<none>";
            if (AutoSpawnBotSlots != null && AutoSpawnBotSlots.Count > 0)
            {
                var sb = new StringBuilder();
                for (int j = 0; j < AutoSpawnBotSlots.Count; j++)
                {
                    if (j > 0) sb.Append(",");
                    sb.Append(AutoSpawnBotSlots[j]);
                }
                botSlotStr = sb.ToString();
            }
            Log.LogInfo($"Config: BindPort={BindPort} BridgePort={BridgePort} InitialScene={InitialScene} Verbose={Verbose} RoundEndDelay={RoundEndDelaySec:0.0}s NextMatchDelay={NextMatchDelaySec:0.0}s RoundMinPlay={RoundMinPlaySec:0.0}s PreCombatGrace={OraclePreCombatGraceSec:0.0}s TimeScale={TrainTimeScale:0.0}x AutoSpawnBotSlots={botSlotStr}");
        }

        // Harmony postfix on NetworkSocketServer ctor. The stock ctor sets
        // Server = new NetServer(config{Port = 1337}). We can't easily unwind
        // that, but Lidgren NetServer hasn't been Start()ed yet at this point —
        // we can mutate its Configuration.Port before Init() is called.
        private static void PatchServerPort(object __instance)
        {
            try
            {
                var serverProp = AccessTools.Property(__instance.GetType(), "Server");
                if ((object)serverProp == null) return;
                var netServer = serverProp.GetValue(__instance, null);
                if ((object)netServer == null) return;
                var configProp = AccessTools.Property(netServer.GetType(), "Configuration");
                if ((object)configProp == null) return;
                var config = configProp.GetValue(netServer, null);
                if ((object)config == null) return;
                var portProp = AccessTools.Property(config.GetType(), "Port");
                if ((object)portProp == null) return;
                portProp.SetValue(config, BindPort, null);
                Log.LogInfo($"NetworkSocketServer ctor postfix: rewrote Port → {BindPort}.");
            }
            catch (Exception e)
            {
                Log.LogError($"PatchServerPort threw: {e}");
            }
        }

    }

    // Phase 6.22 — log listener that tees every BepInEx log line to a
    // per-lobby file. Lets multiple oracles share the same install without
    // their plugin logs trampling each other in BepInEx/LogOutput.log.
    // Catches lines from ALL sources (SFHeadlessHost, BepInEx itself, any
    // other plugin), so the per-lobby file is a superset of LogOutput.
    //
    // 2026-05-23 fix: removed `lock (_lock)` — the C# compiler emits
    // `Monitor.Enter(obj, ref bool)` (2-arg) which SF's old Mono 2.0
    // runtime DOESN'T HAVE. The MissingMethodException was caught and
    // re-logged, hitting our listener again, recursively, dumping 400MB
    // of log per oracle in ~10 minutes. Replaced with a ThreadStatic
    // re-entry guard + no locking. BepInEx log events come from the
    // Unity main thread; concurrent writes are not a real concern here.
    // The re-entry guard means even if WriteLine itself throws, the
    // listener immediately returns instead of recursing.
    internal class PerLobbyLogListener : BepInEx.Logging.ILogListener
    {
        private readonly System.IO.StreamWriter _writer;
        [System.ThreadStatic] private static bool _reentryGuard;

        // Spam suppression (2026-06-06 freeze fix). The headless game throws a
        // few benign exceptions EVERY FRAME — Steamworks-not-init from leftover
        // P2P paths, OnlineRoom.CheckSides NullRef on GetComponentInChildren<
        // Torso>(), and the "IsHost before inside lobby" warning — each as a
        // multi-line message+stacktrace blob. Unfiltered this wrote ~40 MB/s
        // per oracle; the fleet-wide dirty-page writeback storm froze the box.
        // The IsP2PPacketAvailable source fix kills the biggest source; this is
        // the belt-and-suspenders that drops any remaining known per-frame
        // blob and emits one rolled-up summary every few seconds so the spam
        // stays visible without ever flooding disk again.
        private long _suppressed;
        private DateTime _lastSummaryUtc = DateTime.UtcNow;
        private static readonly TimeSpan SummaryEvery = TimeSpan.FromSeconds(10);
        private static readonly string[] _benignNeedles =
        {
            "Steamworks is not initialized",
            "TestIfAvailableClient",
            "IsP2PPacketAvailable",
            "ListenForPackages",
            "SyncableObjectManager.",
            "OnlineRoom.CheckSides",
            "OnlineRoom.Update",
            "GetComponentInChildren",
            "You should not call IsHost before",
        };

        private static bool IsBenignSpam(string data)
        {
            if (string.IsNullOrEmpty(data)) return false;
            for (int i = 0; i < _benignNeedles.Length; i++)
                if (data.IndexOf(_benignNeedles[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public PerLobbyLogListener(string path)
        {
            // Append mode so a restart doesn't wipe history. Truncate is
            // handled by the launcher (which deletes stale files itself).
            var fs = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read);
            _writer = new System.IO.StreamWriter(fs) { AutoFlush = true };
            _writer.WriteLine($"--- per-lobby log opened {DateTime.UtcNow:O} ---");
        }

        public void LogEvent(object sender, BepInEx.Logging.LogEventArgs eventArgs)
        {
            if (_reentryGuard) return;
            _reentryGuard = true;
            try
            {
                var data = eventArgs.Data == null ? null : eventArgs.Data.ToString();
                if (IsBenignSpam(data))
                {
                    _suppressed++;
                    var now = DateTime.UtcNow;
                    var elapsed = now - _lastSummaryUtc;
                    if (elapsed >= SummaryEvery)
                    {
                        _writer.WriteLine($"[Warning:SFHeadlessHost] suppressed {_suppressed} benign per-frame Unity-log spam lines in last {(int)elapsed.TotalSeconds}s (Steamworks-not-init / OnlineRoom NRE)");
                        _suppressed = 0;
                        _lastSummaryUtc = now;
                    }
                    return;
                }
                _writer.WriteLine($"[{eventArgs.Level,-7}:{eventArgs.Source.SourceName}] {eventArgs.Data}");
            }
            catch { /* never let logging crash the plugin OR recurse */ }
            finally { _reentryGuard = false; }
        }

        public void Dispose()
        {
            try { _writer.Dispose(); } catch { }
        }
    }
}
