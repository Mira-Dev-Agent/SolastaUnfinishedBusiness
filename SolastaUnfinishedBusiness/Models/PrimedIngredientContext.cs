using System.Collections.Generic;
using System.Linq;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ItemFlagDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Crafting with base gear: a recipe that asks for a Primed weapon or armor also takes the plain one. Primed items are
// used first, as the game does; plain ones only count while they are not equipped, so a hero's own gear is never used
// up. This works for every recipe (the game's, the mod's and campaigns'), as it changes the ingredient count and use,
// not the recipes.
internal static class PrimedIngredientContext
{
    private static Dictionary<ItemDefinition, ItemDefinition> _primedToBase;

    private static Dictionary<ItemDefinition, ItemDefinition> PrimedToBase => _primedToBase ??= BuildPrimedToBase();

    // every Primed item and the plain item it is made from, found by name (Primed_Longsword, CEHalberdPrimed)
    private static Dictionary<ItemDefinition, ItemDefinition> BuildPrimedToBase()
    {
        var database = DatabaseRepository.GetDatabase<ItemDefinition>();
        var result = new Dictionary<ItemDefinition, ItemDefinition>();

        foreach (var primed in database.Where(x =>
                     (x.IsWeapon || x.IsArmor) &&
                     (x.ItemPresentation.ItemFlags.Contains(ItemFlagPrimed) || x.Name.Contains("Primed"))))
        {
            var candidates = new List<string>();

            if (primed.Name == "Primed_Leather_Armor")
            {
                candidates.Add("Leather");
            }

            if (primed.Name.StartsWith("Primed_"))
            {
                candidates.Add(primed.Name.Substring("Primed_".Length));
            }

            if (primed.Name.EndsWith("Primed"))
            {
                candidates.Add(primed.Name.Substring(0, primed.Name.Length - "Primed".Length));
            }

            foreach (var name in candidates)
            {
                if (!database.TryGetElement(name, out var baseItem) || baseItem == primed || baseItem.Magical)
                {
                    continue;
                }

                result[primed] = baseItem;

                break;
            }
        }

        return result;
    }

    private static bool TryGetBase(ItemDefinition primed, out ItemDefinition baseItem)
    {
        baseItem = null;

        return Main.Settings.CraftWithBaseInsteadOfPrimed &&
               primed &&
               PrimedToBase.TryGetValue(primed, out baseItem);
    }

    private static IEnumerable<RulesetCharacterHero> LivingHeroes()
    {
        var heroes = Gui.GameLocation
            ? ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyRulesetCharacterHeroes
            : Gui.GameCampaign?.Party?.CharactersList
                .Select(x => x.RulesetCharacter as RulesetCharacterHero)
                .ToList();

        return (heroes ?? []).Where(x => x is { IsDead: false });
    }

    // plain items the hero carries without wearing or holding them
    private static List<RulesetItem> UnequippedItems(RulesetCharacterHero hero, ItemDefinition itemDefinition)
    {
        var items = new List<RulesetItem>();

        hero.CharacterInventory.EnumerateAllItems(items);

        return items
            .Where(x => x.ItemDefinition == itemDefinition && !IsEquipped(hero, x))
            .ToList();
    }

    private static bool IsEquipped(RulesetCharacter hero, RulesetItem item)
    {
        var slot = hero.CharacterInventory.FindSlotHoldingItem(item);

        return slot != null &&
               (slot.Name.Contains("MainHand") ||
                slot.Name.Contains("OffHand") ||
                (slot.SlotTypeDefinition && slot.SlotTypeDefinition.BodySlot));
    }

    private static int CountOf(RulesetItem item)
    {
        return item.ItemDefinition.CanBeStacked ? item.StackCount : 1;
    }

    // how many plain items can stand in for this Primed one
    internal static int CountBaseFor(ItemDefinition primed)
    {
        return TryGetBase(primed, out var baseItem)
            ? LivingHeroes().Sum(hero => UnequippedItems(hero, baseItem).Sum(CountOf))
            : 0;
    }

    // the plain items a recipe will use up in place of missing Primed ones, as "2 × Longsword" lines
    internal static List<string> DescribeBaseUse(RecipeDefinition recipe)
    {
        var lines = new List<string>();

        foreach (var ingredient in recipe.Ingredients)
        {
            if (!TryGetBase(ingredient.ItemDefinition, out var baseItem))
            {
                continue;
            }

            var plain = CountBaseFor(ingredient.ItemDefinition);
            var primed = Gui.Game.CountItemsOfTypeInParty(ingredient.ItemDefinition) - plain;
            var used = System.Math.Min(plain, ingredient.Amount - primed);

            if (used > 0)
            {
                lines.Add($"{used} × {baseItem.FormatTitle()}");
            }
        }

        return lines;
    }

    // after the game used up the Primed items it found, uses plain ones for what is left
    internal static void ConsumeBaseFor(Dictionary<ItemDefinition, int> remaining)
    {
        foreach (var entry in remaining.ToList())
        {
            if (entry.Value <= 0 || !TryGetBase(entry.Key, out var baseItem))
            {
                continue;
            }

            var left = entry.Value;

            foreach (var hero in LivingHeroes())
            {
                foreach (var item in UnequippedItems(hero, baseItem))
                {
                    if (left <= 0)
                    {
                        break;
                    }

                    if (item.ItemDefinition.CanBeStacked && item.StackCount > left)
                    {
                        item.SpendStack(left);
                        left = 0;

                        break;
                    }

                    left -= CountOf(item);
                    hero.CharacterInventory.DestroyItem(item);
                }
            }

            if (left > 0)
            {
                remaining[entry.Key] = left;
            }
            else
            {
                remaining.Remove(entry.Key);
            }
        }
    }
}
