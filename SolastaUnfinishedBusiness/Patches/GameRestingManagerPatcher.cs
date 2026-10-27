using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameRestingManagerPatcher
{
    private const string PartyTooSpreadOut = "Failure/&FailureFlagPartyTooSpreadOut";

    // set while the rest screen checks or starts a rest; rests started by functors on every computer keep the game's
    // host-only rule
    internal static bool InRestScreen { get; set; }

    //PATCH: allows a short rest while the party is spread out, but not during a conversation or cutscene
    //PATCH: lets every player start a rest from the rest screen, not only the host (MULTIPLAYER)
    [HarmonyPatch(typeof(GameRestingManager), nameof(GameRestingManager.CanExecuteLocationRest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanExecuteLocationRest_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            RestType restType, ref string failureFlag, ref bool shouldCheckPlayerPrivileges, ref bool __result)
        {
            if (InRestScreen && Main.Settings.MultiplayerAllowPlayersToStartRests)
            {
                shouldCheckPlayerPrivileges = false;
            }

            if (!Main.Settings.AllowShortRestWithPartySpreadOut ||
                restType != RestType.ShortRest ||
                ServiceRepository.GetService<INarrativeDirectionService>() is not { IsSequenceInProgress: true })
            {
                return true;
            }

            failureFlag = "Failure/&FailureFlagRestInConversation";
            __result = false;

            return false;
        }

        // the distance check is the game's last one, so passing it leaves every other rule in place
        [UsedImplicitly]
        public static void Postfix(
            GameRestingManager __instance,
            RestType restType,
            ref string failureFlag,
            bool shouldCheckPlayerPrivileges,
            ref bool __result)
        {
            //PATCH: on the rest screen, a long rest the party already took today waits until it can rest again
            if (InRestScreen && restType == RestType.LongRest && !LongRestWaitContext.IgnoreRestFrequency)
            {
                LongRestWaitContext.NeedsWait = !__result &&
                                                LongRestWaitContext.OnlyRestedToday(__instance, failureFlag,
                                                    shouldCheckPlayerPrivileges);

                if (LongRestWaitContext.NeedsWait)
                {
                    failureFlag = string.Empty;
                    __result = true;
                }
            }

            if (__result ||
                !Main.Settings.AllowShortRestWithPartySpreadOut ||
                restType != RestType.ShortRest ||
                failureFlag != PartyTooSpreadOut)
            {
                return;
            }

            failureFlag = string.Empty;
            __result = true;
        }
    }
}
