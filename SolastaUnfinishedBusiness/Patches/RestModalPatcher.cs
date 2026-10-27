using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RestModalPatcher
{
    // a rest the game allows waits for every other player's approval first
    private static bool MustWaitForApproval(RestType restType, Action proceed)
    {
        var failure = string.Empty;

        if (ServiceRepository.GetService<IGameRestingService>()?.CanExecuteLocationRest(restType, ref failure, true) !=
            true)
        {
            return false;
        }

        return MultiplayerApprovalContext.MustWaitForApproval(
            restType == RestType.ShortRest
                ? MultiplayerApprovalContext.ApprovalAction.ShortRest
                : MultiplayerApprovalContext.ApprovalAction.LongRest,
            string.Empty, null, MultiplayerApprovalContext.RestSeconds(restType), proceed);
    }

    //PATCH: asks every player to approve a short rest (MULTIPLAYER)
    [HarmonyPatch(typeof(RestModal), nameof(RestModal.OnStartShortRestCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnStartShortRestCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RestModal __instance)
        {
            GameRestingManagerPatcher.InRestScreen = true;

            return !MustWaitForApproval(RestType.ShortRest, __instance.OnStartShortRestCb);
        }

        [UsedImplicitly]
        public static void Finalizer()
        {
            GameRestingManagerPatcher.InRestScreen = false;
        }
    }

    //PATCH: asks every player to approve a long rest (MULTIPLAYER)
    [HarmonyPatch(typeof(RestModal), nameof(RestModal.OnStartLongRestCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnStartLongRestCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RestModal __instance)
        {
            GameRestingManagerPatcher.InRestScreen = true;

            //PATCH: when the party already rested today, waits until it can rest again, then rests
            if (LongRestWaitContext.TryWaitThenRest(__instance))
            {
                return false;
            }

            return !MustWaitForApproval(RestType.LongRest, __instance.OnStartLongRestCb);
        }

        [UsedImplicitly]
        public static void Finalizer()
        {
            GameRestingManagerPatcher.InRestScreen = false;
        }
    }

    //PATCH: the rest screen's start buttons are enabled for every player, not only the host (MULTIPLAYER)
    [HarmonyPatch(typeof(RestBeforePanel), nameof(RestBeforePanel.SetRestType))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetRestType_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            GameRestingManagerPatcher.InRestScreen = true;
        }

        [UsedImplicitly]
        public static void Finalizer()
        {
            GameRestingManagerPatcher.InRestScreen = false;
        }
    }
}
