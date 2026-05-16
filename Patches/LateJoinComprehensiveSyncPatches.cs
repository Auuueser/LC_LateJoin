using GameNetcodeStuff;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LC_LateJoin.Patches;

internal static class LateJoinComprehensiveSyncManager
{
    private const int ExtendedProtocolVersion = 2;
    private const int BufferSize = 2097152;
    private const string StandardSnapshotRequestMessage = "LC_LateJoin.SnapshotRequest";
    private const string ExtendedSnapshotRequestMessage = "LC_LateJoin.ExtendedSnapshotRequest";
    private const string ExtendedSnapshotMessage = "LC_LateJoin.ExtendedSnapshot";
    private const string ExtendedSnapshotAckMessage = "LC_LateJoin.ExtendedSnapshotAck";

    private const int FieldKindBool = 1;
    private const int FieldKindInt = 2;
    private const int FieldKindFloat = 3;
    private const int FieldKindVector3 = 4;
    private const int FieldKindString = 5;

    private static NetworkManager registeredManager;
    private static bool messagesRegistered;
    private static bool postGenerationSetupDone;
    private static bool postGenerationSetupRunning;
    private static int nextExtendedSnapshotSequence;
    private static int lastAppliedExtendedSnapshotSequence = -1;
    private static ExtendedSnapshotData pendingSnapshot;
    private static Coroutine pendingApplyCoroutine;
    private static readonly HashSet<ulong> approvedLateJoinClients = new HashSet<ulong>();
    private static readonly HashSet<ulong> pendingAckClients = new HashSet<ulong>();
    private static readonly Dictionary<ulong, float> lastExtendedSnapshotRequest = new Dictionary<ulong, float>();

    private static readonly ReflectedSyncSpec[] ReflectedSpecs =
    {
        new ReflectedSyncSpec(
            1,
            "Landmine",
            new[] { "mineActivated", "hasExploded", "sendingExplosionRPC", "localPlayerOnMine" },
            Array.Empty<string>(),
            new[] { "pressMineDebounceTimer" },
            Array.Empty<string>(),
            Array.Empty<string>()),

        new ReflectedSyncSpec(
            2,
            "Turret",
            new[] { "turretActive", "rotatingOnInterval", "rotatingRight", "hasLineOfSight", "wasTargetingPlayerLastFrame", "rotatingSmoothly", "rotatingClockwise", "enteringBerserkMode", "targetingDeadPlayer" },
            new[] { "turretMode", "turretModeLastFrame", "wallAndPlayerMask" },
            new[] { "targetRotation", "rotationSpeed", "rotationRange", "currentRotation", "switchRotationTimer", "lostLOSTimer", "turretInterval", "berserkTimer" },
            Array.Empty<string>(),
            Array.Empty<string>()),

        new ReflectedSyncSpec(
            3,
            "SteamValveHazard",
            new[] { "valveHasBurst", "valveHasCracked", "valveHasBeenRepaired" },
            Array.Empty<string>(),
            new[] { "valveCrackTime", "valveBurstTime", "fogSizeMultiplier", "currentFogSize" },
            Array.Empty<string>(),
            Array.Empty<string>()),

        new ReflectedSyncSpec(
            4,
            "SpikeRoofTrap",
            new[] { "slammingDown", "trapActive", "slamOnIntervals" },
            Array.Empty<string>(),
            new[] { "timeSinceMovingUp", "slamInterval" },
            Array.Empty<string>(),
            Array.Empty<string>()),

        new ReflectedSyncSpec(
            5,
            "MineshaftElevatorController",
            new[] { "elevatorFinishedMoving", "elevatorIsAtBottom", "elevatorCalled", "elevatorMovingDown", "movingDownLastFrame", "calledDown", "elevatorDoorOpen", "playMusic", "startedMusic" },
            Array.Empty<string>(),
            new[] { "elevatorFinishTimer", "callCooldown", "stopPlayingMusicTimer" },
            new[] { "previousElevatorPosition" },
            Array.Empty<string>()),

        new ReflectedSyncSpec(
            6,
            "EntranceTeleport",
            new[] { "isEntranceToBuilding", "enemyNearLastCheck", "gotExitPoint", "checkedForFirstTime", "exitPointDoesntExist", "playingCreakAudio" },
            new[] { "entranceId", "audioReverbPreset" },
            new[] { "checkForEnemiesInterval", "timeAtLastUse" },
            Array.Empty<string>(),
            Array.Empty<string>()),

        new ReflectedSyncSpec(
            7,
            "TerminalAccessibleObject",
            new[] { "inCooldown", "setCodeRandomlyFromRoundManager", "isBigDoor", "initializedValues", "playerHitDoorTrigger", "isDoorOpen", "isPoweredOn" },
            new[] { "rows", "columns" },
            new[] { "codeAccessCooldownTimer", "currentCooldownTimer" },
            Array.Empty<string>(),
            new[] { "objectCode" }),

        new ReflectedSyncSpec(
            8,
            "VehicleController",
            new[]
            {
                "ignitionStarted", "carDestroyed", "carEngine1AudioActive", "carEngine2AudioActive", "carRollingAudioActive",
                "localPlayerInControl", "localPlayerInPassengerSeat", "drivePedalPressed", "brakePedalPressed",
                "enabledCollisionForAllPlayers", "keyIsInDriverHand", "keyIsInIgnition", "windshieldBroken", "useVel",
                "radioOn", "radioTurnedOnBefore", "isHoodOnFire", "carHoodOpen", "hoodPoppedUp", "magnetedToShip",
                "finishedMagneting", "loadedVehicleFromSave", "destroyNextFrame", "backLightsOn", "honkingHorn"
            },
            new[] { "vehicleID", "baseCarHP", "carHP", "gear", "audio1Type", "audio2Type", "currentRadioClip", "movingAverageLength", "averageCount", "decalIndex" },
            new[]
            {
                "stability", "speed", "EngineTorque", "MaxEngineRPM", "MinEngineRPM", "EngineRPM", "steeringInput",
                "steeringWheelTurnSpeed", "carAcceleration", "carMaxSpeed", "brakeSpeed", "idleSpeed", "carFragility",
                "minimalBumpForce", "mediumBumpForce", "maximumBumpForce", "timeAtLastDamage", "turbulenceAmount",
                "gearStickAnimValue", "steeringAnimValue", "steeringWheelAnimFloat", "carStress", "carStressChange",
                "engineIntensityPercentage", "syncSpeedMultiplier", "syncRotationSpeed", "syncCarPositionInterval",
                "chanceToStartIgnition", "timeAtLastGearShift", "audio1Time", "audio2Time", "radioSignalQuality",
                "radioSignalDecreaseThreshold", "radioSignalTurbulence", "changeRadioSignalTime", "currentSongTime",
                "carHitPlayerForceFraction", "carReactToPlayerHitMultiplier", "magnetTime", "magnetRotationTime", "stressPerSecond",
                "timeSinceSpringingDriverSeat", "springForce"
            },
            new[] { "syncedPosition", "positionOffset", "rotationOffset", "truckVelocityLastFrame", "averageVelocity", "magnetTargetPosition", "magnetStartPosition", "averageVelocityAtMagnetStart" },
            Array.Empty<string>())
    };

