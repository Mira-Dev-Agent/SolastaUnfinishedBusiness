using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GamePatcher
{
    //PATCH: crafting counts unequipped plain weapons and armor toward a Primed ingredient (CraftWithBaseInsteadOfPrimed)
    [HarmonyPatch(typeof(Game), nameof(Game.CountItemsOfTypeInParty))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CountItemsOfTypeInParty_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ItemDefinition itemDefintiion, ref int __result)
        {
            __result += PrimedIngredientContext.CountBaseFor(itemDefintiion);
        }
    }
}
