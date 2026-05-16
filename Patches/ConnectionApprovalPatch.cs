using HarmonyLib;
using System;
using System.Text;
using Unity.Netcode;
using static Unity.Netcode.NetworkManager;

namespace LC_LateJoin.Patches;

[HarmonyPatch(typeof(GameNetworkManager), "ConnectionApproval")]
internal static class ConnectionApprovalPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ConnectionApprovalRequest request, ConnectionApprovalResponse response)
    {
        LateJoinSyncManager.EnsureNetworkHandlersRegistered();

        if (request.ClientNetworkId == NetworkManager.Singleton.LocalClientId)
        {
            return true;
        }

        if (!GameNetworkManager.Instance.gameHasStarted)
        {
            return true;
        }

        if (!TryValidateBaseConnection(request, response, out string identityKey))
        {
            return false;
        }

        if (!LateJoinSyncManager.CanApproveLateJoin(out string reason))
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
        Plugin.Log.LogInfo($"Approved late join for client {request.ClientNetworkId}");
        return false;
    }

    [HarmonyPostfix]
    private static void Postfix(ConnectionApprovalRequest request, ConnectionApprovalResponse response)
    {
        if (request.ClientNetworkId == NetworkManager.Singleton.LocalClientId)
        {
            return;
        }

        if (response.Approved)
        {
            LateJoinSyncManager.RecordApprovedClientIdentity(request.ClientNetworkId, ExtractIdentityKey(request));
            return;
        }

        if (response.Reason != "Game has already started!")
        {
            return;
        }

        if (!LateJoinSyncManager.CanApproveLateJoin(out string reason))
        {
            Plugin.Log.LogInfo($"Late join approval blocked for client {request.ClientNetworkId}: {reason}");
            response.Reason = $"Ship is not ready for late joining: {reason}";
            return;
        }

        response.Approved = true;
        response.Reason = string.Empty;
        LateJoinSyncManager.RecordApprovedClientIdentity(request.ClientNetworkId, ExtractIdentityKey(request));
        Plugin.Log.LogInfo($"Approved landed late join for client {request.ClientNetworkId}");
    }

    private static bool TryValidateBaseConnection(ConnectionApprovalRequest request, ConnectionApprovalResponse response, out string identityKey)
    {
        string payload = Encoding.ASCII.GetString(request.Payload);
        string[] parts = payload.Split(new[] { ',' }, StringSplitOptions.None);
        identityKey = ExtractIdentityKey(parts, request.ClientNetworkId);

        if (string.IsNullOrEmpty(payload))
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

        if (GameNetworkManager.Instance.connectedPlayers >= 4)
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

        if (!GameNetworkManager.Instance.disableSteam
            && (StartOfRound.Instance == null
                || parts.Length < 2
                || StartOfRound.Instance.KickedClientIds.Contains((ulong)Convert.ToInt64(parts[1]))))
        {
            response.Reason = "You cannot rejoin after being kicked.";
            response.Approved = false;
            response.CreatePlayerObject = false;
            response.Pending = false;
            return false;
        }

        return true;
    }

    private static string ExtractIdentityKey(ConnectionApprovalRequest request)
    {
        string payload = Encoding.ASCII.GetString(request.Payload);
        string[] parts = payload.Split(new[] { ',' }, StringSplitOptions.None);
        return ExtractIdentityKey(parts, request.ClientNetworkId);
    }

    private static string ExtractIdentityKey(string[] parts, ulong clientId)
    {
        if (!GameNetworkManager.Instance.disableSteam && parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            return parts[1];
        }

        return clientId.ToString();
    }
}