    public static void EnsureNetworkHandlersRegistered()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || manager.CustomMessagingManager == null)
        {
            return;
        }

        if (messagesRegistered && registeredManager == manager)
        {
            return;
        }

        UnregisterMessages();
        registeredManager = manager;
        registeredManager.CustomMessagingManager.RegisterNamedMessageHandler(ExtendedSnapshotRequestMessage, OnExtendedSnapshotRequest);
        registeredManager.CustomMessagingManager.RegisterNamedMessageHandler(ExtendedSnapshotMessage, OnExtendedSnapshot);
        registeredManager.CustomMessagingManager.RegisterNamedMessageHandler(ExtendedSnapshotAckMessage, OnExtendedSnapshotAck);
        messagesRegistered = true;
        Plugin.Log.LogInfo("Registered comprehensive late-join sync message handlers");
    }

    public static void Shutdown()
    {
        UnregisterMessages();
        registeredManager = null;
        messagesRegistered = false;
        postGenerationSetupDone = false;
        postGenerationSetupRunning = false;
        nextExtendedSnapshotSequence = 0;
        lastAppliedExtendedSnapshotSequence = -1;
        pendingSnapshot = null;
        pendingApplyCoroutine = null;
        approvedLateJoinClients.Clear();
        pendingAckClients.Clear();
        lastExtendedSnapshotRequest.Clear();
    }

    public static void TrackApprovedLateJoinClient(ulong clientId)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            approvedLateJoinClients.Add(clientId);
            pendingAckClients.Add(clientId);
        }
    }

    public static void HandleServerClientConnected(StartOfRound round, ulong clientId)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer || round == null)
        {
            return;
        }

        EnsureNetworkHandlersRegistered();
        if (clientId == NetworkManager.Singleton.LocalClientId)
        {
            return;
        }

        if (approvedLateJoinClients.Contains(clientId) || IsStableLandedForLateJoin(out _))
        {
            approvedLateJoinClients.Add(clientId);
            pendingAckClients.Add(clientId);
        }
    }

    public static void HandleServerClientDisconnected(ulong clientId)
    {
        approvedLateJoinClients.Remove(clientId);
        pendingAckClients.Remove(clientId);
        lastExtendedSnapshotRequest.Remove(clientId);
    }

    public static bool IsStableLandedForLateJoin(out string reason)
    {
        StartOfRound round = StartOfRound.Instance;
        GameNetworkManager gameNetworkManager = GameNetworkManager.Instance;
        if (round == null || gameNetworkManager == null)
        {
            reason = "round or network manager is missing";
            return false;
        }

        if (round.allPlayerScripts == null || round.connectedPlayersAmount + 1 >= round.allPlayerScripts.Length)
        {
            reason = $"lobby is full or player scripts are missing; connected={round.connectedPlayersAmount + 1}";
            return false;
        }

        if (!gameNetworkManager.gameHasStarted)
        {
            reason = "pre-game lobby";
            return true;
        }

        if (round.inShipPhase)
        {
            reason = "round is still in ship/loading phase";
            return false;
        }

        if (round.shipIsLeaving)
        {
            reason = "ship is leaving";
            return false;
        }

        if (round.beganLoadingNewLevel || round.newGameIsLoading)
        {
            reason = "new level is loading";
            return false;
        }

        if (!round.shipHasLanded || !round.shipDoorsEnabled)
        {
            reason = $"ship has not fully landed; shipHasLanded={round.shipHasLanded}, shipDoorsEnabled={round.shipDoorsEnabled}";
            return false;
        }

        RoundManager roundManager = RoundManager.Instance;
        if (roundManager == null || !roundManager.dungeonFinishedGeneratingForAllPlayers)
        {
            reason = $"dungeon is not finalized for all players; dungeonFinishedGeneratingForAllPlayers={roundManager != null && roundManager.dungeonFinishedGeneratingForAllPlayers}";
            return false;
        }

        reason = "stable landed level is ready";
        return true;
    }

    public static void ResetClientPostGenerationSetup()
    {
        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
        {
            postGenerationSetupDone = false;
            postGenerationSetupRunning = false;
            pendingSnapshot = null;
            lastAppliedExtendedSnapshotSequence = -1;
        }
    }

    public static bool InterceptWorldSnapshotRequest()
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
        {
            return true;
        }

        EnsureNetworkHandlersRegistered();
        if (!postGenerationSetupDone)
        {
            if (!postGenerationSetupRunning)
            {
                StartCoroutine(RunPostGenerationSetupThenRequestSnapshots());
            }

            return false;
        }

        RequestExtendedSnapshot();
        return true;
    }

    public static void RequestExtendedSnapshot()
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer || NetworkManager.Singleton.CustomMessagingManager == null)
        {
            return;
        }

        try
        {
            using FastBufferWriter writer = new FastBufferWriter(16, Allocator.Temp);
            WriteInt(writer, ExtendedProtocolVersion);
            WriteInt(writer, StartOfRound.Instance != null ? StartOfRound.Instance.currentLevelID : -1);
            WriteInt(writer, StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed : 0);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(ExtendedSnapshotRequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            Plugin.Log.LogInfo("Requested comprehensive late-join snapshot from host");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to request comprehensive late-join snapshot: {e}");
        }
    }

    private static void UnregisterMessages()
    {
        if (registeredManager == null || registeredManager.CustomMessagingManager == null || !messagesRegistered)
        {
            return;
        }

        try
        {
            registeredManager.CustomMessagingManager.UnregisterNamedMessageHandler(ExtendedSnapshotRequestMessage);
            registeredManager.CustomMessagingManager.UnregisterNamedMessageHandler(ExtendedSnapshotMessage);
            registeredManager.CustomMessagingManager.UnregisterNamedMessageHandler(ExtendedSnapshotAckMessage);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Comprehensive late-join unregister skipped: {e.Message}");
        }
    }

    private static IEnumerator RunPostGenerationSetupThenRequestSnapshots()
    {
        postGenerationSetupRunning = true;
        float start = Time.realtimeSinceStartup;
        while ((StartOfRound.Instance == null || RoundManager.Instance == null || GameNetworkManager.Instance == null)
            && Time.realtimeSinceStartup - start < 20f)
        {
            yield return null;
        }

        RoundManager roundManager = RoundManager.Instance;
        StartOfRound round = StartOfRound.Instance;
        if (roundManager != null && round != null)
        {
            InvokeIfExists(roundManager, "RefreshLightsList");
            InvokeIfExists(roundManager, "SetLevelObjectVariables");
            InvokeIfExists(roundManager, "ResetEnemySpawningVariables");
            InvokeIfExists(roundManager, "ResetEnemyTypesSpawnedCounts");
            InvokeIfExists(roundManager, "RefreshEnemiesList");
            InvokeIfExists(roundManager, "PredictAllOutsideEnemies");
            InvokeIfExists(roundManager, "SetExitIDs");
            InvokeIfExists(roundManager, "SetBigDoorCodes");
            InvokeIfExists(roundManager, "SetLockedDoors");
            InvokeIfExists(roundManager, "SetSteamValveTimes");
            InvokeIfExists(roundManager, "SetPowerOffAtStart");

            round.beganLoadingNewLevel = false;
            round.newGameIsLoading = false;
        }

        start = Time.realtimeSinceStartup;
        while (!LocalGeneratedObjectsReady() && Time.realtimeSinceStartup - start < 8f)
        {
            yield return null;
        }

        postGenerationSetupDone = true;
        postGenerationSetupRunning = false;
        SendStandardSnapshotRequestDirectly();
        RequestExtendedSnapshot();
    }

    private static bool LocalGeneratedObjectsReady()
    {
        if (RoundManager.Instance == null || StartOfRound.Instance == null)
        {
            return false;
        }

        if (RoundManager.Instance.currentLevel != null
            && RoundManager.Instance.currentLevel.spawnEnemiesAndScrap
            && !RoundManager.Instance.dungeonCompletedGenerating)
        {
            return false;
        }

        return UnityEngine.Object.FindObjectOfType<EntranceTeleport>() != null
            || Time.realtimeSinceStartup > 0f;
    }

    private static void SendStandardSnapshotRequestDirectly()
    {
        try
        {
            using FastBufferWriter writer = new FastBufferWriter(4, Allocator.Temp);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(StandardSnapshotRequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            Plugin.Log.LogInfo("Requested standard landed world snapshot after comprehensive setup");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to request standard landed world snapshot after comprehensive setup: {e}");
        }
    }

    private static void OnExtendedSnapshotRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            return;
        }

        try
        {
            int protocolVersion = ReadInt(ref reader);
            int levelId = ReadInt(ref reader);
            int seed = ReadInt(ref reader);
            if (protocolVersion != ExtendedProtocolVersion)
            {
                Plugin.Log.LogWarning($"Ignoring comprehensive snapshot request from {senderClientId} with protocol {protocolVersion}; expected {ExtendedProtocolVersion}");
                return;
            }

            StartOfRound round = StartOfRound.Instance;
            if (round != null && levelId >= 0 && (round.currentLevelID != levelId || round.randomMapSeed != seed))
            {
                Plugin.Log.LogWarning($"Ignoring comprehensive snapshot request from {senderClientId} for level={levelId}, seed={seed}; host level={round.currentLevelID}, seed={round.randomMapSeed}");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Comprehensive snapshot request header read skipped for {senderClientId}: {e.Message}");
        }

        if (!approvedLateJoinClients.Contains(senderClientId) && !IsStableLandedForLateJoin(out _))
        {
            return;
        }

        if (lastExtendedSnapshotRequest.TryGetValue(senderClientId, out float lastRequest)
            && Time.realtimeSinceStartup - lastRequest < 0.75f)
        {
            return;
        }

        lastExtendedSnapshotRequest[senderClientId] = Time.realtimeSinceStartup;
        pendingAckClients.Add(senderClientId);
        StartCoroutine(SendExtendedSnapshotOnNextFrame(senderClientId));
    }

    private static IEnumerator SendExtendedSnapshotOnNextFrame(ulong clientId)
    {
        yield return null;
        SendExtendedSnapshot(clientId);
        yield return new WaitForSeconds(2f);

        if (NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsServer
            && NetworkManager.Singleton.ConnectedClientsIds.Contains(clientId)
            && pendingAckClients.Contains(clientId))
        {
            SendExtendedSnapshot(clientId);
        }
    }

    private static void SendExtendedSnapshot(ulong clientId)
    {
        try
        {
            using FastBufferWriter writer = new FastBufferWriter(BufferSize, Allocator.Temp);
            ExtendedSnapshotData snapshot = BuildExtendedSnapshot(++nextExtendedSnapshotSequence);
            WriteExtendedSnapshot(writer, snapshot);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(ExtendedSnapshotMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            Plugin.Log.LogInfo($"Sent comprehensive late-join snapshot {snapshot.Sequence} to client {clientId}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to send comprehensive late-join snapshot to client {clientId}: {e}");
        }
    }

    private static void OnExtendedSnapshot(ulong senderClientId, FastBufferReader reader)
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
        {
            return;
        }

        try
        {
            ExtendedSnapshotData snapshot = ReadExtendedSnapshot(ref reader);
            if (!ValidateIncomingSnapshot(snapshot))
            {
                return;
            }

            if (!AreRequiredObjectsReady(snapshot))
            {
                pendingSnapshot = snapshot;
                if (pendingApplyCoroutine == null)
                {
                    pendingApplyCoroutine = StartCoroutine(ApplyPendingSnapshotWhenReady(10f));
                }
                return;
            }

            ApplyExtendedSnapshot(snapshot);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to apply comprehensive late-join snapshot: {e}");
        }
    }

    private static void OnExtendedSnapshotAck(ulong senderClientId, FastBufferReader reader)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            return;
        }

        try
        {
            int protocolVersion = ReadInt(ref reader);
            int sequence = ReadInt(ref reader);
            if (protocolVersion == ExtendedProtocolVersion)
            {
                pendingAckClients.Remove(senderClientId);
                approvedLateJoinClients.Remove(senderClientId);
                Plugin.Log.LogInfo($"Received comprehensive late-join snapshot ack {sequence} from client {senderClientId}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Comprehensive late-join snapshot ack read skipped for {senderClientId}: {e.Message}");
        }
    }

    private static IEnumerator ApplyPendingSnapshotWhenReady(float timeout)
    {
        float start = Time.realtimeSinceStartup;
        while (pendingSnapshot != null && !AreRequiredObjectsReady(pendingSnapshot) && Time.realtimeSinceStartup - start < timeout)
        {
            RequestExtendedSnapshot();
            yield return new WaitForSeconds(0.5f);
        }

        ExtendedSnapshotData snapshot = pendingSnapshot;
        pendingSnapshot = null;
        pendingApplyCoroutine = null;
        if (snapshot != null && ValidateIncomingSnapshot(snapshot))
        {
            ApplyExtendedSnapshot(snapshot);
        }
    }

    private static ExtendedSnapshotData BuildExtendedSnapshot(int sequence)
    {
        StartOfRound round = StartOfRound.Instance;
        ExtendedSnapshotData snapshot = new ExtendedSnapshotData
        {
            Sequence = sequence,
            LevelId = round != null ? round.currentLevelID : -1,
            RandomSeed = round != null ? round.randomMapSeed : 0,
            ShipHasLanded = round != null && round.shipHasLanded,
            ShipIsLeaving = round != null && round.shipIsLeaving,
            BeganLoadingNewLevel = round != null && round.beganLoadingNewLevel,
            NewGameIsLoading = round != null && round.newGameIsLoading
        };

        snapshot.AuthoritativeItemIds.AddRange(FindSpawnedNetworkIds<GrabbableObject>());
        snapshot.AuthoritativeEnemyIds.AddRange(FindSpawnedNetworkIds<EnemyAI>());
        snapshot.HeldItems.AddRange(BuildHeldItemStates(round));
        snapshot.PlayerStates.AddRange(BuildPlayerExtendedStates(round));
        snapshot.ReflectedObjects.AddRange(BuildReflectedObjectStates());
        return snapshot;
    }

    private static IEnumerable<ulong> FindSpawnedNetworkIds<T>() where T : Component
    {
        T[] objects = UnityEngine.Object.FindObjectsOfType<T>();
        for (int i = 0; i < objects.Length; i++)
        {
            T component = objects[i];
            if (component == null)
            {
                continue;
            }

            NetworkObject networkObject = component.GetComponent<NetworkObject>();
            if (networkObject != null && networkObject.IsSpawned)
            {
                yield return networkObject.NetworkObjectId;
            }
        }
    }

    private static IEnumerable<HeldItemState> BuildHeldItemStates(StartOfRound round)
    {
        if (round == null || round.allPlayerScripts == null)
        {
            yield break;
        }

        for (int playerIndex = 0; playerIndex < round.allPlayerScripts.Length; playerIndex++)
        {
            PlayerControllerB player = round.allPlayerScripts[playerIndex];
            if (player == null)
            {
                continue;
            }

            if (player.ItemOnlySlot != null && TryGetNetworkId(player.ItemOnlySlot, out ulong itemOnlyId))
            {
                yield return new HeldItemState
                {
                    NetworkId = itemOnlyId,
                    PlayerIndex = playerIndex,
                    Slot = 50,
                    IsPocketed = player.ItemOnlySlot.isPocketed,
                    IsActiveHeld = false,
                    IsItemOnlySlot = true
                };
            }

            if (player.ItemSlots == null)
            {
                continue;
            }

            for (int slot = 0; slot < player.ItemSlots.Length; slot++)
            {
                GrabbableObject item = player.ItemSlots[slot];
                if (item == null || !TryGetNetworkId(item, out ulong itemId))
                {
                    continue;
                }

                yield return new HeldItemState
                {
                    NetworkId = itemId,
                    PlayerIndex = playerIndex,
                    Slot = slot,
                    IsPocketed = item.isPocketed,
                    IsActiveHeld = player.currentlyHeldObjectServer == item && !item.isPocketed,
                    IsItemOnlySlot = false
                };
            }
        }
    }

    private static IEnumerable<PlayerExtendedState> BuildPlayerExtendedStates(StartOfRound round)
    {
        if (round == null || round.allPlayerScripts == null)
        {
            yield break;
        }

        for (int i = 0; i < round.allPlayerScripts.Length; i++)
        {
            PlayerControllerB player = round.allPlayerScripts[i];
            if (player == null)
            {
                continue;
            }

            yield return new PlayerExtendedState
            {
                PlayerIndex = i,
                ActualClientId = player.actualClientId,
                PlayerClientId = player.playerClientId,
                IsPlayerControlled = player.isPlayerControlled,
                IsPlayerDead = player.isPlayerDead,
                DisconnectedMidGame = player.disconnectedMidGame,
                IsInsideFactory = GetBoolField(player, "isInsideFactory"),
                CauseOfDeath = GetIntField(player, "causeOfDeath"),
                SpectatedPlayerIndex = GetPlayerIndex(round, GetObjectField(player, "spectatedPlayerScript") as PlayerControllerB)
            };
        }
    }

    private static IEnumerable<ReflectedObjectState> BuildReflectedObjectStates()
    {
        for (int specIndex = 0; specIndex < ReflectedSpecs.Length; specIndex++)
        {
            ReflectedSyncSpec spec = ReflectedSpecs[specIndex];
            foreach (UnityEngine.Object obj in FindObjectsByTypeName(spec.TypeName))
            {
                if (obj == null || !TryGetNetworkObject(obj, out NetworkObject networkObject) || networkObject == null || !networkObject.IsSpawned)
                {
                    continue;
                }

                Component component = obj as Component;
                Transform transform = component != null ? component.transform : null;
                ReflectedObjectState state = new ReflectedObjectState
                {
                    Kind = spec.Kind,
                    NetworkId = networkObject.NetworkObjectId,
                    Position = transform != null ? transform.position : Vector3.zero,
                    Euler = transform != null ? transform.eulerAngles : Vector3.zero
                };

                AppendFieldValues(obj, spec, state.Values);
                yield return state;
            }
        }
    }

    private static void AppendFieldValues(UnityEngine.Object obj, ReflectedSyncSpec spec, List<FieldValue> values)
    {
        AppendBoolFields(obj, spec.BoolFields, values);
        AppendIntFields(obj, spec.IntFields, values, spec.BoolFields.Length);
        AppendFloatFields(obj, spec.FloatFields, values, spec.BoolFields.Length + spec.IntFields.Length);
        AppendVector3Fields(obj, spec.Vector3Fields, values, spec.BoolFields.Length + spec.IntFields.Length + spec.FloatFields.Length);
        AppendStringFields(obj, spec.StringFields, values, spec.BoolFields.Length + spec.IntFields.Length + spec.FloatFields.Length + spec.Vector3Fields.Length);
    }

    private static void AppendBoolFields(object obj, string[] fields, List<FieldValue> values)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (TryGetField(obj, fields[i], out object value) && value is bool boolValue)
            {
                values.Add(new FieldValue { FieldIndex = i, Kind = FieldKindBool, BoolValue = boolValue });
            }
        }
    }

    private static void AppendIntFields(object obj, string[] fields, List<FieldValue> values, int offset)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (!TryGetField(obj, fields[i], out object value) || value == null)
            {
                continue;
            }

            try
            {
                values.Add(new FieldValue { FieldIndex = offset + i, Kind = FieldKindInt, IntValue = Convert.ToInt32(value) });
            }
            catch
            {
                // Enum/int conversion failure: skip field instead of breaking the whole snapshot.
            }
        }
    }

    private static void AppendFloatFields(object obj, string[] fields, List<FieldValue> values, int offset)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (!TryGetField(obj, fields[i], out object value) || value == null)
            {
                continue;
            }

            try
            {
                values.Add(new FieldValue { FieldIndex = offset + i, Kind = FieldKindFloat, FloatValue = Convert.ToSingle(value) });
            }
            catch
            {
                // Skip non-float-compatible field.
            }
        }
    }

    private static void AppendVector3Fields(object obj, string[] fields, List<FieldValue> values, int offset)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (TryGetField(obj, fields[i], out object value) && value is Vector3 vectorValue)
            {
                values.Add(new FieldValue { FieldIndex = offset + i, Kind = FieldKindVector3, Vector3Value = vectorValue });
            }
        }
    }

    private static void AppendStringFields(object obj, string[] fields, List<FieldValue> values, int offset)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (TryGetField(obj, fields[i], out object value))
            {
                values.Add(new FieldValue { FieldIndex = offset + i, Kind = FieldKindString, StringValue = value as string ?? string.Empty });
            }
        }
    }

    private static void WriteExtendedSnapshot(FastBufferWriter writer, ExtendedSnapshotData snapshot)
    {
        WriteInt(writer, ExtendedProtocolVersion);
        WriteInt(writer, snapshot.Sequence);
        WriteInt(writer, snapshot.LevelId);
        WriteInt(writer, snapshot.RandomSeed);
        WriteBool(writer, snapshot.ShipHasLanded);
        WriteBool(writer, snapshot.ShipIsLeaving);
        WriteBool(writer, snapshot.BeganLoadingNewLevel);
        WriteBool(writer, snapshot.NewGameIsLoading);

        WriteULongList(writer, snapshot.AuthoritativeItemIds);
        WriteULongList(writer, snapshot.AuthoritativeEnemyIds);

        WriteInt(writer, snapshot.PlayerStates.Count);
        for (int i = 0; i < snapshot.PlayerStates.Count; i++)
        {
            PlayerExtendedState state = snapshot.PlayerStates[i];
            WriteInt(writer, state.PlayerIndex);
            WriteULong(writer, state.ActualClientId);
            WriteULong(writer, state.PlayerClientId);
            WriteBool(writer, state.IsPlayerControlled);
            WriteBool(writer, state.IsPlayerDead);
            WriteBool(writer, state.DisconnectedMidGame);
            WriteBool(writer, state.IsInsideFactory);
            WriteInt(writer, state.CauseOfDeath);
            WriteInt(writer, state.SpectatedPlayerIndex);
        }

        WriteInt(writer, snapshot.HeldItems.Count);
        for (int i = 0; i < snapshot.HeldItems.Count; i++)
        {
            HeldItemState state = snapshot.HeldItems[i];
            WriteULong(writer, state.NetworkId);
            WriteInt(writer, state.PlayerIndex);
            WriteInt(writer, state.Slot);
            WriteBool(writer, state.IsPocketed);
            WriteBool(writer, state.IsActiveHeld);
            WriteBool(writer, state.IsItemOnlySlot);
        }

        WriteInt(writer, snapshot.ReflectedObjects.Count);
        for (int i = 0; i < snapshot.ReflectedObjects.Count; i++)
        {
            ReflectedObjectState state = snapshot.ReflectedObjects[i];
            WriteInt(writer, state.Kind);
            WriteULong(writer, state.NetworkId);
            WriteVector3(writer, state.Position);
            WriteVector3(writer, state.Euler);
            WriteInt(writer, state.Values.Count);
            for (int v = 0; v < state.Values.Count; v++)
            {
                WriteFieldValue(writer, state.Values[v]);
            }
        }
    }

    private static ExtendedSnapshotData ReadExtendedSnapshot(ref FastBufferReader reader)
    {
        int protocolVersion = ReadInt(ref reader);
        if (protocolVersion != ExtendedProtocolVersion)
        {
            throw new InvalidOperationException($"Comprehensive snapshot protocol mismatch: got {protocolVersion}, expected {ExtendedProtocolVersion}");
        }

        ExtendedSnapshotData snapshot = new ExtendedSnapshotData
        {
            Sequence = ReadInt(ref reader),
            LevelId = ReadInt(ref reader),
            RandomSeed = ReadInt(ref reader),
            ShipHasLanded = ReadBool(ref reader),
            ShipIsLeaving = ReadBool(ref reader),
            BeganLoadingNewLevel = ReadBool(ref reader),
            NewGameIsLoading = ReadBool(ref reader)
        };

        snapshot.AuthoritativeItemIds.AddRange(ReadULongList(ref reader));
        snapshot.AuthoritativeEnemyIds.AddRange(ReadULongList(ref reader));

        int playerCount = ReadInt(ref reader);
        for (int i = 0; i < playerCount; i++)
        {
            snapshot.PlayerStates.Add(new PlayerExtendedState
            {
                PlayerIndex = ReadInt(ref reader),
                ActualClientId = ReadULong(ref reader),
                PlayerClientId = ReadULong(ref reader),
                IsPlayerControlled = ReadBool(ref reader),
                IsPlayerDead = ReadBool(ref reader),
                DisconnectedMidGame = ReadBool(ref reader),
                IsInsideFactory = ReadBool(ref reader),
                CauseOfDeath = ReadInt(ref reader),
                SpectatedPlayerIndex = ReadInt(ref reader)
            });
        }

        int heldCount = ReadInt(ref reader);
        for (int i = 0; i < heldCount; i++)
        {
            snapshot.HeldItems.Add(new HeldItemState
            {
                NetworkId = ReadULong(ref reader),
                PlayerIndex = ReadInt(ref reader),
                Slot = ReadInt(ref reader),
                IsPocketed = ReadBool(ref reader),
                IsActiveHeld = ReadBool(ref reader),
                IsItemOnlySlot = ReadBool(ref reader)
            });
        }

        int reflectedCount = ReadInt(ref reader);
        for (int i = 0; i < reflectedCount; i++)
        {
            ReflectedObjectState state = new ReflectedObjectState
            {
                Kind = ReadInt(ref reader),
                NetworkId = ReadULong(ref reader),
                Position = ReadVector3(ref reader),
                Euler = ReadVector3(ref reader)
            };

            int valueCount = ReadInt(ref reader);
            for (int v = 0; v < valueCount; v++)
            {
                state.Values.Add(ReadFieldValue(ref reader));
            }

            snapshot.ReflectedObjects.Add(state);
        }

        return snapshot;
    }

    private static bool ValidateIncomingSnapshot(ExtendedSnapshotData snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        if (snapshot.Sequence <= lastAppliedExtendedSnapshotSequence)
        {
            Plugin.Log.LogDebug($"Ignoring stale comprehensive late-join snapshot {snapshot.Sequence}; last applied {lastAppliedExtendedSnapshotSequence}");
            return false;
        }

        StartOfRound round = StartOfRound.Instance;
        if (round != null && snapshot.LevelId >= 0 && (round.currentLevelID != snapshot.LevelId || round.randomMapSeed != snapshot.RandomSeed))
        {
            Plugin.Log.LogWarning($"Ignoring comprehensive late-join snapshot for level={snapshot.LevelId}, seed={snapshot.RandomSeed}; local level={round.currentLevelID}, seed={round.randomMapSeed}");
            return false;
        }

        if (!snapshot.ShipHasLanded || snapshot.ShipIsLeaving || snapshot.BeganLoadingNewLevel || snapshot.NewGameIsLoading)
        {
            Plugin.Log.LogWarning($"Ignoring comprehensive late-join snapshot because host state is not stable; landed={snapshot.ShipHasLanded}, leaving={snapshot.ShipIsLeaving}, beganLoading={snapshot.BeganLoadingNewLevel}, newGameLoading={snapshot.NewGameIsLoading}");
            return false;
        }

        return true;
    }

    private static bool AreRequiredObjectsReady(ExtendedSnapshotData snapshot)
    {
        int missing = 0;
        for (int i = 0; i < snapshot.ReflectedObjects.Count; i++)
        {
            if (TryGetNetworkObject(snapshot.ReflectedObjects[i].NetworkId) == null)
            {
                missing++;
                if (missing > 16)
                {
                    return false;
                }
            }
        }

        for (int i = 0; i < snapshot.HeldItems.Count; i++)
        {
            if (TryGetNetworkObject(snapshot.HeldItems[i].NetworkId) == null)
            {
                missing++;
                if (missing > 16)
                {
                    return false;
                }
            }
        }

        return missing == 0;
    }

    private static void ApplyExtendedSnapshot(ExtendedSnapshotData snapshot)
    {
        if (!ValidateIncomingSnapshot(snapshot))
        {
            return;
        }

        lastAppliedExtendedSnapshotSequence = snapshot.Sequence;
        ReconcileAuthoritativeItems(snapshot.AuthoritativeItemIds);
        ReconcileAuthoritativeEnemies(snapshot.AuthoritativeEnemyIds);
        ApplyPlayerExtendedStates(snapshot.PlayerStates);
        ApplyReflectedObjectStates(snapshot.ReflectedObjects);
        ApplyHeldItemStates(snapshot.HeldItems);
        SendExtendedSnapshotAck(snapshot.Sequence);
        Plugin.Log.LogInfo($"Applied comprehensive late-join snapshot {snapshot.Sequence}");
    }

    private static void ReconcileAuthoritativeItems(List<ulong> authoritativeIds)
    {
        HashSet<ulong> authoritative = new HashSet<ulong>(authoritativeIds);
        GrabbableObject[] items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
        for (int i = 0; i < items.Length; i++)
        {
            GrabbableObject item = items[i];
            if (item == null || item.NetworkObject == null || !item.NetworkObject.IsSpawned)
            {
                continue;
            }

            if (authoritative.Contains(item.NetworkObject.NetworkObjectId))
            {
                continue;
            }

            if (item.isHeld || item.playerHeldBy != null)
            {
                continue;
            }

            try
            {
                item.deactivated = true;
                item.EnablePhysics(false);
                item.EnableItemMeshes(false);
                item.transform.position = new Vector3(0f, -1000f, 0f);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Local-only item reconciliation skipped for {item.name}: {e.Message}");
            }
        }
    }

    private static void ReconcileAuthoritativeEnemies(List<ulong> authoritativeIds)
    {
        HashSet<ulong> authoritative = new HashSet<ulong>(authoritativeIds);
        EnemyAI[] enemies = UnityEngine.Object.FindObjectsOfType<EnemyAI>();
        for (int i = 0; i < enemies.Length; i++)
        {
            EnemyAI enemy = enemies[i];
            if (enemy == null || enemy.NetworkObject == null || !enemy.NetworkObject.IsSpawned)
            {
                continue;
            }

            if (authoritative.Contains(enemy.NetworkObject.NetworkObjectId))
            {
                continue;
            }

            try
            {
                enemy.isEnemyDead = true;
                enemy.enemyHP = 0;
                enemy.KillEnemy(false);
            }
            catch
            {
                // Fall through to local disable.
            }

            try
            {
                enemy.gameObject.SetActive(false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Local-only enemy reconciliation skipped for {enemy.name}: {e.Message}");
            }
        }
    }

    private static void ApplyPlayerExtendedStates(List<PlayerExtendedState> states)
    {
        StartOfRound round = StartOfRound.Instance;
        if (round == null || round.allPlayerScripts == null)
        {
            return;
        }

        for (int i = 0; i < states.Count; i++)
        {
            PlayerExtendedState state = states[i];
            if (state.PlayerIndex < 0 || state.PlayerIndex >= round.allPlayerScripts.Length)
            {
                continue;
            }

            PlayerControllerB player = round.allPlayerScripts[state.PlayerIndex];
            if (player == null)
            {
                continue;
            }

            player.actualClientId = state.ActualClientId;
            player.playerClientId = state.PlayerClientId;
            player.isPlayerControlled = state.IsPlayerControlled;
            player.isPlayerDead = state.IsPlayerDead;
            player.disconnectedMidGame = state.DisconnectedMidGame;
            SetFieldIfExists(player, "isInsideFactory", state.IsInsideFactory);
            SetEnumOrIntFieldIfExists(player, "causeOfDeath", state.CauseOfDeath);
            if (state.SpectatedPlayerIndex >= 0 && state.SpectatedPlayerIndex < round.allPlayerScripts.Length)
            {
                SetFieldIfExists(player, "spectatedPlayerScript", round.allPlayerScripts[state.SpectatedPlayerIndex]);
            }
        }
    }

    private static void ApplyHeldItemStates(List<HeldItemState> states)
    {
        StartOfRound round = StartOfRound.Instance;
        if (round == null || round.allPlayerScripts == null)
        {
            return;
        }

        for (int i = 0; i < states.Count; i++)
        {
            HeldItemState state = states[i];
            if (state.PlayerIndex < 0 || state.PlayerIndex >= round.allPlayerScripts.Length)
            {
                continue;
            }

            PlayerControllerB holder = round.allPlayerScripts[state.PlayerIndex];
            NetworkObject networkObject = TryGetNetworkObject(state.NetworkId);
            if (holder == null || networkObject == null || !networkObject.TryGetComponent(out GrabbableObject item))
            {
                continue;
            }

            try
            {
                item.playerHeldBy = holder;
                item.heldByPlayerOnServer = true;
                item.isHeld = true;
                item.isPocketed = state.IsPocketed;
                item.parentObject = holder.serverItemHolder;
                item.EnablePhysics(false);
                item.EnableItemMeshes(!state.IsPocketed);

                if (holder.serverItemHolder != null)
                {
                    item.transform.SetParent(holder.serverItemHolder, false);
                    item.transform.localPosition = Vector3.zero;
                    item.transform.localRotation = Quaternion.identity;
                }

                if (state.IsItemOnlySlot || state.Slot == 50)
                {
                    holder.ItemOnlySlot = item;
                    continue;
                }

                if (holder.ItemSlots != null && state.Slot >= 0 && state.Slot < holder.ItemSlots.Length)
                {
                    holder.ItemSlots[state.Slot] = item;
                    if (state.IsActiveHeld)
                    {
                        holder.currentlyHeldObjectServer = item;
                        holder.isHoldingObject = true;
                        holder.currentItemSlot = state.Slot;
                        holder.twoHanded = item.itemProperties != null && item.itemProperties.twoHanded;
                        holder.twoHandedAnimation = item.itemProperties != null && item.itemProperties.twoHandedAnimation;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Held item late-join attach skipped for object {state.NetworkId}: {e.Message}");
            }
        }
    }

    private static void ApplyReflectedObjectStates(List<ReflectedObjectState> states)
    {
        for (int i = 0; i < states.Count; i++)
        {
            ReflectedObjectState state = states[i];
            ReflectedSyncSpec spec = GetSpec(state.Kind);
            if (spec == null)
            {
                continue;
            }

            NetworkObject networkObject = TryGetNetworkObject(state.NetworkId);
            if (networkObject == null)
            {
                continue;
            }

            UnityEngine.Object target = FindComponentForSpec(networkObject, spec);
            if (target == null)
            {
                continue;
            }

            if (target is Component component)
            {
                component.transform.position = state.Position;
                component.transform.eulerAngles = state.Euler;
            }

            ApplyFieldValues(target, spec, state.Values);
            ApplyPostFieldSideEffects(target, spec);
        }
    }

    private static void ApplyFieldValues(UnityEngine.Object target, ReflectedSyncSpec spec, List<FieldValue> values)
    {
        int boolStart = 0;
        int intStart = boolStart + spec.BoolFields.Length;
        int floatStart = intStart + spec.IntFields.Length;
        int vectorStart = floatStart + spec.FloatFields.Length;
        int stringStart = vectorStart + spec.Vector3Fields.Length;

        for (int i = 0; i < values.Count; i++)
        {
            FieldValue value = values[i];
            try
            {
                if (value.Kind == FieldKindBool && value.FieldIndex >= boolStart && value.FieldIndex < intStart)
                {
                    SetFieldIfExists(target, spec.BoolFields[value.FieldIndex - boolStart], value.BoolValue);
                }
                else if (value.Kind == FieldKindInt && value.FieldIndex >= intStart && value.FieldIndex < floatStart)
                {
                    SetEnumOrIntFieldIfExists(target, spec.IntFields[value.FieldIndex - intStart], value.IntValue);
                }
                else if (value.Kind == FieldKindFloat && value.FieldIndex >= floatStart && value.FieldIndex < vectorStart)
                {
                    SetFieldIfExists(target, spec.FloatFields[value.FieldIndex - floatStart], value.FloatValue);
                }
                else if (value.Kind == FieldKindVector3 && value.FieldIndex >= vectorStart && value.FieldIndex < stringStart)
                {
                    SetFieldIfExists(target, spec.Vector3Fields[value.FieldIndex - vectorStart], value.Vector3Value);
                }
                else if (value.Kind == FieldKindString && value.FieldIndex >= stringStart && value.FieldIndex < stringStart + spec.StringFields.Length)
                {
                    SetFieldIfExists(target, spec.StringFields[value.FieldIndex - stringStart], value.StringValue ?? string.Empty);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Reflected field apply skipped for {spec.TypeName} field index {value.FieldIndex}: {e.Message}");
            }
        }
    }

    private static void ApplyPostFieldSideEffects(UnityEngine.Object target, ReflectedSyncSpec spec)
    {
        try
        {
            if (spec.TypeName == "Landmine")
            {
                bool active = GetBoolField(target, "mineActivated");
                bool exploded = GetBoolField(target, "hasExploded");
                InvokeIfExists(target, "ToggleMineEnabledLocalClient", active && !exploded);
                if (exploded)
                {
                    Animator animator = GetObjectField(target, "mineAnimator") as Animator;
                    if (animator != null)
                    {
                        animator.SetTrigger("detonate");
                    }
                }
            }
            else if (spec.TypeName == "Turret")
            {
                InvokeIfExists(target, "ToggleTurretEnabledLocalClient", GetBoolField(target, "turretActive"));
                InvokeIfExists(target, "SwitchTurretMode", GetIntField(target, "turretMode"));
            }
            else if (spec.TypeName == "SpikeRoofTrap")
            {
                InvokeIfExists(target, "ToggleSpikesEnabledLocalClient", GetBoolField(target, "trapActive"));
            }
            else if (spec.TypeName == "SteamValveHazard")
            {
                if (GetBoolField(target, "valveHasBeenRepaired"))
                {
                    InvokeIfExists(target, "FixValveLocalClient");
                }
                else if (GetBoolField(target, "valveHasBurst"))
                {
                    InvokeIfExists(target, "BurstValve");
                }
                else if (GetBoolField(target, "valveHasCracked"))
                {
                    InvokeIfExists(target, "CrackValve");
                }
            }
            else if (spec.TypeName == "MineshaftElevatorController")
            {
                if (GetBoolField(target, "elevatorDoorOpen"))
                {
                    InvokeIfExists(target, "SetElevatorDoorOpen");
                }
                else
                {
                    InvokeIfExists(target, "SetElevatorDoorClosed");
                }
            }
            else if (spec.TypeName == "TerminalAccessibleObject")
            {
                bool powered = GetBoolField(target, "isPoweredOn");
                bool open = GetBoolField(target, "isDoorOpen");
                SetFieldIfExists(target, "isPoweredOn", true);
                InvokeIfExists(target, "InitializeValues");
                InvokeIfExists(target, "SetDoorOpen", open);
                SetFieldIfExists(target, "isPoweredOn", powered);
                SetFieldIfExists(target, "isDoorOpen", open);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Post field side-effect apply skipped for {spec.TypeName}: {e.Message}");
        }
    }

    private static void SendExtendedSnapshotAck(int sequence)
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer || NetworkManager.Singleton.CustomMessagingManager == null)
        {
            return;
        }

        try
        {
            using FastBufferWriter writer = new FastBufferWriter(16, Allocator.Temp);
            WriteInt(writer, ExtendedProtocolVersion);
            WriteInt(writer, sequence);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(ExtendedSnapshotAckMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Failed to send comprehensive snapshot ack: {e.Message}");
        }
    }

    private static Coroutine StartCoroutine(IEnumerator routine)
    {
        if (StartOfRound.Instance != null)
        {
            return StartOfRound.Instance.StartCoroutine(routine);
        }

        if (RoundManager.Instance != null)
        {
            return RoundManager.Instance.StartCoroutine(routine);
        }

        if (Plugin.Instance != null)
        {
            return Plugin.Instance.StartCoroutine(routine);
        }

        return null;
    }

    private static IEnumerable<UnityEngine.Object> FindObjectsByTypeName(string typeName)
    {
        Type type = AccessTools.TypeByName(typeName);
        if (type == null)
        {
            yield break;
        }

        UnityEngine.Object[] objects = Array.Empty<UnityEngine.Object>();
        try
        {
            objects = UnityEngine.Object.FindObjectsOfType(type);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"FindObjectsOfType({typeName}) skipped: {e.Message}");
        }

        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] != null)
            {
                yield return objects[i];
            }
        }
    }

    private static bool TryGetNetworkObject(UnityEngine.Object obj, out NetworkObject networkObject)
    {
        networkObject = null;
        if (obj is Component component)
        {
            networkObject = component.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                networkObject = component.GetComponentInParent<NetworkObject>();
            }
            return networkObject != null;
        }

        if (obj is GameObject gameObject)
        {
            networkObject = gameObject.GetComponent<NetworkObject>();
            return networkObject != null;
        }

        return false;
    }

    private static NetworkObject TryGetNetworkObject(ulong networkId)
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null)
        {
            return null;
        }

        NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkId, out NetworkObject networkObject);
        return networkObject;
    }

    private static UnityEngine.Object FindComponentForSpec(NetworkObject networkObject, ReflectedSyncSpec spec)
    {
        Type type = AccessTools.TypeByName(spec.TypeName);
        if (networkObject == null || type == null)
        {
            return null;
        }

        Component direct = networkObject.GetComponent(type);
        if (direct != null)
        {
            return direct;
        }

        Component[] children = networkObject.GetComponentsInChildren(type, true);
        return children != null && children.Length > 0 ? children[0] : null;
    }

    private static ReflectedSyncSpec GetSpec(int kind)
    {
        for (int i = 0; i < ReflectedSpecs.Length; i++)
        {
            if (ReflectedSpecs[i].Kind == kind)
            {
                return ReflectedSpecs[i];
            }
        }

        return null;
    }

    private static bool TryGetNetworkId(GrabbableObject item, out ulong networkId)
    {
        networkId = 0UL;
        if (item == null || item.NetworkObject == null || !item.NetworkObject.IsSpawned)
        {
            return false;
        }

        networkId = item.NetworkObject.NetworkObjectId;
        return true;
    }

    private static int GetPlayerIndex(StartOfRound round, PlayerControllerB player)
    {
        if (round == null || round.allPlayerScripts == null || player == null)
        {
            return -1;
        }

        for (int i = 0; i < round.allPlayerScripts.Length; i++)
        {
            if (round.allPlayerScripts[i] == player)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryGetField(object obj, string fieldName, out object value)
    {
        value = null;
        if (obj == null)
        {
            return false;
        }

        FieldInfo field = AccessTools.Field(obj.GetType(), fieldName);
        if (field == null)
        {
            return false;
        }

        value = field.GetValue(obj);
        return true;
    }

    private static bool GetBoolField(object obj, string fieldName)
    {
        return TryGetField(obj, fieldName, out object value) && value is bool boolValue && boolValue;
    }

    private static int GetIntField(object obj, string fieldName)
    {
        if (!TryGetField(obj, fieldName, out object value) || value == null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static object GetObjectField(object obj, string fieldName)
    {
        return TryGetField(obj, fieldName, out object value) ? value : null;
    }

    private static void SetFieldIfExists(object obj, string fieldName, object value)
    {
        if (obj == null)
        {
            return;
        }

        FieldInfo field = AccessTools.Field(obj.GetType(), fieldName);
        if (field == null)
        {
            return;
        }

        field.SetValue(obj, value);
    }

    private static void SetEnumOrIntFieldIfExists(object obj, string fieldName, int value)
    {
        if (obj == null)
        {
            return;
        }

        FieldInfo field = AccessTools.Field(obj.GetType(), fieldName);
        if (field == null)
        {
            return;
        }

        if (field.FieldType.IsEnum)
        {
            field.SetValue(obj, Enum.ToObject(field.FieldType, value));
        }
        else
        {
            field.SetValue(obj, Convert.ChangeType(value, field.FieldType));
        }
    }

    private static void InvokeIfExists(object instance, string methodName, params object[] args)
    {
        if (instance == null)
        {
            return;
        }

        MethodInfo method = AccessTools.Method(instance.GetType(), methodName);
        if (method == null)
        {
            return;
        }

        try
        {
            method.Invoke(instance, args);
        }
        catch (TargetParameterCountException)
        {
            // Method signature changed; skip safely.
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Invoke {instance.GetType().Name}.{methodName} skipped: {e.Message}");
        }
    }

    private static void WriteULongList(FastBufferWriter writer, List<ulong> values)
    {
        values ??= new List<ulong>();
        WriteInt(writer, values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            WriteULong(writer, values[i]);
        }
    }

    private static List<ulong> ReadULongList(ref FastBufferReader reader)
    {
        int count = Mathf.Max(0, ReadInt(ref reader));
        List<ulong> values = new List<ulong>(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(ReadULong(ref reader));
        }
        return values;
    }

    private static void WriteFieldValue(FastBufferWriter writer, FieldValue value)
    {
        WriteInt(writer, value.FieldIndex);
        WriteInt(writer, value.Kind);
        if (value.Kind == FieldKindBool)
        {
            WriteBool(writer, value.BoolValue);
        }
        else if (value.Kind == FieldKindInt)
        {
            WriteInt(writer, value.IntValue);
        }
        else if (value.Kind == FieldKindFloat)
        {
            WriteFloat(writer, value.FloatValue);
        }
        else if (value.Kind == FieldKindVector3)
        {
            WriteVector3(writer, value.Vector3Value);
        }
        else if (value.Kind == FieldKindString)
        {
            WriteString(writer, value.StringValue);
        }
    }

    private static FieldValue ReadFieldValue(ref FastBufferReader reader)
    {
        FieldValue value = new FieldValue
        {
            FieldIndex = ReadInt(ref reader),
            Kind = ReadInt(ref reader)
        };

        if (value.Kind == FieldKindBool)
        {
            value.BoolValue = ReadBool(ref reader);
        }
        else if (value.Kind == FieldKindInt)
        {
            value.IntValue = ReadInt(ref reader);
        }
        else if (value.Kind == FieldKindFloat)
        {
            value.FloatValue = ReadFloat(ref reader);
        }
        else if (value.Kind == FieldKindVector3)
        {
            value.Vector3Value = ReadVector3(ref reader);
        }
        else if (value.Kind == FieldKindString)
        {
            value.StringValue = ReadString(ref reader);
        }

        return value;
    }

    private static void WriteString(FastBufferWriter writer, string value)
    {
        value ??= string.Empty;
        if (value.Length > 256)
        {
            value = value.Substring(0, 256);
        }

        WriteInt(writer, value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            WriteInt(writer, value[i]);
        }
    }

    private static string ReadString(ref FastBufferReader reader)
    {
        int length = Mathf.Clamp(ReadInt(ref reader), 0, 256);
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)ReadInt(ref reader);
        }

        return new string(chars);
    }

    private static void WriteBool(FastBufferWriter writer, bool value)
    {
        writer.WriteValueSafe(value, default(FastBufferWriter.ForPrimitives));
    }

    private static bool ReadBool(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out bool value, default(FastBufferWriter.ForPrimitives));
        return value;
    }

    private static void WriteInt(FastBufferWriter writer, int value)
    {
        writer.WriteValueSafe(value, default(FastBufferWriter.ForPrimitives));
    }

    private static int ReadInt(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out int value, default(FastBufferWriter.ForPrimitives));
        return value;
    }

    private static void WriteULong(FastBufferWriter writer, ulong value)
    {
        writer.WriteValueSafe(value, default(FastBufferWriter.ForPrimitives));
    }

    private static ulong ReadULong(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out ulong value, default(FastBufferWriter.ForPrimitives));
        return value;
    }

    private static void WriteFloat(FastBufferWriter writer, float value)
    {
        writer.WriteValueSafe(value, default(FastBufferWriter.ForPrimitives));
    }

    private static float ReadFloat(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out float value, default(FastBufferWriter.ForPrimitives));
        return value;
    }

    private static void WriteVector3(FastBufferWriter writer, Vector3 value)
    {
        writer.WriteValueSafe(value);
    }

    private static Vector3 ReadVector3(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out Vector3 value);
        return value;
    }

    private sealed class ExtendedSnapshotData
    {
        public int Sequence;
        public int LevelId;
        public int RandomSeed;
        public bool ShipHasLanded;
        public bool ShipIsLeaving;
        public bool BeganLoadingNewLevel;
        public bool NewGameIsLoading;
        public readonly List<ulong> AuthoritativeItemIds = new List<ulong>();
        public readonly List<ulong> AuthoritativeEnemyIds = new List<ulong>();
        public readonly List<PlayerExtendedState> PlayerStates = new List<PlayerExtendedState>();
        public readonly List<HeldItemState> HeldItems = new List<HeldItemState>();
        public readonly List<ReflectedObjectState> ReflectedObjects = new List<ReflectedObjectState>();
    }

    private sealed class PlayerExtendedState
    {
        public int PlayerIndex;
        public ulong ActualClientId;
        public ulong PlayerClientId;
        public bool IsPlayerControlled;
        public bool IsPlayerDead;
        public bool DisconnectedMidGame;
        public bool IsInsideFactory;
        public int CauseOfDeath;
        public int SpectatedPlayerIndex;
    }

    private sealed class HeldItemState
    {
        public ulong NetworkId;
        public int PlayerIndex;
        public int Slot;
        public bool IsPocketed;
        public bool IsActiveHeld;
        public bool IsItemOnlySlot;
    }

    private sealed class ReflectedObjectState
    {
        public int Kind;
        public ulong NetworkId;
        public Vector3 Position;
        public Vector3 Euler;
        public readonly List<FieldValue> Values = new List<FieldValue>();
    }

    private struct FieldValue
    {
        public int FieldIndex;
        public int Kind;
        public bool BoolValue;
        public int IntValue;
        public float FloatValue;
        public Vector3 Vector3Value;
        public string StringValue;
    }

    private sealed class ReflectedSyncSpec
    {
        public readonly int Kind;
        public readonly string TypeName;
        public readonly string[] BoolFields;
        public readonly string[] IntFields;
        public readonly string[] FloatFields;
        public readonly string[] Vector3Fields;
        public readonly string[] StringFields;

        public ReflectedSyncSpec(int kind, string typeName, string[] boolFields, string[] intFields, string[] floatFields, string[] vector3Fields, string[] stringFields)
        {
            Kind = kind;
            TypeName = typeName;
            BoolFields = boolFields ?? Array.Empty<string>();
            IntFields = intFields ?? Array.Empty<string>();
            FloatFields = floatFields ?? Array.Empty<string>();
            Vector3Fields = vector3Fields ?? Array.Empty<string>();
            StringFields = stringFields ?? Array.Empty<string>();
        }
    }
}

