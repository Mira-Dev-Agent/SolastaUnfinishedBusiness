using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NarrativeStatePlayerRoleAssignmentPatcher
{
    [HarmonyPatch(typeof(NarrativeStatePlayerRoleAssignment), nameof(NarrativeStatePlayerRoleAssignment.BuildHook))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BuildHook_Patch
    {
        //PATCH: ensures dialogs won't break on official campaigns with parties less than 4 (PARTYSIZE)
        private static void TryAdd(
            Dictionary<string, WorldLocationCharacter> playerRolesMap,
            string role,
            WorldLocationCharacter actor)
        {
            playerRolesMap.TryAdd(role, actor);
        }

        //PATCH: supports heroes with best proficiency to roll dialogue choices instead of vanilla behavior
        private static void RemoveAt(List<WorldLocationCharacter> actors, int position)
        {
            if (!Main.Settings.EnableHeroWithBestProficiencyToRollChoice)
            {
                actors.RemoveAt(position);
            }
        }

        [UsedImplicitly]
        public static void Prefix(NarrativeStatePlayerRoleAssignment __instance, out HashSet<string> __state)
        {
            __state = __instance.PlayerRolesMap == null ? [] : [.. __instance.PlayerRolesMap.Keys];
        }

        [UsedImplicitly]
        public static void Postfix(NarrativeStatePlayerRoleAssignment __instance, HashSet<string> __state)
        {
            //PATCH: one hero per role so heroes don't talk to themselves (MULTIPLAYER)
            if (Main.Settings.EnableHeroWithBestProficiencyToRollChoice)
            {
                MultiplayerContext.EnsureUniqueHeroPerRole(__instance, __state);
            }
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var tryAddMethod = new Action<
                Dictionary<string, WorldLocationCharacter>,
                string,
                WorldLocationCharacter>(TryAdd).Method;
            var removeAtMethod = new Action<
                List<WorldLocationCharacter>,
                int>(RemoveAt).Method;

            return instructions
                .ReplaceAdd("System.String, WorldLocationCharacter", -1,
                    "NarrativeStatePlayerRoleAssignment.BuildHook.Add",
                    new CodeInstruction(OpCodes.Call, tryAddMethod))
                .ReplaceRemoveAt(-1,
                    "NarrativeStatePlayerRoleAssignment.BuildHook.RemoveAt",
                    new CodeInstruction(OpCodes.Call, removeAtMethod));
        }
    }
}
