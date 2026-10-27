using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterPoolManagerPatcher
{
    [HarmonyPatch(typeof(CharacterPoolManager), nameof(CharacterPoolManager.EnumeratePool))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumeratePool_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            //PATCH: moves characters the game could not load (e.g. made with removed mod content) to a subfolder
            ErrorDiagnosticsContext.MoveUnloadableCharacters();
        }
    }

    [HarmonyPatch(typeof(CharacterCreationScreen), nameof(CharacterCreationScreen.OnEndHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterCreationScreenOnEndHide_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            //PATCH: ends a remake; a saved remake moves the old file to Unloadable/Remade
            CharacterRemakeContext.CreationClosed();
        }
    }

    [HarmonyPatch(typeof(RuntimeState_Outgame), nameof(RuntimeState_Outgame.Begin))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RuntimeStateOutgameBegin_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            //PATCH: tells the player which characters were moved and why, once the main menu is up
            ErrorDiagnosticsContext.ShowMovedCharactersIfAny();
        }
    }

    [HarmonyPatch(typeof(CharacterPoolManager), nameof(CharacterPoolManager.SaveCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SaveCharacter_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetCharacterHero heroCharacter, bool temporaryCharacter)
        {
            //PATCH: notes that a remade character was saved, so its old file can leave the remake list
            CharacterRemakeContext.CharacterSaved(heroCharacter, temporaryCharacter);

            //PATCH: Keeps last level up hero selected
            if (heroCharacter == null)
            {
                return;
            }

            Global.LastLevelUpHeroName = Main.Settings.KeepCharactersPanelOpenAndHeroSelectedAfterLevelUp
                ? heroCharacter.Name
                : null;
        }
    }
}
