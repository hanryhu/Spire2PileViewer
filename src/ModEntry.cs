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
#if DEBUG
        if (AutoTest.Harness)
        {
            // Test fixtures only: none of the mod's own patches, so this instance is like a teammate without the
            // Pile Viewer that the auto-test can still drive (see dev-autotest-mp.ps1 -Suite harness).
            new Harmony("com.hanryhu.spire2pileviewer.harness").CreateClassProcessor(typeof(AutoTest.LobbyReadyPatch)).Patch();
            ModLog.Info("ModEntry: harness instance (test fixtures only, no Pile Viewer)");
            return;
        }
#endif
        Settings.Load();
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
