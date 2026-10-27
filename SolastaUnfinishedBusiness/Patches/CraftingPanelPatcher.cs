using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CraftingPanelPatcher
{
    private static bool _confirmed;

    //PATCH: warns before crafting starts that the ingredients are used up and that rolls can fail (WarnBeforeCrafting)
    [HarmonyPatch(typeof(CraftingPanel), nameof(CraftingPanel.OnStartCraftingCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnStartCraftingCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CraftingPanel __instance)
        {
            var hero = __instance.GuiCharacter?.RulesetCharacterHero;
            var recipe = __instance.SelectedRecipe?.RecipeDefinition;

            if (_confirmed || !Main.Settings.WarnBeforeCrafting || hero == null || !recipe)
            {
                return true;
            }

            var description = Gui.Format("Message/&CraftingStartWarningDescription",
                recipe.CraftedItem.FormatTitle(), recipe.CraftingDC.ToString(),
                hero.ComputeCraftingDurationHours(recipe).ToString());
            var plainItems = PrimedIngredientContext.DescribeBaseUse(recipe);

            if (plainItems.Count > 0)
            {
                description += "\n\n" + Gui.Format("Message/&CraftingStartWarningPlainItems",
                    string.Join("\n", plainItems));
            }

            Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2, "Message/&CraftingStartWarningTitle",
                description, "Message/&CraftingStartWarningAccept", "Message/&MessageCancelTitle",
                () =>
                {
                    _confirmed = true;

                    try
                    {
                        __instance.OnStartCraftingCb();
                    }
                    finally
                    {
                        _confirmed = false;
                    }
                },
                null);

            return false;
        }
    }
}
