using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameRestingManagerPatcher
{
    private const string PartyTooSpreadOut = "Failure/&FailureFlagPartyTooSpreadOut";

    //PATCH: allows a short rest while the party is spread out, but not during a conversation or cutscene
    [HarmonyPatch(typeof(GameRestingManager), nameof(GameRestingManager.CanExecuteLocationRest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanExecuteLocationRest_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RestType restType, ref string failureFlag, ref bool __result)
        {
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
        public static void Postfix(RestType restType, ref string failureFlag, ref bool __result)
        {
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
