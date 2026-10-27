using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using TA;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class MultiplayerPatcher
{
    //PATCH: starts locations with a smaller command delay (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkTick), nameof(NetworkTick.ResetTickBuffer))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ResetTickBuffer_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(NetworkTick __instance)
        {
            if (!Main.Settings.MultiplayerEnableTickBufferTuning)
            {
                return true;
            }

            MultiplayerContext.ResetTickBuffer(__instance);

            return false;
        }
    }

    //PATCH: lowers the command delay sooner and further when every peer's ping allows it (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkTick), nameof(NetworkTick.UpdateTickBufferValue))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UpdateTickBufferValue_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(NetworkTick __instance, ref byte wantedTickBuffer, ref bool __result)
        {
            if (!Main.Settings.MultiplayerEnableTickBufferTuning)
            {
                return true;
            }

            __result = MultiplayerContext.UpdateTickBufferValue(__instance, out wantedTickBuffer);

            return false;
        }
    }

    //PATCH: forgets desync checks and pending key states from a previous location (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkTick), nameof(NetworkTick.ResetTick))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ResetTick_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            MultiplayerContext.ResetDesyncDetection();
            MultiplayerContext.ClearLocalShiftKeyState();
        }
    }

    //PATCH: sends the SHIFT key state with cast commands (MULTIPLAYER)
    [HarmonyPatch(typeof(CharacterActionParams), nameof(CharacterActionParams.SerializeElements))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterActionParamsSerializeElements_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterActionParams __instance, IElementsSerializer serializer)
        {
            MultiplayerContext.SerializeShiftKeyState(__instance, serializer);
        }
    }

    //PATCH: applies the received SHIFT key state when a cast executes, on every computer (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.ExecuteAction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LocalExecuteAction_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterActionParams characterActionParams)
        {
            MultiplayerContext.ApplyShiftKeyState(characterActionParams);
        }
    }

    //PATCH: applies the received SHIFT key state when an instant cast executes, on every computer (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.ExecuteInstantSingleAction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LocalExecuteInstantSingleAction_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterActionParams characterActionParams)
        {
            MultiplayerContext.ApplyShiftKeyState(characterActionParams);
        }
    }

    //PATCH: sends the SHIFT key state with reaction answers (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.ProcessReactionRequest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SendProcessReactionRequest_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ReactionRequest reactionRequest, ref int subOption)
        {
            subOption = MultiplayerContext.EncodeShiftKeyState(subOption, reactionRequest?.Character);
        }
    }

    //PATCH: applies the received SHIFT key state before the reaction is handled, on every computer (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.ProcessReactionRequest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LocalProcessReactionRequest_Patch
    {
        [HarmonyPriority(Priority.First)]
        [UsedImplicitly]
        public static void Prefix(ReactionRequest reactionRequest, ref int subOption)
        {
            subOption = MultiplayerContext.DecodeShiftKeyState(subOption, reactionRequest?.Character);
        }
    }

    //PATCH: sends the SHIFT key state with slot to sorcery points conversions (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.CreateSorceryPoints))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SendCreateSorceryPoints_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetCharacter character, ref int slotLevel)
        {
            slotLevel = MultiplayerContext.EncodeShiftKeyState(slotLevel, GameLocationCharacter.GetFromActor(character));
        }
    }

    //PATCH: applies the received SHIFT key state before the conversion spends a slot, on every computer (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.CreateSorceryPoints))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LocalCreateSorceryPoints_Patch
    {
        [HarmonyPriority(Priority.First)]
        [UsedImplicitly]
        public static void Prefix(RulesetCharacter character, ref int slotLevel)
        {
            slotLevel = MultiplayerContext.DecodeShiftKeyState(slotLevel, GameLocationCharacter.GetFromActor(character));
        }
    }

    //PATCH: periodically shares the entity guid so peers can detect a desync (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.FixedUpdate))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FixedUpdate_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NetworkingManager __instance)
        {
            MultiplayerContext.SendDesyncCheckIfNeeded(__instance);
        }
    }

    //PATCH: compares shared entity guids from every peer and alerts on mismatch (MULTIPLAYER)
    [HarmonyPatch(typeof(RulesetEntityManager), nameof(RulesetEntityManager.CheckCurEntityGuidByTick))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CheckCurEntityGuidByTick_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ulong localTick, ulong incomingGuid)
        {
            if (!Main.Settings.MultiplayerEnableDesyncDetection)
            {
                return true;
            }

            MultiplayerContext.CheckCurEntityGuidByTick(localTick, incomingGuid);

            return false;
        }
    }

    //PATCH: lets characters share cells instead of queuing behind each other (MULTIPLAYER)
    [HarmonyPatch(typeof(CharacterActionMoveStepBase), nameof(CharacterActionMoveStepBase.WaitForCanMove))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class WaitForCanMove_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterActionMoveStepBase __instance, int3 position, ref bool ignoreOccupants)
        {
            if (!ignoreOccupants && MultiplayerContext.CanIgnoreOccupantsAt(__instance.ActingCharacter, position))
            {
                ignoreOccupants = true;
            }
        }
    }

    //PATCH: picks the target from the game's synced random instead of the unsynced global one (MULTIPLAYER)
    [HarmonyPatch(typeof(CharacterActionStandAgainstTheTide),
        nameof(CharacterActionStandAgainstTheTide.TryFindValidTarget))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TryFindValidTarget_Patch
    {
        private static int Range(int min, int max)
        {
            return Global.IsMultiplayer && Main.Settings.MultiplayerDeterministicStandAgainstTheTide
                ? DeterministicRandom.Range(min, max)
                : UnityEngine.Random.Range(min, max);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var rangeMethod = typeof(UnityEngine.Random).GetMethod(nameof(UnityEngine.Random.Range),
                [typeof(int), typeof(int)]);
            var myRangeMethod = new Func<int, int, int>(Range).Method;

            return instructions.ReplaceCalls(rangeMethod, "CharacterActionStandAgainstTheTide.TryFindValidTarget",
                new CodeInstruction(OpCodes.Call, myRangeMethod));
        }
    }
}
