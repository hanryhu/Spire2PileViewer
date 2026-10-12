using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace Spire2PileViewer;

/// <summary>Combat lifecycle hooks (same hook points BetterSpire2 uses for its overlays).</summary>
public static class Patches
{
    public static readonly Type[] All =
    {
        typeof(CombatSetUpPatch),
        typeof(CombatWinPatch),
        typeof(CombatLosePatch),
        typeof(ReturnToMainMenuPatch),
        typeof(AutoTest.MainMenuReadyPatch),
        typeof(AutoTest.LobbyReadyPatch),
        typeof(SettingsMenu.InputPatch),
    };

    [HarmonyPatch(typeof(CombatManager), "SetUpCombat")]
    public static class CombatSetUpPatch
    {
        private static void Postfix() => Safe("CombatSetUp", PileViewer.OnCombatSetUp);
    }

    [HarmonyPatch(typeof(CombatManager), "EndCombatInternal", new Type[] { })]
    public static class CombatWinPatch
    {
        private static void Prefix() => Safe("CombatWin", PileViewer.Hide);
    }

    [HarmonyPatch(typeof(CombatManager), "LoseCombat")]
    public static class CombatLosePatch
    {
        private static void Prefix() => Safe("CombatLose", PileViewer.Hide);
    }

    [HarmonyPatch(typeof(NGame), "ReturnToMainMenu")]
    public static class ReturnToMainMenuPatch
    {
        private static void Prefix() => Safe("ReturnToMainMenu", PileViewer.Hide);
    }

    private static void Safe(string context, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Error(context, ex);
        }
    }
}
