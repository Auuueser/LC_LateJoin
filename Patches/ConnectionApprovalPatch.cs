using HarmonyLib;
using System;
using System.Text;
using Unity.Netcode;
using static Unity.Netcode.NetworkManager;

namespace LC_LateJoin.Patches;

[HarmonyPatch(typeof(GameNetworkManager), "ConnectionApproval")]
internal static class ConnectionApprovalPatch
{
    [HarmonyPriority(Priority.First)]
    [HarmonyPrefix]
    private static bool Prefix(ConnectionApprovalRequest request, ConnectionApprovalResponse response)
    {
        LateJoinSyncManager.EnsureNetworkHandlersRegistered();
        LateJoinComprehensiveSyncManager.EnsureNetworkHandlersRegistered();

        if (NetworkManager.Singleton == null || request.ClientNetworkId == NetworkManager.Singleton.LocalClientId)
        {
            return true;
        }

        if (GameNetworkManager.Instance == null || !GameNetworkManager.Instance.gameHasStarted)
        {
            return true;
        }

        if (!TryValidateBaseConnection(request, response, out string identityKey))
        {
            return false;
        }

        if (!LateJoinComprehensiveSyncManager.IsStableLandedForLateJoin(out string reason))
        {
            Plugin.Log.LogInfo($"Late join approval blocked for client {request.ClientNetworkId}: {reason}");
            response.Reason = $"Ship is not ready for late joining: {reason}";
            response.Approved = false;
            response.CreatePlayerObject = false;
            response.Pending = false;
            return false;
        }

        response.Reason = string.Empty;
        response.Approved = true;
        response.CreatePlayerObject = false;
        response.Pending = false;
        LateJoinSyncManager.RecordApprovedClientIdentity(request.ClientNetworkId, identityKey);
        LateJoinComprehensiveSyncManager.TrackApprovedLateJoinClient(request.ClientNetworkId);
        Plugin.Log.LogInfo($"Approved stable landed late join for client {request.ClientNetworkId}");
        return false;
    }

    [HarmonyPriority(Priority.Last)]
    [HarmonyPostfix]
    private static void Postfix(ConnectionApprovalRequest request, ConnectionApprovalResponse response)
    {
        if (NetworkManager.Singleton == null || request.ClientNetworkId == NetworkManager.Singleton.LocalClientId)
        {
            return;
        }

        if (response.Approved)
        {
            LateJoinSyncManager.RecordApprovedClientIdentity(request.ClientNetworkId, ExtractIdentityKey(request));
            if (GameNetworkManager.Instance != null && GameNetworkManager.Instance.gameHasStarted)
            {
                LateJoinComprehensiveSyncManager.TrackApprovedLateJoinClient(request.ClientNetworkId);
            }
            return;
        }

        if (response.Reason != "Game has already started!")
        {
            return;
        }

        if (!LateJoinComprehensiveSyncManager.IsStableLandedForLateJoin(out string reason))
        {
            Plugin.Log.LogInfo($"Late join approval blocked for client {request.ClientNetworkId}: {reason}");
            response.Reason = $"Ship is not ready for late joining: {reason}";
            response.Approved = false;
            response.CreatePlayerObject = false;
            response.Pending = false;
            return;
        }

        response.Approved = true;
        response.Reason = string.Empty;
        response.CreatePlayerObject = false;
        response.Pending = false;
        LateJoinSyncManager.RecordApprovedClientIdentity(request.ClientNetworkId, ExtractIdentityKey(request));
        LateJoinComprehensiveSyncManager.TrackApprovedLateJoinClient(request.ClientNetworkId);
        Plugin.Log.LogInfo($"Approved stable landed late join for client {request.ClientNetworkId}");
    }

    private static bool TryValidateBaseConnection(ConnectionApprovalRequest request, ConnectionApprovalResponse response, out string identityKey)
    {
        string payload;
        try
        {
            payload = request.Payload != null ? Encoding.ASCII.GetString(request.Payload) : string.Empty;
        }
        catch
        {
            payload = string.Empty;
        }

        string[] parts = payload.Split(new[] { ',' }, StringSplitOptions.None);
        identityKey = ExtractIdentityKey(parts, request.ClientNetworkId);

        if (string.IsNullOrWhiteSpace(payload) || parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            response.Reason = "Unknown; please verify your game files.";
            response.Approved = false;
            response.CreatePlayerObject = false;
            response.Pending = false;
            return false;
        }

        if (GameNetworkManager.Instance.disallowConnection)
        {
            response.Reason = "The host was not accepting connections.";
            response.Approved = false;
            response.CreatePlayerObject = false;
            response.Pending = false;
            return false;
        }

        int maxPlayers = StartOfRound.Instance != null && StartOfRound.Instance.allPlayerScripts != null
            ? StartOfRound.Instance.allPlayerScripts.Length
            : 4;
        if (GameNetworkManager.Instance.connectedPlayers >= maxPlayers)
        {
            response.Reason = "Lobby is full!";
            response.Approved = false;
            response.CreatePlayerObject = false;
            response.Pending = false;
            return false;
        }

        if (GameNetworkManager.Instance.gameVersionNum.ToString() != parts[0])
        {
            response.Reason = $"Game version mismatch! Their version: {GameNetworkManager.Instance.gameVersionNum}. Your version: {parts[0]}";
            response.Approved = false;
            response.CreatePlayerObject = false;
            response.Pending = false;
            return false;
        }

        if (!GameNetworkManager.Instance.disableSteam)
        {
            if (StartOfRound.Instance == null || parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]) || !ulong.TryParse(parts[1], out ulong steamId))
            {
                response.Reason = "Unknown Steam identity; please verify your game files.";
                response.Approved = false;
                response.CreatePlayerObject = false;
                response.Pending = false;
                return false;
            }

            if (StartOfRound.Instance.KickedClientIds.Contains(steamId))
            {
                response.Reason = "You cannot rejoin after being kicked.";
                response.Approved = false;
                response.CreatePlayerObject = false;
                response.Pending = false;
                return false;
            }
        }

        return true;
    }

    private static string ExtractIdentityKey(ConnectionApprovalRequest request)
    {
        string payload;
        try
        {
            payload = request.Payload != null ? Encoding.ASCII.GetString(request.Payload) : string.Empty;
        }
        catch
        {
            payload = string.Empty;
        }

        string[] parts = payload.Split(new[] { ',' }, StringSplitOptions.None);
        return ExtractIdentityKey(parts, request.ClientNetworkId);
    }

    private static string ExtractIdentityKey(string[] parts, ulong clientId)
    {
        if (GameNetworkManager.Instance != null
            && !GameNetworkManager.Instance.disableSteam
            && parts != null
            && parts.Length >= 2
            && !string.IsNullOrWhiteSpace(parts[1]))
        {
            return parts[1];
        }

        return clientId.ToString();
    }
}
