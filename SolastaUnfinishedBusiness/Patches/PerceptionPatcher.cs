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
                    currentBestDecisionScore, neededScoreFactorOverride, resetBetweenAcitivities, forceLocalComputation),
                resetBetweenAcitivities ? null : () => KeepPackageState(__instance));
        }

        // a legendary creature decides each legendary action's package in turn, and only the first decision resets
        // the brain: the later ones keep the earlier packages' scores and the contexts already built, so the best of
        // all packages wins. A new decision for the same package must start from that state, not from the scores and
        // up-to-date contexts the discarded decision added with checks still missing (its scores are sorted and cut
        // in place, so they can't be told apart afterwards). Contexts not up to date at that point are built again;
        // they are new objects, so the kept scores still point at the contexts they were scored with. The situational
        // caches kept between packages (threat, relevant enemies, AoE positions) read the game's own perception
        // (PerceivedFoes, CanSenseTarget), not the 10-cell rule, so they stay.
        private static Action KeepPackageState(Brain brain)
        {
            var scoredResults = AiRetryState.Keep(brain.scoredResults);
            var contextUpToDateStatus = AiRetryState.Keep(brain.contextUpToDateStatus);
            var selectedDecision = brain.selectedDecision;

            return () =>
            {
                scoredResults();
                contextUpToDateStatus();
                brain.selectedDecision = selectedDecision;
            };
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

    //PATCH: player requests go out the frame they are made (after this frame's hover); the AI's in short windows
    [HarmonyPatch(typeof(ViewManager), nameof(ViewManager.Update))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ViewManager_Update
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            PerceptionContext.FlushRequests();

            // the cursor note for a pending check is created the first time one is needed
            if (PerceptionContext.HoverPending)
            {
                PerceptionPendingCue.Ensure();
            }
        }
    }
}
