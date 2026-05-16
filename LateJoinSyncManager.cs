using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace LC_LateJoin;

internal static class LateJoinSyncManager
{
    private const string BeginLevelMessage = "LC_LateJoin.BeginLevel";
    private const string SnapshotRequestMessage = "LC_LateJoin.SnapshotRequest";
    private const string WorldSnapshotMessage = "LC_LateJoin.WorldSnapshot";

    private const int BufferSize = 1048576;
    private const int EnemyFieldBool = 1;
    private const int EnemyFieldInt = 2;
    private const int EnemyFieldFloat = 3;
    private const int EnemySpecificNone = 0;
    private const int EnemySpecificCaveDweller = 1;
    private const int MaxEnemyAnimatorParameters = 80;
    private const int MaxEnemyAnimatorLayers = 8;

    private static Plugin plugin;
    private static NetworkManager registeredManager;
    private static bool messagesRegistered;
    private static readonly Dictionary<ulong, string> clientIdentityKeys = new Dictionary<ulong, string>();
    private static readonly Dictionary<string, StoredPlayerState> disconnectedPlayerStates = new Dictionary<string, StoredPlayerState>();
    private static readonly Dictionary<int, StoredPlayerState> disconnectedSlotStates = new Dictionary<int, StoredPlayerState>();
    private static readonly HashSet<ulong> restoredClientIds = new HashSet<ulong>();
    private static readonly HashSet<ulong> caveDwellersWaitingForNodes = new HashSet<ulong>();
    private static bool hasPendingLocalPlayerPosition;
    private static Vector3 pendingLocalPlayerPosition;
    private static Vector3 pendingLocalPlayerEuler;
    private static bool pendingLocalPlayerInElevator;
    private static bool pendingLocalPlayerInHangar;
    private static bool localLandedStateFinalized;
    private static bool localRestorePositionActive;
    private static Vector3 localRestorePosition;
    private static Vector3 localRestoreEuler;
    private static bool localRestoreInElevator;
    private static bool localRestoreInHangar;

    public static void Initialize(Plugin owner)
    {
        plugin = owner;
    }

    public static void EnsureNetworkHandlersRegistered()
    {
        EnsureMessagesRegistered();
    }

    public static void Shutdown()
    {
        if (registeredManager != null && registeredManager.CustomMessagingManager != null && messagesRegistered)
        {
            try
            {
                registeredManager.CustomMessagingManager.UnregisterNamedMessageHandler(BeginLevelMessage);
                registeredManager.CustomMessagingManager.UnregisterNamedMessageHandler(SnapshotRequestMessage);
                registeredManager.CustomMessagingManager.UnregisterNamedMessageHandler(WorldSnapshotMessage);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Late join unregister skipped: {e.Message}");
            }
        }

        messagesRegistered = false;
        registeredManager = null;
        clientIdentityKeys.Clear();
        disconnectedPlayerStates.Clear();
        disconnectedSlotStates.Clear();
        restoredClientIds.Clear();
        caveDwellersWaitingForNodes.Clear();
        ClearPendingLocalPlayerPosition();
        ClearActiveLocalRestorePosition();
        localLandedStateFinalized = false;
    }

