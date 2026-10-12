using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Spire2PileViewer;

[ModInitializer("Init")]
public static class ModEntry
{
    private static bool _initialized;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        ModLog.Init();
        ModLog.Info("ModEntry.Init() starting");

        var harmony = new Harmony("com.hanryhu.spire2pileviewer");
        int ok = 0, failed = 0;
        foreach (var type in Patches.All)
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                ModLog.Info("  Patched: " + type.Name);
                ok++;
            }
            catch (Exception ex)
            {
                ModLog.Error("  Patch failed: " + type.Name, ex);
                failed++;
            }
        }
        ModLog.Info($"Harmony patching complete: {ok} succeeded, {failed} failed");
    }
}
