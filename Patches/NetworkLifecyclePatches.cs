using HarmonyLib;

namespace LC_LateJoin.Patches;

[HarmonyPatch]
internal static class NetworkLifecyclePatches
{
    [HarmonyPatch(typeof(GameNetworkManager), "OnEnable")]
    [HarmonyPostfix]
    private static void GameNetworkManagerOnEnablePostfix()
    {
        LateJoinSyncManager.EnsureNetworkHandlersRegistered();
    }

    [HarmonyPatch(typeof(GameNetworkManager), "Start")]
    [HarmonyPostfix]
    private static void GameNetworkManagerStartPostfix()
    {
        LateJoinSyncManager.EnsureNetworkHandlersRegistered();
    }

    [HarmonyPatch(typeof(StartOfRound), "OnEnable")]
    [HarmonyPostfix]
    private static void StartOfRoundOnEnablePostfix()
    {
        LateJoinSyncManager.EnsureNetworkHandlersRegistered();
    }

    [HarmonyPatch(typeof(StartOfRound), "Start")]
    [HarmonyPostfix]
    private static void StartOfRoundStartPostfix()
    {
        LateJoinSyncManager.EnsureNetworkHandlersRegistered();
    }

    [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
    [HarmonyPrefix]
    private static void DisconnectPrefix()
    {
        LateJoinSyncManager.Shutdown();
    }

    [HarmonyPatch(typeof(StartOfRound), "OnLocalDisconnect")]
    [HarmonyPrefix]
    private static void OnLocalDisconnectPrefix()
    {
        LateJoinSyncManager.Shutdown();
    }
}
