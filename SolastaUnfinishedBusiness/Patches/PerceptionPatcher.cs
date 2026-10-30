using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using TA.AI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class PerceptionPatcher
{
    //PATCH: action steps are synchronized points where a missing perception check may be rolled (MULTIPLAYER)
    [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.Execute))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterAction_Execute
    {
        [HarmonyPriority(Priority.Last)]
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, CharacterAction __instance)
        {
            return PerceptionContext.WrapAction(values, __instance);
        }
    }

    //PATCH: the AI waits for the shared perception checks its decision needs, then decides again (MULTIPLAYER)
    [HarmonyPatch(typeof(Brain), nameof(Brain.DecideNextActivity))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Brain_DecideNextActivity
    {
        [HarmonyPriority(Priority.Last)]
        [UsedImplicitly]
        public static IEnumerator Postfix(
            IEnumerator values,
            Brain __instance,
            float currentBestDecisionScore,
            float neededScoreFactorOverride,
            bool resetBetweenAcitivities,
            bool forceLocalComputation)
        {
            if (PerceptionContext.CreatingAiRerun)
            {
                return values;
            }

            return PerceptionContext.WrapAiDecision(values, () => __instance.DecideNextActivity(
                currentBestDecisionScore, neededScoreFactorOverride, resetBetweenAcitivities, forceLocalComputation));
        }
    }

    //PATCH: perception checks aren't saved; every computer starts empty after a load
    [HarmonyPatch(typeof(Game), nameof(Game.PostLoad))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Game_PostLoad
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            PerceptionContext.Reset("load");
        }
    }

    //PATCH: and after leaving a location, so another map can't inherit its pairs
    [HarmonyPatch(typeof(GameLocationManager), nameof(GameLocationManager.UnloadLocationAsync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocationManager_UnloadLocationAsync
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values)
        {
            try
            {
                while (values.MoveNext())
                {
                    yield return values.Current;
                }
            }
            finally
            {
                (values as IDisposable)?.Dispose();
                PerceptionContext.Reset("location unloaded");
            }
        }
    }

    //PATCH: player requests go out the frame they are made (after this frame's hover)
    [HarmonyPatch(typeof(ViewManager), nameof(ViewManager.Update))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ViewManager_Update
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            PerceptionContext.FlushPlayerRequests();

            // the cursor note for a pending check is created the first time one is needed
            if (PerceptionContext.HoverPending)
            {
                PerceptionPendingCue.Ensure();
            }
        }
    }
}