[HarmonyPatch]
internal static class LateJoinApprovalStatePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(LateJoinSyncManager), "CanApproveLateJoin", new[] { typeof(string).MakeByRefType() });
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ref bool __result, ref string reason)
    {
        __result = LateJoinComprehensiveSyncManager.IsStableLandedForLateJoin(out reason);
        return false;
    }
}

[HarmonyPatch(typeof(LateJoinSyncManager), "EnsureNetworkHandlersRegistered")]
internal static class LateJoinComprehensiveRegisterPatch
{
    private static void Postfix()
    {
        LateJoinComprehensiveSyncManager.EnsureNetworkHandlersRegistered();
    }
}

[HarmonyPatch]
internal static class LateJoinRequestWorldSnapshotPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(LateJoinSyncManager), "RequestWorldSnapshot");
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix()
    {
        return LateJoinComprehensiveSyncManager.InterceptWorldSnapshotRequest();
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        LateJoinComprehensiveSyncManager.RequestExtendedSnapshot();
    }
}

[HarmonyPatch(typeof(RoundManager), "GenerateNewLevelClientRpc")]
internal static class LateJoinGenerateNewLevelResetPatch
{
    private static void Prefix()
    {
        LateJoinComprehensiveSyncManager.ResetClientPostGenerationSetup();
    }
}

