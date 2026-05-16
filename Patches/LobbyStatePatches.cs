using HarmonyLib;
using UnityEngine;

namespace LC_LateJoin.Patches;

[HarmonyPatch]
internal static class LobbyStatePatches
{
    [HarmonyPatch(typeof(GameNetworkManager), "LeaveLobbyAtGameStart")]
    [HarmonyPrefix]
    private static bool KeepSteamLobbyAtGameStart()
    {
        LateJoinSyncManager.SetLobbyJoinable(false);
        return false;
    }

    [HarmonyPatch(typeof(StartOfRound), "StartGame")]
    [HarmonyPrefix]
    private static void StartGamePrefix()
    {
        LateJoinSyncManager.SetLobbyJoinable(false);
    }

    [HarmonyPatch(typeof(StartOfRound), "OnShipLandedMiscEvents")]
    [HarmonyPostfix]
    private static void OnShipLandedPostfix()
    {
        LateJoinSyncManager.RefreshJoinableState();
    }

    [HarmonyPatch(typeof(RoundManager), "FinishGeneratingNewLevelClientRpc")]
    [HarmonyPostfix]
    private static void FinishGeneratingNewLevelPostfix()
    {
        LateJoinSyncManager.RefreshJoinableState();
    }

    [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
    [HarmonyPrefix]
    private static void ShipLeavePrefix()
    {
        LateJoinSyncManager.SetLobbyJoinable(false);
    }

    [HarmonyPatch(typeof(StartOfRound), "OnClientConnect")]
    [HarmonyPostfix]
    private static void OnClientConnectPostfix(StartOfRound __instance, ulong clientId)
    {
        LateJoinSyncManager.HandleServerClientConnected(__instance, clientId);
    }

    [HarmonyPatch(typeof(StartOfRound), "OnClientDisconnect")]
    [HarmonyPostfix]
    private static void OnClientDisconnectPostfix()
    {
        LateJoinSyncManager.RefreshJoinableState();
    }

    [HarmonyPatch(typeof(StartOfRound), "OnPlayerDC")]
    [HarmonyPrefix]
    private static void OnPlayerDCPrefix(StartOfRound __instance, int playerObjectNumber, ulong clientId)
    {
        LateJoinSyncManager.CacheDisconnectState(__instance, playerObjectNumber, clientId);
    }

    [HarmonyPatch(typeof(QuickMenuManager), "DisableInviteFriendsButton")]
    [HarmonyPrefix]
    private static bool DisableInviteFriendsButtonPrefix(QuickMenuManager __instance)
    {
        if (!LateJoinSyncManager.ShouldExposeInviteButton())
        {
            return true;
        }

        if (__instance.inviteFriendsTextAlpha != null)
        {
            __instance.inviteFriendsTextAlpha.alpha = 1f;
        }

        return false;
    }

    [HarmonyPatch(typeof(QuickMenuManager), "InviteFriendsButton")]
    [HarmonyPrefix]
    private static bool InviteFriendsButtonPrefix()
    {
        if (!LateJoinSyncManager.ShouldExposeInviteButton())
        {
            return true;
        }

        if (!GameNetworkManager.Instance.disableSteam)
        {
            GameNetworkManager.Instance.InviteFriendsUI();
        }

        return false;
    }
}
