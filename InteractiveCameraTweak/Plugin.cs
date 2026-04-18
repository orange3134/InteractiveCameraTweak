using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisResoniteWrapper;
using HarmonyLib;

namespace InteractiveCameraTweak;

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    public override void Load()
    {
        Log = base.Log;
        ResoniteHooks.OnEngineReady += OnEngineReady;
        Log.LogInfo($"Plugin {PluginMetadata.GUID} is loaded!");
    }

    private void OnEngineReady()
    {
        var harmony = new Harmony(PluginMetadata.GUID);
        harmony.PatchAll();
        Log.LogInfo("InteractiveCameraPatch applied.");
    }
}
