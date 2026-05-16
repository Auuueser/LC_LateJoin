using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace LC_LateJoin;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.local.lc_latejoin";
    public const string PluginName = "LC Late Join";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log { get; private set; }
    internal static Plugin Instance { get; private set; }

    private Harmony harmony;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        LateJoinSyncManager.Initialize(this);

        harmony = new Harmony(PluginGuid);
        harmony.PatchAll();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded");
    }
}