[HarmonyPatch]
internal static class LateJoinFinalizeLandedLocalStatePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(LateJoinSyncManager), "FinalizeLandedLocalState");
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(StartOfRound round)
    {
        if (round == null)
        {
            return false;
        }

        if (!round.shipHasLanded || round.shipIsLeaving || round.beganLoadingNewLevel || round.newGameIsLoading)
        {
            Plugin.Log.LogWarning($"Skipping landed local finalization because host state is not stable; shipHasLanded={round.shipHasLanded}, shipIsLeaving={round.shipIsLeaving}, beganLoadingNewLevel={round.beganLoadingNewLevel}, newGameIsLoading={round.newGameIsLoading}");
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(StartOfRound), "StartGame")]
internal static class LateJoinComprehensiveStartGamePatch
{
    private static void Prefix()
    {
        LateJoinSyncManager.SetLobbyJoinable(false);
    }
}

[HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
internal static class LateJoinComprehensiveShipLeavePatch
{
    private static void Prefix()
    {
        LateJoinSyncManager.SetLobbyJoinable(false);
    }
}

[HarmonyPatch(typeof(RoundManager), "FinishGeneratingNewLevelClientRpc")]
internal static class LateJoinComprehensiveFinishGeneratingPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        LateJoinSyncManager.SetLobbyJoinable(false);
    }
}