    public static void RecordApprovedClientIdentity(ulong clientId, string identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey))
        {
            identityKey = clientId.ToString();
        }

        clientIdentityKeys[clientId] = identityKey;
    }

    public static bool CanApproveLateJoin()
    {
        return CanApproveLateJoin(out _);
    }

    public static bool CanApproveLateJoin(out string reason)
    {
        StartOfRound round = StartOfRound.Instance;
        if (round == null || GameNetworkManager.Instance == null)
        {
            reason = "round or network manager is missing";
            return false;
        }

        if (round.allPlayerScripts == null || round.connectedPlayersAmount + 1 >= round.allPlayerScripts.Length)
        {
            reason = $"lobby is full or player scripts are missing; connected={round.connectedPlayersAmount + 1}";
            return false;
        }

        if (round.inShipPhase)
        {
            reason = "ship phase";
            return true;
        }

        if (round.shipIsLeaving)
        {
            reason = "ship is leaving";
            return false;
        }

        if (round.newGameIsLoading)
        {
            reason = "new game is loading";
            return false;
        }

        RoundManager roundManager = RoundManager.Instance;
        bool levelReady = round.shipHasLanded
            || (roundManager != null && (roundManager.dungeonFinishedGeneratingForAllPlayers || roundManager.dungeonCompletedGenerating));

        if (!levelReady)
        {
            reason = $"level is not ready; shipHasLanded={round.shipHasLanded}, beganLoadingNewLevel={round.beganLoadingNewLevel}";
            return false;
        }

        reason = "landed level is ready";
        return true;
    }

    public static bool ShouldExposeInviteButton()
    {
        StartOfRound round = StartOfRound.Instance;
        if (round == null)
        {
            return false;
        }

        return CanApproveLateJoin();
    }

    public static void RefreshJoinableState()
    {
        SetLobbyJoinable(CanApproveLateJoin());
    }

    public static void SetLobbyJoinable(bool joinable)
    {
        if (GameNetworkManager.Instance == null)
        {
            return;
        }

        try
        {
            GameNetworkManager.Instance.SetLobbyJoinable(joinable);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"SetLobbyJoinable({joinable}) skipped: {e.Message}");
        }
    }

    public static void HandleServerClientConnected(StartOfRound round, ulong clientId)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            return;
        }

        EnsureMessagesRegistered();

        if (clientId == NetworkManager.Singleton.LocalClientId || round == null || round.inShipPhase)
        {
            RefreshJoinableState();
            return;
        }

        if (!CanApproveLateJoin())
        {
            RefreshJoinableState();
            return;
        }

        TryRestoreRejoiningPlayerOnServer(round, clientId);
        StartGameCoroutine(SendBeginLevelAfterClientSetup(clientId));
    }

    public static void CacheDisconnectState(StartOfRound round, int playerObjectNumber, ulong clientId)
    {
        if (round == null
            || NetworkManager.Singleton == null
            || !NetworkManager.Singleton.IsServer
            || round.inShipPhase
            || round.allPlayerScripts == null
            || playerObjectNumber < 0
            || playerObjectNumber >= round.allPlayerScripts.Length)
        {
            return;
        }

        PlayerControllerB player = round.allPlayerScripts[playerObjectNumber];
        if (player == null || clientId == NetworkManager.Singleton.LocalClientId)
        {
            return;
        }

        StoredPlayerState state = StoredPlayerState.From(round, player);
        disconnectedSlotStates[playerObjectNumber] = state;

        string identityKey = GetClientIdentityKey(clientId);
        if (!string.IsNullOrEmpty(identityKey))
        {
            disconnectedPlayerStates[identityKey] = state;
        }

        clientIdentityKeys.Remove(clientId);
        Plugin.Log.LogInfo($"Cached late join restore state for client {clientId} at player slot {playerObjectNumber}");
    }

    private static bool TryRestoreRejoiningPlayerOnServer(StartOfRound round, ulong clientId)
    {
        if (round == null || round.ClientPlayerList == null || round.allPlayerScripts == null)
        {
            return false;
        }

        if (!round.ClientPlayerList.TryGetValue(clientId, out int playerObjectNumber)
            || playerObjectNumber < 0
            || playerObjectNumber >= round.allPlayerScripts.Length)
        {
            return false;
        }

        StoredPlayerState state = null;
        string restoreSource = null;
        string identityKey = GetClientIdentityKey(clientId);
        if (!string.IsNullOrEmpty(identityKey) && disconnectedPlayerStates.TryGetValue(identityKey, out state))
        {
            restoreSource = "identity";
        }
        else if (disconnectedSlotStates.TryGetValue(playerObjectNumber, out state))
        {
            restoreSource = "slot";
        }

        if (state == null)
        {
            return false;
        }

        PlayerControllerB player = round.allPlayerScripts[playerObjectNumber];
        if (player == null)
        {
            return false;
        }

        ApplyStoredStateToServerPlayer(round, player, clientId, state);
        if (!string.IsNullOrEmpty(identityKey))
        {
            disconnectedPlayerStates.Remove(identityKey);
        }

        disconnectedSlotStates.Remove(playerObjectNumber);
        RemoveStoredStateFromIdentityCache(state);
        restoredClientIds.Add(clientId);
        Plugin.Log.LogInfo($"Restored reconnecting client {clientId} to cached position and health via {restoreSource}");
        return true;
    }

    private static void RemoveStoredStateFromIdentityCache(StoredPlayerState state)
    {
        if (state == null || disconnectedPlayerStates.Count == 0)
        {
            return;
        }

        string[] matchingKeys = disconnectedPlayerStates
            .Where(pair => ReferenceEquals(pair.Value, state))
            .Select(pair => pair.Key)
            .ToArray();

        for (int i = 0; i < matchingKeys.Length; i++)
        {
            disconnectedPlayerStates.Remove(matchingKeys[i]);
        }
    }

    private static IEnumerator SendBeginLevelAfterClientSetup(ulong clientId)
    {
        yield return null;
        yield return new WaitForSeconds(0.35f);

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer || !CanApproveLateJoin())
        {
            yield break;
        }

        SendBeginLevelSync(clientId);
        RefreshJoinableState();
    }

    private static void EnsureMessagesRegistered()
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

        Shutdown();
        registeredManager = manager;
        registeredManager.CustomMessagingManager.RegisterNamedMessageHandler(BeginLevelMessage, OnBeginLevelSync);
        registeredManager.CustomMessagingManager.RegisterNamedMessageHandler(SnapshotRequestMessage, OnSnapshotRequest);
        registeredManager.CustomMessagingManager.RegisterNamedMessageHandler(WorldSnapshotMessage, OnWorldSnapshot);
        messagesRegistered = true;
        Plugin.Log.LogInfo("Registered late join network message handlers");
    }

    private static void StartGameCoroutine(IEnumerator routine)
    {
        if (StartOfRound.Instance != null)
        {
            StartOfRound.Instance.StartCoroutine(routine);
            return;
        }

        if (RoundManager.Instance != null)
        {
            RoundManager.Instance.StartCoroutine(routine);
            return;
        }

        if (plugin != null)
        {
            plugin.StartCoroutine(routine);
        }
    }

    private static void SendBeginLevelSync(ulong clientId)
    {
        LevelSyncInfo info = BuildLevelSyncInfo();
        try
        {
            using FastBufferWriter writer = new FastBufferWriter(BufferSize, Allocator.Temp);
            WriteLevelSyncInfo(writer, info);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(BeginLevelMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            Plugin.Log.LogInfo($"Sent landed level sync to client {clientId}: level={info.LevelId}, seed={info.RandomSeed}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to send landed level sync to client {clientId}: {e}");
        }
    }

    private static void OnBeginLevelSync(ulong senderClientId, FastBufferReader reader)
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
        {
            return;
        }

        try
        {
            LevelSyncInfo info = ReadLevelSyncInfo(ref reader);
            localLandedStateFinalized = false;
            ClearPendingLocalPlayerPosition();
            ClearActiveLocalRestorePosition();
            MarkClientAsLandedGame();
            StartGameCoroutine(GenerateLevelThenRequestSnapshot(info));
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to read landed level sync: {e}");
        }
    }

    private static IEnumerator GenerateLevelThenRequestSnapshot(LevelSyncInfo info)
    {
        Plugin.Log.LogInfo($"Received landed level sync: level={info.LevelId}, seed={info.RandomSeed}");

        float start = Time.realtimeSinceStartup;
        while ((StartOfRound.Instance == null || RoundManager.Instance == null || GameNetworkManager.Instance.localPlayerController == null)
            && Time.realtimeSinceStartup - start < 20f)
        {
            yield return null;
        }

        RepairLocalPlayerReference();
        MarkClientAsLandedGame();
        PlayerControllerB localPlayer = GameNetworkManager.Instance.localPlayerController;
        if (localPlayer != null)
        {
            localPlayer.isPlayerControlled = false;
        }

        if (StartOfRound.Instance == null || RoundManager.Instance == null)
        {
            Plugin.Log.LogError("Cannot generate landed late-join level; round objects are missing.");
            yield break;
        }

        ApplyWeatherToLevel(info);
        HideOrbitVisualsForLandedLevel(StartOfRound.Instance);
        StartGameCoroutine(StabilizeLateJoinLighting(12f));

        try
        {
            RunWithRpcStage(RoundManager.Instance, NetworkBehaviour.__RpcExecStage.Execute, () =>
            {
                RoundManager.Instance.GenerateNewLevelClientRpc(info.RandomSeed, info.LevelId, info.MoldIterations, info.MoldStartPosition, info.DestroyedMold);
            });
            HideOrbitVisualsForLandedLevel(StartOfRound.Instance);
            FixDirectionalShadowCasters();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to generate landed late-join level: {e}");
            yield break;
        }

        start = Time.realtimeSinceStartup;
        while (RoundManager.Instance != null
            && RoundManager.Instance.currentLevel != null
            && RoundManager.Instance.currentLevel.spawnEnemiesAndScrap
            && !RoundManager.Instance.dungeonCompletedGenerating
            && Time.realtimeSinceStartup - start < 35f)
        {
            yield return null;
        }

        RequestWorldSnapshot();
        StartGameCoroutine(RequestWorldSnapshotFollowups(9f));
    }

    private static void RequestWorldSnapshot()
    {
        try
        {
            using FastBufferWriter writer = new FastBufferWriter(4, Allocator.Temp);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(SnapshotRequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            Plugin.Log.LogInfo("Requested landed world snapshot from host");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to request landed world snapshot: {e}");
        }
    }

    private static IEnumerator RequestWorldSnapshotFollowups(float seconds)
    {
        float start = Time.realtimeSinceStartup;
        yield return new WaitForSeconds(2.5f);
        while (Time.realtimeSinceStartup - start < seconds)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
            {
                yield break;
            }

            RequestWorldSnapshot();
            yield return new WaitForSeconds(2.5f);
        }
    }

    private static void OnSnapshotRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            return;
        }

        StartGameCoroutine(SendWorldSnapshotOnNextFrame(senderClientId));
    }

    private static IEnumerator SendWorldSnapshotOnNextFrame(ulong clientId)
    {
        yield return null;
        SendWorldSnapshot(clientId);
        yield return new WaitForSeconds(2f);

        if (NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsServer
            && NetworkManager.Singleton.ConnectedClientsIds.Contains(clientId))
        {
            SendWorldSnapshot(clientId);
        }

        restoredClientIds.Remove(clientId);
    }

    private static void SendWorldSnapshot(ulong clientId)
    {
        try
        {
            using FastBufferWriter writer = new FastBufferWriter(BufferSize, Allocator.Temp);
            WriteWorldSnapshot(writer);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(WorldSnapshotMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            Plugin.Log.LogInfo($"Sent landed world snapshot to client {clientId}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to send landed world snapshot to client {clientId}: {e}");
        }
    }

    private static void OnWorldSnapshot(ulong senderClientId, FastBufferReader reader)
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
        {
            return;
        }

        try
        {
            ApplyWorldSnapshot(ref reader);
            Plugin.Log.LogInfo("Applied landed world snapshot");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to apply landed world snapshot: {e}");
        }
    }

    private static LevelSyncInfo BuildLevelSyncInfo()
    {
        StartOfRound round = StartOfRound.Instance;
        SelectableLevel level = round.currentLevel;
        MoldSpreadManager moldSpreadManager = UnityEngine.Object.FindObjectOfType<MoldSpreadManager>();
        int[] destroyedMold = Array.Empty<int>();
        if (moldSpreadManager != null
            && moldSpreadManager.planetMoldStates != null
            && round.currentLevelID >= 0
            && round.currentLevelID < moldSpreadManager.planetMoldStates.Length)
        {
            destroyedMold = moldSpreadManager.planetMoldStates[round.currentLevelID].destroyedMold.ToArray();
        }

        return new LevelSyncInfo
        {
            RandomSeed = round.randomMapSeed,
            LevelId = round.currentLevelID,
            MoldIterations = level != null ? level.moldSpreadIterations : 0,
            MoldStartPosition = level != null ? level.moldStartPosition : -1,
            Weather = level != null ? (int)level.currentWeather : (int)LevelWeatherType.None,
            DestroyedMold = destroyedMold
        };
    }

    private static void ApplyWeatherToLevel(LevelSyncInfo info)
    {
        StartOfRound round = StartOfRound.Instance;
        if (round == null || round.levels == null || info.LevelId < 0 || info.LevelId >= round.levels.Length)
        {
            return;
        }

        LevelWeatherType weather = (LevelWeatherType)info.Weather;
        round.levels[info.LevelId].currentWeather = weather;
        if (RoundManager.Instance != null)
        {
            RoundManager.Instance.currentLevel = round.levels[info.LevelId];
            RoundManager.Instance.currentLevel.currentWeather = weather;
        }
    }

    private static void WriteWorldSnapshot(FastBufferWriter writer)
    {
        StartOfRound round = StartOfRound.Instance;
        TimeOfDay time = TimeOfDay.Instance;
        RoundManager roundManager = RoundManager.Instance;
        Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
        BreakerBox breakerBox = UnityEngine.Object.FindObjectOfType<BreakerBox>();

        WriteBool(writer, round.inShipPhase);
        WriteBool(writer, round.shipHasLanded);
        WriteBool(writer, round.shipIsLeaving);
        WriteBool(writer, round.shipDoorsEnabled);
        WriteBool(writer, round.hangarDoorsClosed);
        WriteInt(writer, round.connectedPlayersAmount);
        WriteInt(writer, round.livingPlayers);
        WriteInt(writer, terminal != null ? terminal.groupCredits : 0);

        WriteFloat(writer, time != null ? time.globalTime : 0f);
        WriteFloat(writer, time != null ? time.currentDayTime : 0f);
        WriteFloat(writer, time != null ? time.normalizedTimeOfDay : 0f);
        WriteFloat(writer, time != null ? time.timeUntilDeadline : 0f);
        WriteInt(writer, time != null ? time.profitQuota : 0);
        WriteInt(writer, time != null ? time.quotaFulfilled : 0);
        WriteBool(writer, time != null && time.currentDayTimeStarted);
        WriteBool(writer, time != null && time.movingGlobalTimeForward);
        WriteFloat(writer, time != null ? time.shipLeaveAutomaticallyTime : 0.996f);
        WriteInt(writer, time != null ? time.votesForShipToLeaveEarly : 0);
        WriteBool(writer, time != null && time.shipLeavingAlertCalled);

        WriteBool(writer, breakerBox == null || breakerBox.isPowerOn);
        WriteFloat(writer, roundManager != null ? roundManager.totalScrapValueInLevel : 0f);
        WriteInt(writer, roundManager != null ? roundManager.scrapCollectedInLevel : 0);
        WriteInt(writer, roundManager != null ? roundManager.valueOfFoundScrapItems : 0);

        WritePlayers(writer, round);
        WriteItems(writer);
        WriteDoors(writer);
        WriteTerminalDoors(writer);
        WriteAnimatedTriggers(writer);
        WriteEnemies(writer);
    }

    private static void ApplyWorldSnapshot(ref FastBufferReader reader)
    {
        StartOfRound round = StartOfRound.Instance;
        TimeOfDay time = TimeOfDay.Instance;
        RoundManager roundManager = RoundManager.Instance;

        bool inShipPhase = ReadBool(ref reader);
        bool shipHasLanded = ReadBool(ref reader);
        bool shipIsLeaving = ReadBool(ref reader);
        bool shipDoorsEnabled = ReadBool(ref reader);
        bool hangarDoorsClosed = ReadBool(ref reader);
        int connectedPlayersAmount = ReadInt(ref reader);
        int livingPlayers = ReadInt(ref reader);
        int groupCredits = ReadInt(ref reader);

        float globalTime = ReadFloat(ref reader);
        float currentDayTime = ReadFloat(ref reader);
        float normalizedTimeOfDay = ReadFloat(ref reader);
        float timeUntilDeadline = ReadFloat(ref reader);
        int profitQuota = ReadInt(ref reader);
        int quotaFulfilled = ReadInt(ref reader);
        bool currentDayTimeStarted = ReadBool(ref reader);
        bool movingGlobalTimeForward = ReadBool(ref reader);
        float shipLeaveAutomaticallyTime = ReadFloat(ref reader);
        int votesForShipToLeaveEarly = ReadInt(ref reader);
        bool shipLeavingAlertCalled = ReadBool(ref reader);

        bool powerOn = ReadBool(ref reader);
        float totalScrapValue = ReadFloat(ref reader);
        int scrapCollected = ReadInt(ref reader);
        int foundScrapValue = ReadInt(ref reader);

        RepairLocalPlayerReference();
        MarkClientAsLandedGame();

        round.inShipPhase = inShipPhase;
        round.shipHasLanded = shipHasLanded;
        round.shipIsLeaving = shipIsLeaving;
        round.shipDoorsEnabled = shipDoorsEnabled;
        round.hangarDoorsClosed = hangarDoorsClosed;
        round.connectedPlayersAmount = connectedPlayersAmount;
        round.livingPlayers = livingPlayers;

        Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
        if (terminal != null)
        {
            terminal.groupCredits = groupCredits;
        }

        if (time != null)
        {
            time.globalTime = globalTime;
            time.currentDayTime = currentDayTime;
            time.normalizedTimeOfDay = normalizedTimeOfDay;
            time.timeUntilDeadline = timeUntilDeadline;
            time.profitQuota = profitQuota;
            time.quotaFulfilled = quotaFulfilled;
            time.currentDayTimeStarted = currentDayTimeStarted;
            time.movingGlobalTimeForward = movingGlobalTimeForward;
            time.shipLeaveAutomaticallyTime = shipLeaveAutomaticallyTime;
            time.votesForShipToLeaveEarly = votesForShipToLeaveEarly;
            time.shipLeavingAlertCalled = shipLeavingAlertCalled;
            time.UpdateProfitQuotaCurrentTime();
            HUDManager.Instance.SetShipLeaveEarlyVotesText(votesForShipToLeaveEarly);
            HUDManager.Instance.SetClock(normalizedTimeOfDay, (float)time.numberOfHours, true);
        }

        if (roundManager != null)
        {
            roundManager.totalScrapValueInLevel = totalScrapValue;
            roundManager.scrapCollectedInLevel = scrapCollected;
            roundManager.valueOfFoundScrapItems = foundScrapValue;
        }

        ApplyPlayers(ref reader, round);
        ApplyItems(ref reader, round);
        ApplyDoors(ref reader);
        ApplyTerminalDoors(ref reader);
        ApplyAnimatedTriggers(ref reader);
        ApplyEnemies(ref reader, round);
        ApplyPowerState(powerOn);
        FinalizeLandedLocalState(round);
        StartGameCoroutine(RepairCaveDwellersAfterSnapshot(8f));
    }

    private static void FinalizeLandedLocalState(StartOfRound round)
    {
        if (round == null)
        {
            return;
        }

        round.inShipPhase = false;
        round.shipHasLanded = true;
        round.shipIsLeaving = false;
        round.beganLoadingNewLevel = false;
        round.newGameIsLoading = false;
        round.shipDoorsEnabled = true;

        ApplyShipDoorState(round, round.hangarDoorsClosed);

        HUDManager.Instance.loadingText.enabled = false;
        HUDManager.Instance.LoadingScreen.SetBool("IsLoading", false);

        PlayerControllerB localPlayer = GameNetworkManager.Instance.localPlayerController;
        if (localPlayer != null && !localPlayer.isPlayerDead)
        {
            EnsureLocalPlayerControl(round, localPlayer);
            localPlayer.isPlayerControlled = true;
            if (!localLandedStateFinalized && hasPendingLocalPlayerPosition)
            {
                localPlayer.isInElevator = pendingLocalPlayerInElevator;
                localPlayer.isInHangarShipRoom = pendingLocalPlayerInHangar;
                localPlayer.parentedToElevatorLastFrame = false;
                SetPlayerParentForSnapshot(round, localPlayer, pendingLocalPlayerInElevator);
                localPlayer.TeleportPlayer(pendingLocalPlayerPosition, false, 0f, false, true);
                localPlayer.transform.eulerAngles = pendingLocalPlayerEuler;
                SetActiveLocalRestorePosition(pendingLocalPlayerPosition, pendingLocalPlayerEuler, pendingLocalPlayerInElevator, pendingLocalPlayerInHangar);
                StartGameCoroutine(ReapplyLocalRestorePositionAfterSpawn(round, localPlayer, 4.25f));
            }
            else if (!localLandedStateFinalized)
            {
                localPlayer.isInElevator = true;
                localPlayer.isInHangarShipRoom = true;
                localPlayer.parentedToElevatorLastFrame = false;
                int playerId = Mathf.Clamp((int)localPlayer.playerClientId, 0, round.playerSpawnPositions.Length - 1);
                localPlayer.TeleportPlayer(round.playerSpawnPositions[playerId].position, false, 0f, false, true);
            }

            Vector3 syncedLocalPosition = localPlayer.transform.localPosition;
            localPlayer.serverPlayerPosition = syncedLocalPosition;
            localPlayer.oldPlayerPosition = syncedLocalPosition;
            localLandedStateFinalized = true;
            ClearPendingLocalPlayerPosition();
        }

        HideOrbitVisualsForLandedLevel(round);
        StartGameCoroutine(StabilizeLateJoinLighting(10f));
        round.SetPlayerObjectExtrapolate(false);
        round.StartTrackingAllPlayerVoices();
    }

    private static void MarkClientAsLandedGame()
    {
        if (GameNetworkManager.Instance != null)
        {
            GameNetworkManager.Instance.gameHasStarted = true;
        }

        if (StartOfRound.Instance != null)
        {
            StartOfRound.Instance.inShipPhase = false;
            StartOfRound.Instance.beganLoadingNewLevel = false;
            StartOfRound.Instance.newGameIsLoading = false;
        }
    }

    private static IEnumerator StabilizeLateJoinLighting(float seconds)
    {
        float start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < seconds)
        {
            HideOrbitVisualsForLandedLevel(StartOfRound.Instance);
            FixDirectionalShadowCasters();
            yield return new WaitForSeconds(0.5f);
        }
    }

    private static IEnumerator ReapplyLocalRestorePositionAfterSpawn(StartOfRound round, PlayerControllerB localPlayer, float seconds)
    {
        float start = Time.realtimeSinceStartup;
        while (localRestorePositionActive
            && localPlayer != null
            && Time.realtimeSinceStartup - start < seconds)
        {
            ApplyLocalRestorePositionNow(round, localPlayer, true);
            yield return null;
        }

        if (localRestorePositionActive && localPlayer != null)
        {
            ApplyLocalRestorePositionNow(round, localPlayer, true);
        }

        ClearActiveLocalRestorePosition();
    }

    private static void ApplyLocalRestorePositionNow(StartOfRound round, PlayerControllerB localPlayer, bool sendToOthers)
    {
        if (localPlayer == null)
        {
            return;
        }

        localPlayer.isInElevator = localRestoreInElevator;
        localPlayer.isInHangarShipRoom = localRestoreInHangar;
        localPlayer.parentedToElevatorLastFrame = false;
        SetPlayerParentForSnapshot(round, localPlayer, localRestoreInElevator);
        bool controllerWasEnabled = localPlayer.thisController != null && localPlayer.thisController.enabled;
        if (localPlayer.thisController != null)
        {
            localPlayer.thisController.enabled = false;
        }

        localPlayer.transform.position = localRestorePosition;
        if (localPlayer.thisController != null)
        {
            localPlayer.thisController.enabled = controllerWasEnabled;
        }

        localPlayer.transform.eulerAngles = localRestoreEuler;
        Vector3 syncedLocalPosition = localPlayer.transform.localPosition;
        localPlayer.serverPlayerPosition = syncedLocalPosition;
        localPlayer.oldPlayerPosition = syncedLocalPosition;

        if (sendToOthers)
        {
            SendLocalPlayerPositionToOthers(localPlayer, syncedLocalPosition);
        }
    }

    private static void SendLocalPlayerPositionToOthers(PlayerControllerB player, Vector3 syncedLocalPosition)
    {
        if (player == null)
        {
            return;
        }

        try
        {
            player.UpdatePlayerPositionRpc(
                syncedLocalPosition,
                player.isInElevator,
                player.isInHangarShipRoom,
                player.isExhausted,
                player.thisController != null && player.thisController.isGrounded);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Late join restore position RPC skipped for player {player.playerClientId}: {e.Message}");
        }
    }

    private static void ApplyShipDoorState(StartOfRound round, bool closed)
    {
        if (round == null)
        {
            return;
        }

        round.SetShipDoorsClosed(closed);
        HangarShipDoor hangarDoor = UnityEngine.Object.FindObjectOfType<HangarShipDoor>();
        if (hangarDoor != null)
        {
            hangarDoor.SetDoorButtonsEnabled(true);
            hangarDoor.shipDoorsAnimator.SetBool("Closed", closed);
            hangarDoor.PlayDoorAnimation(closed);
        }
        else if (round.shipDoorsAnimator != null)
        {
            round.shipDoorsAnimator.SetBool("Closed", closed);
        }
    }

    private static void HideOrbitVisualsForLandedLevel(StartOfRound round)
    {
        if (round == null)
        {
            return;
        }

        if (round.currentPlanetPrefab != null)
        {
            round.currentPlanetPrefab.SetActive(false);
        }

        if (round.outerSpaceSunAnimator != null)
        {
            round.outerSpaceSunAnimator.gameObject.SetActive(false);
        }

        GameObject planets = GameObject.Find("Environment/SpaceProps/Planets");
        if (planets != null)
        {
            planets.SetActive(false);
        }

        FixDirectionalShadowCasters();
    }

    private static void FixDirectionalShadowCasters()
    {
        Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
        Light keep = null;
        float keepScore = float.MinValue;

        for (int i = 0; i < lights.Length; i++)
        {
            Light light = lights[i];
            if (light == null || light.type != LightType.Directional || !light.enabled || !light.gameObject.activeInHierarchy || light.shadows == LightShadows.None)
            {
                continue;
            }

            if (IsOrbitLight(light))
            {
                light.shadows = LightShadows.None;
                continue;
            }

            float score = light.intensity;
            if (keep == null || score > keepScore)
            {
                keep = light;
                keepScore = score;
            }
        }

        if (keep == null)
        {
            return;
        }

        for (int i = 0; i < lights.Length; i++)
        {
            Light light = lights[i];
            if (light != null
                && light != keep
                && light.type == LightType.Directional
                && light.enabled
                && light.gameObject.activeInHierarchy
                && light.shadows != LightShadows.None)
            {
                light.shadows = LightShadows.None;
            }
        }
    }

    private static bool IsOrbitLight(Light light)
    {
        if (light == null)
        {
            return false;
        }

        Transform transform = light.transform;
        while (transform != null)
        {
            string name = transform.name;
            if (name.Contains("SpaceProps") || name.Contains("Planets"))
            {
                return true;
            }

            transform = transform.parent;
        }

        StartOfRound round = StartOfRound.Instance;
        if (round != null)
        {
            if (round.outerSpaceSunAnimator != null && IsChildOf(light.transform, round.outerSpaceSunAnimator.transform))
            {
                return true;
            }

            if (round.currentPlanetPrefab != null && IsChildOf(light.transform, round.currentPlanetPrefab.transform))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsChildOf(Transform child, Transform parent)
    {
        Transform current = child;
        while (current != null)
        {
            if (current == parent)
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static void EnsureLocalPlayerControl(StartOfRound round, PlayerControllerB localPlayer)
    {
        if (round == null || localPlayer == null)
        {
            return;
        }

        if (!round.localClientHasControl || round.thisClientPlayerId != (int)localPlayer.playerClientId || localPlayer.justConnected)
        {
            localPlayer.justConnected = false;
            localPlayer.ConnectClientToPlayerObject();
        }

        round.localPlayerController = localPlayer;
        GameNetworkManager.Instance.localPlayerController = localPlayer;
        round.thisClientPlayerId = (int)localPlayer.playerClientId;
        round.localClientHasControl = true;
    }

    private static void WritePlayers(FastBufferWriter writer, StartOfRound round)
    {
        int count = round.allPlayerScripts != null ? round.allPlayerScripts.Length : 0;
        WriteInt(writer, count);
        for (int i = 0; i < count; i++)
        {
            PlayerControllerB player = round.allPlayerScripts[i];
            WriteBool(writer, player != null);
            if (player == null)
            {
                continue;
            }

            WriteULong(writer, player.actualClientId);
            WriteULong(writer, player.playerClientId);
            WriteBool(writer, restoredClientIds.Contains(player.actualClientId));
            WriteBool(writer, player.isPlayerControlled);
            WriteBool(writer, player.isPlayerDead);
            WriteBool(writer, player.disconnectedMidGame);
            WriteBool(writer, player.isInElevator);
            WriteBool(writer, player.isInHangarShipRoom);
            WriteInt(writer, player.currentSuitID);
            WriteBool(writer, player.isCrouching);
            WriteInt(writer, player.health);
            WriteBool(writer, player.criticallyInjured);
            WriteBool(writer, player.bleedingHeavily);
            WriteBool(writer, player.isExhausted);
            WriteBool(writer, player.thisController != null && player.thisController.isGrounded);
            Vector3 syncedLocalPosition = GetAuthoritativeLocalPlayerPosition(player);
            WriteVector3(writer, GetAuthoritativeWorldPlayerPosition(round, player, syncedLocalPosition));
            WriteVector3(writer, player.transform.eulerAngles);
            WriteVector3(writer, syncedLocalPosition);
        }
    }

    private static void ApplyPlayers(ref FastBufferReader reader, StartOfRound round)
    {
        ClearPendingLocalPlayerPosition();
        int count = ReadInt(ref reader);
        for (int i = 0; i < count; i++)
        {
            bool hasPlayer = ReadBool(ref reader);
            if (!hasPlayer)
            {
                continue;
            }

            ulong actualClientId = ReadULong(ref reader);
            ulong playerClientId = ReadULong(ref reader);
            bool restorePositionForOwner = ReadBool(ref reader);
            bool isControlled = ReadBool(ref reader);
            bool isDead = ReadBool(ref reader);
            bool disconnectedMidGame = ReadBool(ref reader);
            bool isInElevator = ReadBool(ref reader);
            bool isInHangar = ReadBool(ref reader);
            int suitId = ReadInt(ref reader);
            bool isCrouching = ReadBool(ref reader);
            int health = ReadInt(ref reader);
            bool criticallyInjured = ReadBool(ref reader);
            bool bleedingHeavily = ReadBool(ref reader);
            bool isExhausted = ReadBool(ref reader);
            bool isGrounded = ReadBool(ref reader);
            Vector3 worldPosition = ReadVector3(ref reader);
            Vector3 worldEuler = ReadVector3(ref reader);
            Vector3 serverPosition = ReadVector3(ref reader);

            if (round.allPlayerScripts == null || i >= round.allPlayerScripts.Length || round.allPlayerScripts[i] == null)
            {
                continue;
            }

            PlayerControllerB player = round.allPlayerScripts[i];
            player.actualClientId = actualClientId;
            player.playerClientId = playerClientId;
            player.isPlayerControlled = isControlled;
            player.isPlayerDead = isDead;
            player.disconnectedMidGame = disconnectedMidGame;
            player.isInElevator = isInElevator;
            player.isInHangarShipRoom = isInHangar;
            bool isLocalPlayer = player.IsOwner || (NetworkManager.Singleton != null && actualClientId == NetworkManager.Singleton.LocalClientId);
            ApplyPlayerVitalsAndPose(player, isCrouching, health, criticallyInjured, bleedingHeavily, isLocalPlayer);

            if (!isLocalPlayer)
            {
                ApplyPlayerPositionLikeOriginalRpc(round, player, serverPosition, worldEuler, isInElevator, isInHangar, isExhausted, isGrounded);
            }
            else
            {
                player.serverPlayerPosition = serverPosition;
                player.oldPlayerPosition = serverPosition;
                if (restorePositionForOwner)
                {
                    hasPendingLocalPlayerPosition = true;
                    pendingLocalPlayerPosition = worldPosition;
                    pendingLocalPlayerEuler = worldEuler;
                    pendingLocalPlayerInElevator = isInElevator;
                    pendingLocalPlayerInHangar = isInHangar;
                }
            }

            UnlockableSuit.SwitchSuitForPlayer(player, suitId, false);
        }
    }

    private static string GetClientIdentityKey(ulong clientId)
    {
        if (clientIdentityKeys.TryGetValue(clientId, out string identityKey))
        {
            return identityKey;
        }

        if (GameNetworkManager.Instance != null && GameNetworkManager.Instance.disableSteam)
        {
            return clientId.ToString();
        }

        return null;
    }

    private static Vector3 GetAuthoritativeLocalPlayerPosition(PlayerControllerB player)
    {
        if (player == null)
        {
            return Vector3.zero;
        }

        if (NetworkManager.Singleton != null && player.actualClientId == NetworkManager.Singleton.LocalClientId)
        {
            return player.transform.localPosition;
        }

        return player.serverPlayerPosition;
    }

    private static Vector3 GetAuthoritativeWorldPlayerPosition(StartOfRound round, PlayerControllerB player, Vector3 syncedLocalPosition)
    {
        if (player == null)
        {
            return Vector3.zero;
        }

        if (NetworkManager.Singleton != null && player.actualClientId == NetworkManager.Singleton.LocalClientId)
        {
            return player.transform.position;
        }

        Transform parent = ResolvePlayerSnapshotParent(round, player);
        return parent != null ? parent.TransformPoint(syncedLocalPosition) : syncedLocalPosition;
    }

    private static Transform ResolvePlayerSnapshotParent(StartOfRound round, PlayerControllerB player)
    {
        if (player != null && player.isInElevator && round != null && round.elevatorTransform != null)
        {
            return round.elevatorTransform;
        }

        if (player != null && player.transform.parent != null)
        {
            return player.transform.parent;
        }

        if (round != null && round.playersContainer != null)
        {
            return round.playersContainer;
        }

        return null;
    }

    private static void ApplyStoredStateToServerPlayer(StartOfRound round, PlayerControllerB player, ulong clientId, StoredPlayerState state)
    {
        player.actualClientId = clientId;
        player.isPlayerControlled = true;
        player.isPlayerDead = state.IsPlayerDead;
        player.disconnectedMidGame = false;
        player.isInElevator = state.IsInElevator;
        player.isInHangarShipRoom = state.IsInHangar;
        player.parentedToElevatorLastFrame = false;
        SetPlayerParentForSnapshot(round, player, state.IsInElevator);
        player.transform.localPosition = state.SyncedLocalPosition;
        player.transform.eulerAngles = state.WorldEuler;
        player.serverPlayerPosition = state.SyncedLocalPosition;
        player.oldPlayerPosition = state.SyncedLocalPosition;
        player.snapToServerPosition = true;
        ApplyPlayerVitalsAndPose(player, state.IsCrouching, state.Health, state.CriticallyInjured, state.BleedingHeavily, false);
        UnlockableSuit.SwitchSuitForPlayer(player, state.SuitId, false);
    }

    private static void ApplyPlayerPositionLikeOriginalRpc(StartOfRound round, PlayerControllerB player, Vector3 syncedLocalPosition, Vector3 worldEuler, bool isInElevator, bool isInHangar, bool isExhausted, bool isGrounded)
    {
        if (player == null)
        {
            return;
        }

        player.physicsParent = null;
        player.overridePhysicsParent = null;
        player.lastSyncedPhysicsParent = null;
        player.parentedToElevatorLastFrame = !isInElevator;

        try
        {
            RunWithRpcStage(player, NetworkBehaviour.__RpcExecStage.Execute, () =>
            {
                player.UpdatePlayerPositionRpc(syncedLocalPosition, isInElevator, isInHangar, isExhausted, isGrounded);
            });
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Original player position apply skipped for player {player.playerClientId}: {e.Message}");
            SetPlayerParentForSnapshot(round, player, isInElevator);
        }

        player.transform.localPosition = syncedLocalPosition;
        player.transform.eulerAngles = worldEuler;
        player.serverPlayerPosition = syncedLocalPosition;
        player.oldPlayerPosition = syncedLocalPosition;
        player.snapToServerPosition = true;
        player.timeSincePlayerMoving = 0f;
    }

    private static void ApplyPlayerVitalsAndPose(PlayerControllerB player, bool isCrouching, int health, bool criticallyInjured, bool bleedingHeavily, bool updateHud)
    {
        if (player == null)
        {
            return;
        }

        player.isCrouching = isCrouching;
        player.health = Mathf.Clamp(health, 0, 100);
        player.criticallyInjured = criticallyInjured;
        player.bleedingHeavily = bleedingHeavily;
        if (criticallyInjured)
        {
            player.hasBeenCriticallyInjured = true;
        }

        if (player.playerBodyAnimator != null)
        {
            player.playerBodyAnimator.SetBool("crouching", isCrouching);
            player.playerBodyAnimator.SetBool("Limp", criticallyInjured);
            if (isCrouching)
            {
                player.playerBodyAnimator.SetTrigger("startCrouching");
            }
        }

        if (updateHud && HUDManager.Instance != null)
        {
            HUDManager.Instance.SetCracksOnVisor(player.health);
            HUDManager.Instance.UpdateHealthUI(player.health, false);
        }
    }

    private static void ClearPendingLocalPlayerPosition()
    {
        hasPendingLocalPlayerPosition = false;
        pendingLocalPlayerPosition = Vector3.zero;
        pendingLocalPlayerEuler = Vector3.zero;
        pendingLocalPlayerInElevator = false;
        pendingLocalPlayerInHangar = false;
    }

    private static void SetActiveLocalRestorePosition(Vector3 position, Vector3 euler, bool isInElevator, bool isInHangar)
    {
        localRestorePositionActive = true;
        localRestorePosition = position;
        localRestoreEuler = euler;
        localRestoreInElevator = isInElevator;
        localRestoreInHangar = isInHangar;
    }

    private static void ClearActiveLocalRestorePosition()
    {
        localRestorePositionActive = false;
        localRestorePosition = Vector3.zero;
        localRestoreEuler = Vector3.zero;
        localRestoreInElevator = false;
        localRestoreInHangar = false;
    }

    private static void SetPlayerParentForSnapshot(StartOfRound round, PlayerControllerB player, bool isInElevator)
    {
        if (round == null || player == null)
        {
            return;
        }

        if (isInElevator && round.elevatorTransform != null)
        {
            player.transform.SetParent(round.elevatorTransform);
            player.parentedToElevatorLastFrame = true;
            return;
        }

        if (round.playersContainer != null)
        {
            player.transform.SetParent(round.playersContainer);
            player.parentedToElevatorLastFrame = false;
        }
    }

    private static void WriteItems(FastBufferWriter writer)
    {
        GrabbableObject[] items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>()
            .Where(item => item != null && item.NetworkObject != null && item.NetworkObject.IsSpawned)
            .OrderBy(item => item.NetworkObject.NetworkObjectId)
            .ToArray();

        WriteInt(writer, items.Length);
        foreach (GrabbableObject item in items)
        {
            Transform parent = item.transform.parent;
            Vector3 targetWorldPosition = parent != null ? parent.TransformPoint(item.targetFloorPosition) : item.targetFloorPosition;

            WriteULong(writer, item.NetworkObject.NetworkObjectId);
            WriteVector3(writer, item.transform.position);
            WriteVector3(writer, item.transform.eulerAngles);
            WriteVector3(writer, targetWorldPosition);
            WriteBool(writer, item.isInElevator);
            WriteBool(writer, item.isInShipRoom);
            WriteBool(writer, item.isHeld);
            WriteBool(writer, item.isPocketed);
            WriteInt(writer, item.playerHeldBy != null ? (int)item.playerHeldBy.playerClientId : -1);
            WriteInt(writer, FindHeldItemSlot(item));
            WriteBool(writer, item.deactivated);
            WriteBool(writer, item.itemUsedUp);
            WriteInt(writer, item.scrapValue);
            WriteBool(writer, item.insertedBattery != null);
            if (item.insertedBattery != null)
            {
                WriteFloat(writer, item.insertedBattery.charge);
                WriteBool(writer, item.insertedBattery.empty);
            }
        }
    }

    private static void ApplyItems(ref FastBufferReader reader, StartOfRound round)
    {
        ClearHeldItemStateBeforeApplyingItems(round);

        int count = ReadInt(ref reader);
        for (int i = 0; i < count; i++)
        {
            ulong networkId = ReadULong(ref reader);
            Vector3 position = ReadVector3(ref reader);
            Vector3 euler = ReadVector3(ref reader);
            Vector3 targetWorldPosition = ReadVector3(ref reader);
            bool isInElevator = ReadBool(ref reader);
            bool isInShipRoom = ReadBool(ref reader);
            bool isHeld = ReadBool(ref reader);
            bool isPocketed = ReadBool(ref reader);
            int heldByPlayerId = ReadInt(ref reader);
            int heldSlot = ReadInt(ref reader);
            bool deactivated = ReadBool(ref reader);
            bool itemUsedUp = ReadBool(ref reader);
            int scrapValue = ReadInt(ref reader);
            bool hasBattery = ReadBool(ref reader);
            float batteryCharge = 0f;
            bool batteryEmpty = true;
            if (hasBattery)
            {
                batteryCharge = ReadFloat(ref reader);
                batteryEmpty = ReadBool(ref reader);
            }

            NetworkObject networkObject = TryGetNetworkObject(networkId);
            if (networkObject == null || !networkObject.TryGetComponent(out GrabbableObject item))
            {
                continue;
            }

            item.isInElevator = isInElevator;
            item.isInShipRoom = isInShipRoom;
            item.deactivated = deactivated;
            item.itemUsedUp = itemUsedUp;
            item.SetScrapValue(scrapValue);

            if (item.insertedBattery != null && hasBattery)
            {
                item.insertedBattery.charge = batteryCharge;
                item.insertedBattery.empty = batteryEmpty;
            }

            if (!isHeld)
            {
                Transform parent = ResolveItemParent(round, isInElevator);
                item.transform.SetParent(parent, true);
                item.parentObject = null;
                item.playerHeldBy = null;
                item.heldByPlayerOnServer = false;
                item.transform.position = position;
                item.transform.eulerAngles = euler;
                item.targetFloorPosition = parent != null ? parent.InverseTransformPoint(targetWorldPosition) : targetWorldPosition;
                item.EnablePhysics(!isPocketed);
                item.EnableItemMeshes(!isPocketed);
            }
            else if (round != null && heldByPlayerId >= 0 && round.allPlayerScripts != null && heldByPlayerId < round.allPlayerScripts.Length)
            {
                PlayerControllerB holder = round.allPlayerScripts[heldByPlayerId];
                item.playerHeldBy = holder;
                item.heldByPlayerOnServer = true;
                item.parentObject = holder.serverItemHolder;
                item.EnablePhysics(false);
                if (heldSlot == 50)
                {
                    holder.ItemOnlySlot = item;
                }
                else if (heldSlot >= 0 && heldSlot < holder.ItemSlots.Length)
                {
                    holder.ItemSlots[heldSlot] = item;
                    if (!isPocketed)
                    {
                        holder.currentlyHeldObjectServer = item;
                        holder.isHoldingObject = true;
                        holder.currentItemSlot = heldSlot;
                        holder.twoHanded = item.itemProperties.twoHanded;
                        holder.twoHandedAnimation = item.itemProperties.twoHandedAnimation;
                    }
                }
            }

            item.isHeld = isHeld;
            item.isPocketed = isPocketed;
        }
    }

    private static int FindHeldItemSlot(GrabbableObject item)
    {
        if (item == null || item.playerHeldBy == null)
        {
            return -1;
        }

        if (item.playerHeldBy.ItemOnlySlot == item)
        {
            return 50;
        }

        for (int i = 0; i < item.playerHeldBy.ItemSlots.Length; i++)
        {
            if (item.playerHeldBy.ItemSlots[i] == item)
            {
                return i;
            }
        }

        return -1;
    }

    private static void ClearHeldItemStateBeforeApplyingItems(StartOfRound round)
    {
        if (round == null || round.allPlayerScripts == null)
        {
            return;
        }

        for (int i = 0; i < round.allPlayerScripts.Length; i++)
        {
            PlayerControllerB player = round.allPlayerScripts[i];
            if (player == null)
            {
                continue;
            }

            for (int slot = 0; slot < player.ItemSlots.Length; slot++)
            {
                player.ItemSlots[slot] = null;
            }

            player.ItemOnlySlot = null;
            player.currentlyHeldObjectServer = null;
            player.isHoldingObject = false;
            player.twoHanded = false;
            player.twoHandedAnimation = false;
        }
    }

    private static Transform ResolveItemParent(StartOfRound round, bool isInElevator)
    {
        if (isInElevator && round != null && round.elevatorTransform != null)
        {
            return round.elevatorTransform;
        }

        if (RoundManager.Instance != null && RoundManager.Instance.mapPropsContainer != null)
        {
            return RoundManager.Instance.mapPropsContainer.transform;
        }

        return round != null ? round.propsContainer : null;
    }

    private static void WriteDoors(FastBufferWriter writer)
    {
        DoorLock[] doors = UnityEngine.Object.FindObjectsOfType<DoorLock>()
            .Where(door => door != null && door.NetworkObject != null && door.NetworkObject.IsSpawned)
            .OrderBy(door => door.NetworkObject.NetworkObjectId)
            .ToArray();

        WriteInt(writer, doors.Length);
        foreach (DoorLock door in doors)
        {
            WriteULong(writer, door.NetworkObject.NetworkObjectId);
            WriteBool(writer, door.isLocked);
            WriteBool(writer, door.isDoorOpened);
        }
    }

    private static void ApplyDoors(ref FastBufferReader reader)
    {
        int count = ReadInt(ref reader);
        for (int i = 0; i < count; i++)
        {
            ulong networkId = ReadULong(ref reader);
            bool isLocked = ReadBool(ref reader);
            bool isOpen = ReadBool(ref reader);

            NetworkObject networkObject = TryGetNetworkObject(networkId);
            if (networkObject == null || !networkObject.TryGetComponent(out DoorLock door))
            {
                continue;
            }

            AnimatedObjectTrigger trigger = door.GetComponent<AnimatedObjectTrigger>();
            if (trigger != null)
            {
                trigger.SetBoolOnClientOnly(isOpen);
            }

            door.SetDoorAsOpen(isOpen);
            if (isLocked)
            {
                door.LockDoor();
            }
            else
            {
                door.UnlockDoor();
            }
        }
    }

    private static void WriteTerminalDoors(FastBufferWriter writer)
    {
        TerminalAccessibleObject[] doors = UnityEngine.Object.FindObjectsOfType<TerminalAccessibleObject>()
            .Where(door => door != null && door.isBigDoor && door.NetworkObject != null && door.NetworkObject.IsSpawned)
            .OrderBy(door => door.NetworkObject.NetworkObjectId)
            .ToArray();

        WriteInt(writer, doors.Length);
        foreach (TerminalAccessibleObject door in doors)
        {
            WriteULong(writer, door.NetworkObject.NetworkObjectId);
            WriteBool(writer, GetTerminalDoorOpen(door));
        }
    }

    private static void ApplyTerminalDoors(ref FastBufferReader reader)
    {
        int count = ReadInt(ref reader);
        for (int i = 0; i < count; i++)
        {
            ulong networkId = ReadULong(ref reader);
            bool isOpen = ReadBool(ref reader);

            NetworkObject networkObject = TryGetNetworkObject(networkId);
            if (networkObject == null || !networkObject.TryGetComponent(out TerminalAccessibleObject door))
            {
                continue;
            }

            AnimatedObjectTrigger trigger = door.GetComponent<AnimatedObjectTrigger>();
            if (trigger != null)
            {
                trigger.SetBoolOnClientOnly(isOpen);
            }

            door.SetDoorOpen(isOpen);
        }
    }

    private static bool GetTerminalDoorOpen(TerminalAccessibleObject door)
    {
        if (door == null)
        {
            return false;
        }

        return door.isDoorOpen;
    }

    private static void WriteAnimatedTriggers(FastBufferWriter writer)
    {
        AnimatedObjectTrigger[] triggers = UnityEngine.Object.FindObjectsOfType<AnimatedObjectTrigger>()
            .Where(trigger => trigger != null && trigger.NetworkObject != null && trigger.NetworkObject.IsSpawned)
            .OrderBy(trigger => trigger.NetworkObject.NetworkObjectId)
            .ToArray();

        WriteInt(writer, triggers.Length);
        foreach (AnimatedObjectTrigger trigger in triggers)
        {
            WriteULong(writer, trigger.NetworkObject.NetworkObjectId);
            WriteBool(writer, trigger.boolValue);
            WriteBool(writer, trigger.setInitialState);
        }
    }

    private static void ApplyAnimatedTriggers(ref FastBufferReader reader)
    {
        int count = ReadInt(ref reader);
        for (int i = 0; i < count; i++)
        {
            ulong networkId = ReadULong(ref reader);
            bool boolValue = ReadBool(ref reader);
            bool setInitialState = ReadBool(ref reader);

            NetworkObject networkObject = TryGetNetworkObject(networkId);
            if (networkObject == null || !networkObject.TryGetComponent(out AnimatedObjectTrigger trigger))
            {
                continue;
            }

            trigger.setInitialState = setInitialState;
            trigger.SetBoolOnClientOnly(boolValue);
        }
    }

    private static void WriteEnemies(FastBufferWriter writer)
    {
        EnemyAI[] enemies = UnityEngine.Object.FindObjectsOfType<EnemyAI>()
            .Where(enemy => enemy != null && enemy.NetworkObject != null && enemy.NetworkObject.IsSpawned)
            .OrderBy(enemy => enemy.NetworkObject.NetworkObjectId)
            .ToArray();

        WriteInt(writer, enemies.Length);
        foreach (EnemyAI enemy in enemies)
        {
            int targetPlayerId = enemy.targetPlayer != null ? (int)enemy.targetPlayer.playerClientId : -1;
            WriteULong(writer, enemy.NetworkObject.NetworkObjectId);
            WriteVector3(writer, enemy.transform.position);
            WriteVector3(writer, enemy.transform.eulerAngles);
            WriteInt(writer, enemy.currentBehaviourStateIndex);
            WriteInt(writer, enemy.enemyHP);
            WriteBool(writer, enemy.isEnemyDead);
            WriteBool(writer, enemy.isOutside);
            WriteInt(writer, targetPlayerId);
            WriteEnemyAnimatorSnapshot(writer, enemy);
            WriteEnemySpecificSnapshot(writer, enemy);
        }
    }

    private static void ApplyEnemies(ref FastBufferReader reader, StartOfRound round)
    {
        int count = ReadInt(ref reader);
        for (int i = 0; i < count; i++)
        {
            ulong networkId = ReadULong(ref reader);
            Vector3 position = ReadVector3(ref reader);
            Vector3 euler = ReadVector3(ref reader);
            int state = ReadInt(ref reader);
            int hp = ReadInt(ref reader);
            bool isDead = ReadBool(ref reader);
            bool isOutside = ReadBool(ref reader);
            int targetPlayerId = ReadInt(ref reader);

            NetworkObject networkObject = TryGetNetworkObject(networkId);
            EnemyAI enemy = null;
            if (networkObject != null)
            {
                networkObject.TryGetComponent(out enemy);
            }

            if (enemy != null)
            {
                EnsureEnemyAINodes(enemy);
                MoveEnemyToSnapshot(enemy, position, euler);
                enemy.enemyHP = hp;
                if (enemy.isOutside != isOutside)
                {
                    enemy.SetEnemyOutside(isOutside);
                }
                else
                {
                    enemy.isOutside = isOutside;
                }

                if (targetPlayerId >= 0 && round.allPlayerScripts != null && targetPlayerId < round.allPlayerScripts.Length)
                {
                    enemy.targetPlayer = round.allPlayerScripts[targetPlayerId];
                }
                else
                {
                    enemy.targetPlayer = null;
                }

                if (enemy is CaveDwellerAI caveDweller)
                {
                    bool caveNodesReady = EnsureCaveDwellerAINodes(caveDweller);
                    if (state != 0 && !caveNodesReady)
                    {
                        MarkCaveDwellerWaitingForNodes(caveDweller);
                    }
                    else
                    {
                        ClearCaveDwellerWaitingForNodes(caveDweller);
                        ApplyEnemyBehaviourState(enemy, state);
                    }
                }
                else
                {
                    ApplyEnemyBehaviourState(enemy, state);
                }
            }

            ReadAndApplyEnemyAnimatorSnapshot(ref reader, enemy);
            ReadAndApplyEnemySpecificSnapshot(ref reader, enemy, round);

            if (enemy != null && isDead && !enemy.isEnemyDead)
            {
                enemy.KillEnemy(false);
            }
        }
    }

    private static void MoveEnemyToSnapshot(EnemyAI enemy, Vector3 position, Vector3 euler)
    {
        bool warped = false;
        try
        {
            if (enemy.agent != null && enemy.agent.enabled && enemy.agent.isOnNavMesh)
            {
                warped = enemy.agent.Warp(position);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Enemy NavMesh warp skipped: {e.Message}");
        }

        if (!warped)
        {
            enemy.transform.position = position;
        }

        enemy.transform.eulerAngles = euler;
        enemy.serverPosition = position;
        enemy.serverRotation = euler;
    }

    private static void ApplyEnemyBehaviourState(EnemyAI enemy, int state)
    {
        if (state < 0 || enemy.enemyBehaviourStates == null || state >= enemy.enemyBehaviourStates.Length)
        {
            return;
        }

        enemy.SwitchToBehaviourStateOnLocalClient(state);
    }

    private static bool EnsureEnemyAINodes(EnemyAI enemy)
    {
        if (enemy == null || RoundManager.Instance == null)
        {
            return false;
        }

        try
        {
            if (enemy.agent == null)
            {
                enemy.agent = enemy.GetComponentInChildren<NavMeshAgent>();
            }

            if (enemy.enemyType != null && enemy.agent != null)
            {
                enemy.GetAINodes();
            }

            if (!HasUsableAINodes(enemy.allAINodes))
            {
                enemy.allAINodes = GetFallbackAINodes(enemy);
            }

            enemy.allAINodes = CleanAINodes(enemy.allAINodes);
            if (enemy.path1 == null)
            {
                enemy.path1 = new NavMeshPath();
            }

            return HasUsableAINodes(enemy.allAINodes);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Enemy AI node repair skipped for {enemy.name}: {e.Message}");
            return HasUsableAINodes(enemy.allAINodes);
        }
    }

    private static bool EnsureCaveDwellerAINodes(CaveDwellerAI cave)
    {
        if (cave == null || !EnsureEnemyAINodes(cave))
        {
            return false;
        }

        GameObject[] nodes = ExpandAINodesForCaveDweller(cave.allAINodes);
        if (!HasUsableAINodes(nodes))
        {
            return false;
        }

        cave.allAINodes = nodes;
        cave.nodesTempArray = nodes.ToArray();
        if (cave.targetNode == null)
        {
            cave.targetNode = nodes[0].transform;
        }

        if (cave.searchRoutine != null && cave.searchRoutine.searchWidth <= 0f)
        {
            cave.searchRoutine.searchWidth = Mathf.Max(cave.baseSearchWidth, 80f);
        }

        if (cave.babySearchRoutine != null && cave.babySearchRoutine.searchWidth <= 0f)
        {
            cave.babySearchRoutine.searchWidth = Mathf.Max(cave.baseSearchWidth, 80f);
        }

        if (RoundManager.Instance != null && !RoundManager.Instance.SpawnedEnemies.Contains(cave))
        {
            RoundManager.Instance.SpawnedEnemies.Add(cave);
        }

        return true;
    }

    private static GameObject[] GetFallbackAINodes(EnemyAI enemy)
    {
        RoundManager roundManager = RoundManager.Instance;
        if (roundManager == null)
        {
            return Array.Empty<GameObject>();
        }

        if (enemy.isOutside)
        {
            roundManager.GetOutsideAINodes(true);
            if (enemy.enemyType != null && enemy.enemyType.WaterType == EnemyWaterType.LandOnly && HasUsableAINodes(roundManager.outsideAIDryNodesUnordered))
            {
                return CleanAINodes(roundManager.outsideAIDryNodesUnordered);
            }

            if (enemy.enemyType != null && enemy.enemyType.WaterType == EnemyWaterType.WaterOnly && HasUsableAINodes(roundManager.outsideAIWaterNodesUnordered))
            {
                return CleanAINodes(roundManager.outsideAIWaterNodesUnordered);
            }

            if (HasUsableAINodes(roundManager.outsideAINodesUnordered))
            {
                return CleanAINodes(roundManager.outsideAINodesUnordered);
            }

            return CleanAINodes(GameObject.FindGameObjectsWithTag("OutsideAINode"));
        }

        if (!HasUsableAINodes(roundManager.insideAINodes))
        {
            roundManager.insideAINodes = GameObject.FindGameObjectsWithTag("AINode");
        }

        if (HasUsableAINodes(roundManager.insideAINodes))
        {
            return CleanAINodes(roundManager.insideAINodes);
        }

        return CleanAINodes(GameObject.FindGameObjectsWithTag("AINode"));
    }

    private static GameObject[] ExpandAINodesForCaveDweller(GameObject[] nodes)
    {
        nodes = CleanAINodes(nodes);
        if (nodes.Length == 0 || nodes.Length >= 15)
        {
            return nodes;
        }

        GameObject[] expanded = new GameObject[15];
        for (int i = 0; i < expanded.Length; i++)
        {
            expanded[i] = nodes[i % nodes.Length];
        }

        return expanded;
    }

    private static GameObject[] CleanAINodes(GameObject[] nodes)
    {
        return nodes == null ? Array.Empty<GameObject>() : nodes.Where(node => node != null).ToArray();
    }

    private static bool HasUsableAINodes(GameObject[] nodes)
    {
        return nodes != null && nodes.Any(node => node != null);
    }

    private static void MarkCaveDwellerWaitingForNodes(CaveDwellerAI cave)
    {
        if (cave == null)
        {
            return;
        }

        cave.inSpecialAnimation = true;
        if (cave.NetworkObject != null)
        {
            caveDwellersWaitingForNodes.Add(cave.NetworkObject.NetworkObjectId);
        }
    }

    private static void ClearCaveDwellerWaitingForNodes(CaveDwellerAI cave)
    {
        if (cave == null || cave.NetworkObject == null)
        {
            return;
        }

        if (caveDwellersWaitingForNodes.Remove(cave.NetworkObject.NetworkObjectId) && !cave.inTransformingAnimation)
        {
            cave.inSpecialAnimation = false;
        }
    }

    private static bool IsCaveDwellerWaitingForNodes(CaveDwellerAI cave)
    {
        return cave != null && cave.NetworkObject != null && caveDwellersWaitingForNodes.Contains(cave.NetworkObject.NetworkObjectId);
    }

    private static void WriteEnemyAnimatorSnapshot(FastBufferWriter writer, EnemyAI enemy)
    {
        WriteAnimatorSnapshot(writer, enemy != null ? enemy.creatureAnimator : null);
    }

    private static void WriteAnimatorSnapshot(FastBufferWriter writer, Animator animator)
    {
        if (animator == null)
        {
            WriteInt(writer, 0);
            WriteInt(writer, 0);
            return;
        }

        List<EnemyAnimatorValue> values = new List<EnemyAnimatorValue>();
        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length && values.Count < MaxEnemyAnimatorParameters; i++)
        {
            AnimatorControllerParameter parameter = parameters[i];
            try
            {
                if (parameter.type == AnimatorControllerParameterType.Bool)
                {
                    values.Add(new EnemyAnimatorValue
                    {
                        Key = parameter.nameHash,
                        Kind = EnemyFieldBool,
                        BoolValue = animator.GetBool(parameter.nameHash)
                    });
                }
                else if (parameter.type == AnimatorControllerParameterType.Int)
                {
                    values.Add(new EnemyAnimatorValue
                    {
                        Key = parameter.nameHash,
                        Kind = EnemyFieldInt,
                        IntValue = animator.GetInteger(parameter.nameHash)
                    });
                }
                else if (parameter.type == AnimatorControllerParameterType.Float)
                {
                    values.Add(new EnemyAnimatorValue
                    {
                        Key = parameter.nameHash,
                        Kind = EnemyFieldFloat,
                        FloatValue = animator.GetFloat(parameter.nameHash)
                    });
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Enemy animator parameter snapshot skipped: {e.Message}");
            }
        }

        WriteInt(writer, values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            WriteInt(writer, values[i].Key);
            WriteInt(writer, values[i].Kind);
            if (values[i].Kind == EnemyFieldBool)
            {
                WriteBool(writer, values[i].BoolValue);
            }
            else if (values[i].Kind == EnemyFieldInt)
            {
                WriteInt(writer, values[i].IntValue);
            }
            else if (values[i].Kind == EnemyFieldFloat)
            {
                WriteFloat(writer, values[i].FloatValue);
            }
        }

        int layerCount = Mathf.Min(animator.layerCount, MaxEnemyAnimatorLayers);
        WriteInt(writer, layerCount);
        for (int i = 0; i < layerCount; i++)
        {
            AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(i);
            WriteInt(writer, stateInfo.fullPathHash);
            WriteFloat(writer, stateInfo.normalizedTime);
            WriteFloat(writer, animator.GetLayerWeight(i));
        }
    }

    private static void ReadAndApplyEnemyAnimatorSnapshot(ref FastBufferReader reader, EnemyAI enemy)
    {
        ReadAndApplyAnimatorSnapshot(ref reader, enemy != null ? enemy.creatureAnimator : null);
    }

    private static void ReadAndApplyAnimatorSnapshot(ref FastBufferReader reader, Animator animator)
    {
        int parameterCount = ReadInt(ref reader);
        for (int i = 0; i < parameterCount; i++)
        {
            int key = ReadInt(ref reader);
            int kind = ReadInt(ref reader);

            if (kind == EnemyFieldBool)
            {
                bool value = ReadBool(ref reader);
                if (animator != null && AnimatorHasParameter(animator, key, kind))
                {
                    animator.SetBool(key, value);
                }
            }
            else if (kind == EnemyFieldInt)
            {
                int value = ReadInt(ref reader);
                if (animator != null && AnimatorHasParameter(animator, key, kind))
                {
                    animator.SetInteger(key, value);
                }
            }
            else if (kind == EnemyFieldFloat)
            {
                float value = ReadFloat(ref reader);
                if (animator != null && AnimatorHasParameter(animator, key, kind))
                {
                    animator.SetFloat(key, value);
                }
            }
        }

        int layerCount = ReadInt(ref reader);
        for (int i = 0; i < layerCount; i++)
        {
            int stateHash = ReadInt(ref reader);
            float normalizedTime = ReadFloat(ref reader);
            float layerWeight = ReadFloat(ref reader);

            if (animator == null || i >= animator.layerCount)
            {
                continue;
            }

            try
            {
                if (stateHash != 0)
                {
                    animator.Play(stateHash, i, normalizedTime);
                }

                animator.SetLayerWeight(i, layerWeight);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Enemy animator layer apply skipped: {e.Message}");
            }
        }
    }

    private static bool AnimatorHasParameter(Animator animator, int key, int kind)
    {
        AnimatorControllerParameterType expectedType;
        if (kind == EnemyFieldBool)
        {
            expectedType = AnimatorControllerParameterType.Bool;
        }
        else if (kind == EnemyFieldInt)
        {
            expectedType = AnimatorControllerParameterType.Int;
        }
        else if (kind == EnemyFieldFloat)
        {
            expectedType = AnimatorControllerParameterType.Float;
        }
        else
        {
            return false;
        }

        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].nameHash == key && parameters[i].type == expectedType)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteEnemySpecificSnapshot(FastBufferWriter writer, EnemyAI enemy)
    {
        if (enemy is CaveDwellerAI caveDweller)
        {
            WriteInt(writer, EnemySpecificCaveDweller);
            WriteCaveDwellerSnapshot(writer, caveDweller);
            return;
        }

        WriteInt(writer, EnemySpecificNone);
    }

    private static void ReadAndApplyEnemySpecificSnapshot(ref FastBufferReader reader, EnemyAI enemy, StartOfRound round)
    {
        int kind = ReadInt(ref reader);
        if (kind == EnemySpecificCaveDweller)
        {
            ReadAndApplyCaveDwellerSnapshot(ref reader, enemy as CaveDwellerAI, round);
        }
    }

    private static void WriteCaveDwellerSnapshot(FastBufferWriter writer, CaveDwellerAI cave)
    {
        bool babyActive = cave.babyContainer != null && cave.babyContainer.activeSelf;
        bool adultActive = cave.adultContainer != null && cave.adultContainer.activeSelf;
        int observingPlayerId = GetPlayerSnapshotId(cave.observingPlayer);
        ulong observingScrapId = cave.observingScrap != null && cave.observingScrap.NetworkObject != null && cave.observingScrap.NetworkObject.IsSpawned
            ? cave.observingScrap.NetworkObject.NetworkObjectId
            : 0UL;
        ulong propNetworkId = cave.propScript != null && cave.propScript.NetworkObject != null && cave.propScript.NetworkObject.IsSpawned
            ? cave.propScript.NetworkObject.NetworkObjectId
            : 0UL;

        WriteBool(writer, babyActive);
        WriteBool(writer, adultActive);
        WriteInt(writer, (int)cave.babyState);
        WriteBool(writer, cave.sittingDown);
        WriteBool(writer, cave.babyRunning);
        WriteBool(writer, cave.babyCrying);
        WriteBool(writer, cave.rolledOver);
        WriteBool(writer, cave.babySquirming);
        WriteBool(writer, cave.holdingBaby);
        WriteBool(writer, cave.eatingScrap);
        WriteBool(writer, cave.hasPlayerFoundBaby);
        WriteInt(writer, cave.rockingBaby);
        WriteFloat(writer, cave.lonelinessMeter);
        WriteFloat(writer, cave.stressMeter);
        WriteFloat(writer, cave.growthMeter);
        WriteFloat(writer, cave.rockBabyTimer);
        WriteFloat(writer, cave.rollOverTimer);
        WriteVector3(writer, cave.pingAttentionPosition);
        WriteInt(writer, GetPlayerSnapshotId(cave.playerHolding));
        WriteInt(writer, observingPlayerId);
        WriteULong(writer, observingScrapId);
        WriteULong(writer, propNetworkId);
        WriteBool(writer, cave.propScript != null && cave.propScript.grabbable);
        WriteBool(writer, cave.propScript != null && cave.propScript.grabbableToEnemies);
        WriteBool(writer, cave.propScript != null && cave.propScript.enabled);
        WriteBool(writer, cave.propScript != null && cave.propScript.isHeld);
        WriteVector3(writer, cave.spine2 != null ? cave.spine2.localScale : Vector3.one);
        WriteBool(writer, cave.inSpecialAnimation);

        WriteBool(writer, cave.nearTransforming);
        WriteBool(writer, cave.inTransformingAnimation);
        WriteBool(writer, cave.isFakingBabyVoice);
        WriteBool(writer, cave.screaming);
        WriteBool(writer, cave.leaping);
        WriteBool(writer, cave.chasingAfterLeap);
        WriteBool(writer, cave.beganCooldown);
        WriteBool(writer, cave.clickingMandibles);
        WriteBool(writer, cave.pursuingPlayerInSneakMode);
        WriteBool(writer, cave.babyPuked);
        WriteFloat(writer, cave.pingAttentionTimer);
        WriteFloat(writer, cave.screamTimer);
        WriteFloat(writer, cave.leapTimer);
        WriteInt(writer, cave.focusLevel);
        WriteInt(writer, cave.scrapEaten);
        WriteVector3(writer, cave.runningFromPosition);
        WriteVector3(writer, cave.caveHidingSpot);
        WriteAnimatorSnapshot(writer, cave.babyCreatureAnimator);
    }

    private static void ReadAndApplyCaveDwellerSnapshot(ref FastBufferReader reader, CaveDwellerAI cave, StartOfRound round)
    {
        bool babyActive = ReadBool(ref reader);
        bool adultActive = ReadBool(ref reader);
        int babyState = ReadInt(ref reader);
        bool sittingDown = ReadBool(ref reader);
        bool babyRunning = ReadBool(ref reader);
        bool babyCrying = ReadBool(ref reader);
        bool rolledOver = ReadBool(ref reader);
        bool babySquirming = ReadBool(ref reader);
        bool holdingBaby = ReadBool(ref reader);
        bool eatingScrap = ReadBool(ref reader);
        bool hasPlayerFoundBaby = ReadBool(ref reader);
        int rockingBaby = ReadInt(ref reader);
        float lonelinessMeter = ReadFloat(ref reader);
        float stressMeter = ReadFloat(ref reader);
        float growthMeter = ReadFloat(ref reader);
        float rockBabyTimer = ReadFloat(ref reader);
        float rollOverTimer = ReadFloat(ref reader);
        Vector3 pingAttentionPosition = ReadVector3(ref reader);
        int holdingPlayerId = ReadInt(ref reader);
        int observingPlayerId = ReadInt(ref reader);
        ulong observingScrapId = ReadULong(ref reader);
        ulong propNetworkId = ReadULong(ref reader);
        bool propGrabbable = ReadBool(ref reader);
        bool propGrabbableToEnemies = ReadBool(ref reader);
        bool propEnabled = ReadBool(ref reader);
        bool propHeld = ReadBool(ref reader);
        Vector3 spineScale = ReadVector3(ref reader);
        bool inSpecialAnimation = ReadBool(ref reader);

        bool nearTransforming = ReadBool(ref reader);
        bool inTransformingAnimation = ReadBool(ref reader);
        bool isFakingBabyVoice = ReadBool(ref reader);
        bool screaming = ReadBool(ref reader);
        bool leaping = ReadBool(ref reader);
        bool chasingAfterLeap = ReadBool(ref reader);
        bool beganCooldown = ReadBool(ref reader);
        bool clickingMandibles = ReadBool(ref reader);
        bool pursuingPlayerInSneakMode = ReadBool(ref reader);
        bool babyPuked = ReadBool(ref reader);
        float pingAttentionTimer = ReadFloat(ref reader);
        float screamTimer = ReadFloat(ref reader);
        float leapTimer = ReadFloat(ref reader);
        int focusLevel = ReadInt(ref reader);
        int scrapEaten = ReadInt(ref reader);
        Vector3 runningFromPosition = ReadVector3(ref reader);
        Vector3 caveHidingSpot = ReadVector3(ref reader);

        if (cave == null)
        {
            ReadAndApplyAnimatorSnapshot(ref reader, null);
            return;
        }

        CaveDwellerPhysicsProp prop = ResolveCaveDwellerProp(cave, propNetworkId);

        bool caveNodesReady = EnsureCaveDwellerAINodes(cave);
        bool shouldBeAdult = adultActive || cave.currentBehaviourStateIndex != 0;
        if (shouldBeAdult && cave.currentBehaviourStateIndex == 0 && caveNodesReady)
        {
            ApplyEnemyBehaviourState(cave, 1);
        }

        if (shouldBeAdult && !caveNodesReady)
        {
            MarkCaveDwellerWaitingForNodes(cave);
        }
        else if (caveNodesReady)
        {
            ClearCaveDwellerWaitingForNodes(cave);
        }

        cave.babyState = Enum.IsDefined(typeof(BabyState), babyState) ? (BabyState)babyState : BabyState.Roaming;
        cave.sittingDown = sittingDown;
        cave.babyRunning = babyRunning;
        cave.babyCrying = babyCrying;
        cave.rolledOver = rolledOver;
        cave.babySquirming = babySquirming;
        cave.holdingBaby = holdingBaby;
        cave.eatingScrap = eatingScrap;
        cave.hasPlayerFoundBaby = hasPlayerFoundBaby;
        cave.rockingBaby = rockingBaby;
        cave.lonelinessMeter = lonelinessMeter;
        cave.stressMeter = stressMeter;
        cave.growthMeter = growthMeter;
        cave.rockBabyTimer = rockBabyTimer;
        cave.rollOverTimer = rollOverTimer;
        cave.pingAttentionPosition = pingAttentionPosition;
        cave.playerHolding = GetPlayerBySnapshotId(round, holdingPlayerId);
        if (cave.spine2 != null)
        {
            cave.spine2.localScale = spineScale;
        }

        PlayerControllerB observingPlayer = GetPlayerBySnapshotId(round, observingPlayerId);
        GrabbableObject observingScrap = null;
        if (observingScrapId != 0UL)
        {
            NetworkObject observingObject = TryGetNetworkObject(observingScrapId);
            if (observingObject != null)
            {
                observingObject.TryGetComponent(out observingScrap);
            }
        }

        cave.observingPlayer = observingPlayer;
        cave.observingScrap = observingScrap;
        if (observingScrap != null)
        {
            cave.observingObject = observingScrap.gameObject;
        }
        else if (observingPlayer != null && observingPlayer.gameplayCamera != null)
        {
            cave.observingObject = observingPlayer.gameplayCamera.gameObject;
        }
        else
        {
            cave.observingObject = null;
        }

        cave.nearTransforming = nearTransforming;
        cave.inTransformingAnimation = inTransformingAnimation;
        cave.inSpecialAnimation = inSpecialAnimation || inTransformingAnimation || IsCaveDwellerWaitingForNodes(cave);
        cave.isFakingBabyVoice = isFakingBabyVoice;
        cave.screaming = screaming;
        cave.leaping = leaping;
        cave.chasingAfterLeap = chasingAfterLeap;
        cave.beganCooldown = beganCooldown;
        cave.clickingMandibles = clickingMandibles;
        cave.pursuingPlayerInSneakMode = pursuingPlayerInSneakMode;
        cave.babyPuked = babyPuked;
        cave.pingAttentionTimer = pingAttentionTimer;
        cave.screamTimer = screamTimer;
        cave.leapTimer = leapTimer;
        cave.focusLevel = focusLevel;
        cave.scrapEaten = scrapEaten;
        cave.runningFromPosition = runningFromPosition;
        cave.caveHidingSpot = caveHidingSpot;

        ApplyCaveDwellerContainers(cave, babyActive, adultActive, shouldBeAdult, prop, propGrabbable, propGrabbableToEnemies, propEnabled, propHeld);
        ApplyCaveDwellerBabyState(cave, sittingDown, babyRunning, babyCrying, rolledOver, babySquirming, nearTransforming, isFakingBabyVoice);
        ApplyCaveDwellerAdultAnimatorState(cave, screaming, leaping, beganCooldown);
        ReadAndApplyAnimatorSnapshot(ref reader, cave.babyCreatureAnimator);
    }

    private static void ApplyCaveDwellerContainers(
        CaveDwellerAI cave,
        bool babyActive,
        bool adultActive,
        bool shouldBeAdult,
        CaveDwellerPhysicsProp prop,
        bool propGrabbable,
        bool propGrabbableToEnemies,
        bool propEnabled,
        bool propHeld)
    {
        bool adultVisible = shouldBeAdult || adultActive;
        bool babyVisible = !adultVisible;
        if (cave.babyContainer != null)
        {
            cave.babyContainer.SetActive(babyVisible);
        }

        if (cave.adultContainer != null)
        {
            cave.adultContainer.SetActive(adultVisible);
        }

        if (prop == null)
        {
            if (babyActive && babyVisible)
            {
                Plugin.Log.LogDebug("CaveDweller late-join snapshot had no prop to restore baby visuals.");
            }

            return;
        }

        if (adultVisible)
        {
            prop.grabbable = false;
            prop.grabbableToEnemies = false;
            prop.EnablePhysics(false);
            prop.EnableItemMeshes(false);
            prop.enabled = false;
            cave.syncMovementSpeed = 0.15f;
            cave.addPlayerVelocityToDestination = 1f;
            cave.updatePositionThreshold = 0.8f;
            if (cave.agent != null && !cave.inTransformingAnimation)
            {
                cave.agent.enabled = true;
            }

            return;
        }

        prop.grabbable = propGrabbable;
        prop.grabbableToEnemies = propGrabbableToEnemies;
        prop.enabled = propEnabled || !propHeld;
        if (!propHeld)
        {
            prop.EnableItemMeshes(true);
        }
    }

    private static CaveDwellerPhysicsProp ResolveCaveDwellerProp(CaveDwellerAI cave, ulong propNetworkId)
    {
        if (cave == null)
        {
            return null;
        }

        CaveDwellerPhysicsProp prop = null;
        if (propNetworkId != 0UL)
        {
            NetworkObject propNetworkObject = TryGetNetworkObject(propNetworkId);
            if (propNetworkObject != null)
            {
                propNetworkObject.TryGetComponent(out prop);
            }
        }

        if (prop == null)
        {
            prop = cave.propScript;
        }

        if (prop == null && cave.NetworkObject != null)
        {
            cave.NetworkObject.TryGetComponent(out prop);
        }

        if (prop == null)
        {
            prop = FindCaveDwellerPropFor(cave);
        }

        if (prop != null)
        {
            cave.propScript = prop;
            prop.caveDwellerScript = cave;
        }

        return prop;
    }

    private static CaveDwellerPhysicsProp FindCaveDwellerPropFor(CaveDwellerAI cave)
    {
        CaveDwellerPhysicsProp[] props = UnityEngine.Object.FindObjectsOfType<CaveDwellerPhysicsProp>(true);
        CaveDwellerPhysicsProp nearestUnbound = null;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < props.Length; i++)
        {
            CaveDwellerPhysicsProp prop = props[i];
            if (prop == null)
            {
                continue;
            }

            if (prop.caveDwellerScript == cave)
            {
                return prop;
            }

            if (prop.caveDwellerScript != null)
            {
                continue;
            }

            float distance = Vector3.SqrMagnitude(prop.transform.position - cave.transform.position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestUnbound = prop;
            }
        }

        return nearestUnbound;
    }

    private static IEnumerator RepairCaveDwellersAfterSnapshot(float seconds)
    {
        float start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < seconds)
        {
            RepairCaveDwellerVisuals();
            yield return new WaitForSeconds(0.5f);
        }

        RepairCaveDwellerVisuals();
    }

    private static void RepairCaveDwellerVisuals()
    {
        CaveDwellerAI[] caves = UnityEngine.Object.FindObjectsOfType<CaveDwellerAI>(true);
        for (int i = 0; i < caves.Length; i++)
        {
            CaveDwellerAI cave = caves[i];
            if (cave == null)
            {
                continue;
            }

            CaveDwellerPhysicsProp prop = ResolveCaveDwellerProp(cave, 0UL);
            bool nodesReady = EnsureCaveDwellerAINodes(cave);
            bool adultVisible = cave.currentBehaviourStateIndex != 0
                || (cave.adultContainer != null && cave.adultContainer.activeSelf && cave.babyContainer != null && !cave.babyContainer.activeSelf);
            if (adultVisible && !nodesReady)
            {
                MarkCaveDwellerWaitingForNodes(cave);
            }
            else if (nodesReady)
            {
                bool wasWaitingForNodes = IsCaveDwellerWaitingForNodes(cave);
                ClearCaveDwellerWaitingForNodes(cave);
                if (wasWaitingForNodes && adultVisible && cave.currentBehaviourStateIndex == 0)
                {
                    ApplyEnemyBehaviourState(cave, 1);
                }
            }

            bool propHeld = prop != null && prop.isHeld;
            ApplyCaveDwellerContainers(
                cave,
                !adultVisible,
                adultVisible,
                adultVisible,
                prop,
                prop != null && prop.grabbable,
                prop != null && prop.grabbableToEnemies,
                prop != null && prop.enabled,
                propHeld);
        }
    }

    private static void ApplyCaveDwellerBabyState(
        CaveDwellerAI cave,
        bool sittingDown,
        bool babyRunning,
        bool babyCrying,
        bool rolledOver,
        bool babySquirming,
        bool nearTransforming,
        bool isFakingBabyVoice)
    {
        RunWithRpcStage(cave, NetworkBehaviour.__RpcExecStage.Execute, () =>
        {
            cave.SetBabySittingClientRpc(sittingDown);
            cave.SetBabyRunningClientRpc(babyRunning);
            cave.SetBabyCryingClientRpc(babyCrying);
            cave.SetBabySquirmingClientRpc(babySquirming);
            cave.SetBabyRolledOverClientRpc(rolledOver, false);
            cave.SetFakingBabyVoiceClientRpc(isFakingBabyVoice);
            if (nearTransforming)
            {
                cave.SetBabyNearTransformingClientRpc();
            }
        });

        if (cave.babyCreatureAnimator == null)
        {
            return;
        }

        cave.babyCreatureAnimator.SetBool("Sitting", sittingDown);
        cave.babyCreatureAnimator.SetBool("BabyRunning", babyRunning);
        cave.babyCreatureAnimator.SetBool("HoldingBaby", cave.holdingBaby);
        cave.babyCreatureAnimator.SetBool("BabyCrying", babyCrying);
        cave.babyCreatureAnimator.SetBool("Squirming", babySquirming);
        cave.babyCreatureAnimator.SetBool("FallOver", rolledOver);
        cave.babyCreatureAnimator.SetBool("Transform", cave.inTransformingAnimation);
    }

    private static void ApplyCaveDwellerAdultAnimatorState(CaveDwellerAI cave, bool screaming, bool leaping, bool beganCooldown)
    {
        if (cave.creatureAnimator == null)
        {
            return;
        }

        cave.creatureAnimator.SetBool("Screaming", screaming);
        cave.creatureAnimator.SetBool("Leaping", leaping);
        cave.creatureAnimator.SetBool("FinishedLeaping", beganCooldown);
    }

    private static int GetPlayerSnapshotId(PlayerControllerB player)
    {
        return player != null ? (int)player.playerClientId : -1;
    }

    private static PlayerControllerB GetPlayerBySnapshotId(StartOfRound round, int playerId)
    {
        if (round == null || round.allPlayerScripts == null || playerId < 0 || playerId >= round.allPlayerScripts.Length)
        {
            return null;
        }

        return round.allPlayerScripts[playerId];
    }

    private static void ApplyPowerState(bool powerOn)
    {
        BreakerBox breakerBox = UnityEngine.Object.FindObjectOfType<BreakerBox>();
        if (breakerBox != null)
        {
            breakerBox.isPowerOn = powerOn;
        }

        if (RoundManager.Instance == null)
        {
            return;
        }

        RunWithRpcStage(RoundManager.Instance, NetworkBehaviour.__RpcExecStage.Execute, () =>
        {
            if (powerOn)
            {
                RoundManager.Instance.PowerSwitchOnClientRpc();
            }
            else
            {
                RoundManager.Instance.PowerSwitchOffClientRpc();
            }
        });
    }

    private static void RepairLocalPlayerReference()
    {
        if (NetworkManager.Singleton == null || StartOfRound.Instance == null || GameNetworkManager.Instance == null)
        {
            return;
        }

        ulong localClientId = NetworkManager.Singleton.LocalClientId;
        foreach (PlayerControllerB player in StartOfRound.Instance.allPlayerScripts)
        {
            if (player != null && player.actualClientId == localClientId)
            {
                StartOfRound.Instance.localPlayerController = player;
                GameNetworkManager.Instance.localPlayerController = player;
                return;
            }
        }
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

    private static void RunWithRpcStage(NetworkBehaviour behaviour, NetworkBehaviour.__RpcExecStage stage, Action action)
    {
        if (behaviour == null)
        {
            action();
            return;
        }

        NetworkBehaviour.__RpcExecStage previous = behaviour.__rpc_exec_stage;
        try
        {
            behaviour.__rpc_exec_stage = stage;
            action();
        }
        finally
        {
            behaviour.__rpc_exec_stage = previous;
        }
    }

    private static void WriteLevelSyncInfo(FastBufferWriter writer, LevelSyncInfo info)
    {
        WriteInt(writer, info.RandomSeed);
        WriteInt(writer, info.LevelId);
        WriteInt(writer, info.MoldIterations);
        WriteInt(writer, info.MoldStartPosition);
        WriteInt(writer, info.Weather);
        WriteIntArray(writer, info.DestroyedMold);
    }

    private static LevelSyncInfo ReadLevelSyncInfo(ref FastBufferReader reader)
    {
        return new LevelSyncInfo
        {
            RandomSeed = ReadInt(ref reader),
            LevelId = ReadInt(ref reader),
            MoldIterations = ReadInt(ref reader),
            MoldStartPosition = ReadInt(ref reader),
            Weather = ReadInt(ref reader),
            DestroyedMold = ReadIntArray(ref reader)
        };
    }

    private static void WriteIntArray(FastBufferWriter writer, int[] values)
    {
        values ??= Array.Empty<int>();
        WriteInt(writer, values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            WriteInt(writer, values[i]);
        }
    }

    private static int[] ReadIntArray(ref FastBufferReader reader)
    {
        int length = ReadInt(ref reader);
        int[] values = new int[Mathf.Max(0, length)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = ReadInt(ref reader);
        }
        return values;
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

    private struct EnemyAnimatorValue
    {
        public int Key;
        public int Kind;
        public bool BoolValue;
        public int IntValue;
        public float FloatValue;
    }

    private sealed class StoredPlayerState
    {
        public Vector3 WorldPosition;
        public Vector3 SyncedLocalPosition;
        public Vector3 WorldEuler;
        public bool IsInElevator;
        public bool IsInHangar;
        public bool IsCrouching;
        public bool IsPlayerDead;
        public int Health;
        public bool CriticallyInjured;
        public bool BleedingHeavily;
        public int SuitId;

        public static StoredPlayerState From(StartOfRound round, PlayerControllerB player)
        {
            Vector3 syncedLocalPosition = GetAuthoritativeLocalPlayerPosition(player);
            return new StoredPlayerState
            {
                WorldPosition = GetAuthoritativeWorldPlayerPosition(round, player, syncedLocalPosition),
                SyncedLocalPosition = syncedLocalPosition,
                WorldEuler = player.transform.eulerAngles,
                IsInElevator = player.isInElevator,
                IsInHangar = player.isInHangarShipRoom,
                IsCrouching = player.isCrouching,
                IsPlayerDead = player.isPlayerDead,
                Health = player.health,
                CriticallyInjured = player.criticallyInjured,
                BleedingHeavily = player.bleedingHeavily,
                SuitId = player.currentSuitID
            };
        }
    }

    private sealed class LevelSyncInfo
    {
        public int RandomSeed;
        public int LevelId;
        public int MoldIterations;
        public int MoldStartPosition;
        public int Weather;
        public int[] DestroyedMold;
    }
}
