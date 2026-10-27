using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using UnityEngine;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Artificer infusions are tracked by what is active: an infusion stays in its item until it is removed, replaced or
// its knowledge is replaced, and every active infusion takes one of the Artificer's slots.
// - the infusion pool's uses are the free slots: after a long rest, and whenever an infusion ends (by default)
// - tabletop setting: a slot freed by ending an infusion only comes back at the next long rest
// - when every slot is taken, an infusion can still be used: the player chooses which infusion it replaces
// - an Active Infusions power lists every infusion, its item and who holds it, and can end any of them; a Change
//   Infusions action at the end of a long rest does the same, and there a freed slot always comes back at once
// Ending an infusion is a lockstep command, so every computer ends the same one at the same tick.
internal static class InfusionTrackingContext
{
    private const string ChangeInfusionsName = "RestActivityLongRestChangeInfusions";

    // sent through vanilla AssignClassLevelRPC in place of a class name, like the level down
    private const string EndInfusionCommandMarker = "UbEndInfusion:";

    private const RestActivityDefinition.ActivityCondition ActivityConditionDisabled =
        (RestActivityDefinition.ActivityCondition)(-1001);

    private static bool _refundingSlot;

    internal static FeatureDefinitionPower PowerActiveInfusions { get; private set; }

    private static RestActivityDefinition RestActivityChangeInfusions { get; set; }

    private static bool SlotsReturnOnLongRestOnly => Main.Settings.InfusionSlotsReturnOnLongRestOnly;

    internal static void Load()
    {
        PowerActiveInfusions = FeatureDefinitionPowerBuilder
            .Create("PowerInventorActiveInfusions")
            .SetGuiPresentation(Category.Feature, Sprites.ActionInfuse)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .Build())
            .AddToDB();

        // existing Artificers get it too, as feature sets are read whenever powers are granted
        if (DatabaseHelper.TryGetDefinition<FeatureDefinitionFeatureSet>("FeatureSetInventorInfusions",
                out var infusions))
        {
            infusions.FeatureSet.Add(PowerActiveInfusions);
        }

        RestActivityChangeInfusions = RestActivityDefinitionBuilder
            .Create(ChangeInfusionsName)
            .SetGuiPresentation(Category.RestActivity)
            .SetRestData(
                RestDefinitions.RestStage.AfterRest,
                RestType.LongRest,
                RestActivityDefinition.ActivityCondition.None,
                ChangeInfusionsName,
                string.Empty)
            .AddToDB();

        ServiceRepository.GetService<IFunctorService>()
            .RegisterFunctor(ChangeInfusionsName, new FunctorChangeInfusions());
    }

    internal static bool IsRestActivityAvailable(RestActivityDefinition activity, RulesetCharacterHero hero)
    {
        return activity != RestActivityChangeInfusions || GetActiveInfusions(hero).Count > 0;
    }

    #region Slots

    private static bool IsInfusionPower(FeatureDefinitionPower power)
    {
        return power && power.GetAllSubFeaturesOfType<ILimitEffectInstances>().Contains(InventorClass.InfusionLimiter);
    }

    private static bool UsesInfusionSlots(FeatureDefinitionPower power)
    {
        return power is FeatureDefinitionPowerSharedPool { SharedPool: var pool } && pool == InventorClass.InfusionPool;
    }

    // the character's infusions still in effect, oldest first (the order vanilla ends them in)
    internal static List<RulesetEffectPower> GetActiveInfusions(
        RulesetCharacter character, ICollection<ulong> excluded = null)
    {
        if (character == null)
        {
            return [];
        }

        return EffectHelpers.GetAllEffectsBySourceGuid(character.Guid)
            .OfType<RulesetEffectPower>()
            .Where(x => IsInfusionPower(x.PowerDefinition) &&
                        (x.TrackedItemPropertyGuids.Count > 0 || x.TrackedSummonedItemGuids.Count > 0) &&
                        excluded?.Contains(x.Guid) != true)
            .OrderBy(x => x.Guid)
            .ToList();
    }

    private static bool HasInfusionPool(RulesetCharacter character)
    {
        return character?.UsablePowers.Any(x => x.PowerDefinition == InventorClass.InfusionPool) == true;
    }

    private static int FreeSlots(RulesetCharacter character, ICollection<ulong> excluded = null)
    {
        return Mathf.Max(0,
            character.GetMaxUsesForPool(InventorClass.InfusionPool) - GetActiveInfusions(character, excluded).Count);
    }

    private static void SetRemainingSlots(RulesetCharacter character, int remaining)
    {
        var current = character.GetRemainingPowerCharges(InventorClass.InfusionPool);

        if (current != remaining)
        {
            character.UpdateUsageForPower(InventorClass.InfusionPool, current - remaining);
        }
    }

    // a long rest gives back the free slots only: active infusions keep theirs
    internal static void OnRest(RulesetCharacter character, RestType restType, bool simulate)
    {
        if (simulate || restType != RestType.LongRest || !HasInfusionPool(character))
        {
            return;
        }

        SetRemainingSlots(character, FreeSlots(character));
    }

    // after an infusion ends: by default its slot is free again; with the tabletop setting it only comes back at the
    // next long rest, unless it was replaced by a new infusion or changed at the end of a long rest
    internal static void OnPowerTerminated(RulesetCharacter character, RulesetEffectPower power)
    {
        if (power == null || !IsInfusionPower(power.PowerDefinition) || !HasInfusionPool(character))
        {
            return;
        }

        // the ended infusion may still be registered while it terminates
        var free = FreeSlots(character, [power.Guid]);
        var current = character.GetRemainingPowerCharges(InventorClass.InfusionPool);

        SetRemainingSlots(character, !SlotsReturnOnLongRestOnly
            ? free
            : Mathf.Min(_refundingSlot ? current + 1 : current, free));
    }

    // with every slot taken, infusing stays possible: the player chooses which infusion it replaces
    internal static void AllowReplacingWhenFull(
        RulesetCharacter character, RulesetUsablePower usablePower, ref int remainingUses)
    {
        if (remainingUses > 0 || usablePower == null || !UsesInfusionSlots(usablePower.PowerDefinition))
        {
            return;
        }

        var max = character.GetMaxUsesForPool(InventorClass.InfusionPool);

        if (max > 0 && GetActiveInfusions(character).Count >= max)
        {
            remainingUses = 1;
        }
    }

    #endregion

    #region End Infusion Command

    private static void RequestEndInfusion(RulesetCharacter character, RulesetEffectPower infusion, bool refund)
    {
        var command = $"{EndInfusionCommandMarker}{infusion.Guid}:{(refund ? 1 : 0)}";

        if (Global.IsMultiplayer &&
            ServiceRepository.GetService<INetworkingService>() is NetworkingManager { view: not null } networking)
        {
            networking.view.RPC(nameof(NetworkingManager.AssignClassLevelRPC), RpcTarget.AllViaServer,
                networking.CommandTick, (long)character.Guid, command, false);
            PhotonNetwork.SendAllOutgoingCommands();

            return;
        }

        EndInfusion(character, infusion.Guid, refund);
    }

    internal static bool TryHandleEndInfusionCommand(
        NetworkingManager networking, long characterGuid, string classDefinitionName, ref IEnumerator result)
    {
        if (classDefinitionName == null || !classDefinitionName.StartsWith(EndInfusionCommandMarker))
        {
            return false;
        }

        var parts = classDefinitionName.Substring(EndInfusionCommandMarker.Length).Split(':');

        if (parts.Length != 2 || !ulong.TryParse(parts[0], out var effectGuid))
        {
            return false;
        }

        result = EndInfusionAsync(networking, characterGuid, effectGuid, parts[1] == "1");

        return true;
    }

    private static IEnumerator EndInfusionAsync(
        NetworkingManager networking, long characterGuid, ulong effectGuid, bool refund)
    {
        var character = networking.GetEntityFromGuid<RulesetCharacter>(characterGuid, false);

        if (character != null)
        {
            EndInfusion(character, effectGuid, refund);
        }

        yield break;
    }

    private static void EndInfusion(RulesetCharacter character, ulong effectGuid, bool refund)
    {
        var infusion = GetActiveInfusions(character).FirstOrDefault(x => x.Guid == effectGuid);

        if (infusion == null)
        {
            return;
        }

        _refundingSlot = refund;

        try
        {
            character.TerminatePower(infusion);
        }
        finally
        {
            _refundingSlot = false;
        }
    }

    #endregion

    #region Lists

    private static string DescribeInfusion(RulesetEffectPower infusion)
    {
        var item = InfusedItem(infusion);
        var itemName = item != null ? Gui.Localize(item.ItemDefinition.GuiPresentation.Title) : null;

        return Gui.Format("Message/&InfusionEntry",
            Gui.Localize(infusion.PowerDefinition.GuiPresentation.Title),
            itemName ?? Gui.Localize("Message/&InfusionItemGone"),
            item == null ? string.Empty : DescribeLocation(item));
    }

    private static RulesetItem InfusedItem(RulesetEffectPower infusion)
    {
        foreach (var guid in infusion.TrackedSummonedItemGuids)
        {
            if (RulesetEntity.TryGetEntity<RulesetItem>(guid, out var summoned) && summoned != null)
            {
                return summoned;
            }
        }

        foreach (var guid in infusion.TrackedItemPropertyGuids)
        {
            if (RulesetEntity.TryGetEntity<RulesetItemProperty>(guid, out var property) && property != null &&
                RulesetEntity.TryGetEntity<RulesetItem>(property.targetItemGuid, out var item) && item != null)
            {
                return item;
            }
        }

        return null;
    }

    // which party member holds the item and how; items left behind only say so, as the game doesn't keep their place
    private static string DescribeLocation(RulesetItem item)
    {
        var party = (ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters ?? [])
            .Select(x => x.RulesetCharacter)
            .Concat(Gui.GameCampaign?.Party?.CharactersList?.Select(x => (RulesetCharacter)x.RulesetCharacter) ?? [])
            .Where(x => x != null)
            .Distinct();

        foreach (var character in party)
        {
            var slot = character.CharacterInventory?.FindSlotHoldingItem(item);

            if (slot == null)
            {
                continue;
            }

            var where = slot.Name.Contains("MainHand")
                ? "Message/&InfusionLocationMainHand"
                : slot.Name.Contains("OffHand")
                    ? "Message/&InfusionLocationOffHand"
                    : slot.SlotTypeDefinition && slot.SlotTypeDefinition.BodySlot
                        ? "Message/&InfusionLocationWorn"
                        : "Message/&InfusionLocationBackpack";

            return Gui.Format("Message/&InfusionLocation", character.Name, Gui.Localize(where));
        }

        return Gui.Localize("Message/&InfusionLocationNotWithParty");
    }

    // the infusions are offered one at a time: act on this one, or go to the next
    private static void ShowInfusions(
        RulesetCharacter character,
        string title,
        string acceptTitle,
        System.Action<RulesetEffectPower, System.Action> onAccept,
        HashSet<ulong> ended = null,
        int index = 0)
    {
        ended ??= [];

        var infusions = GetActiveInfusions(character, ended);

        if (index >= infusions.Count)
        {
            if (infusions.Count == 0)
            {
                Gui.GuiService.ShowMessage(MessageModal.Severity.Informative1, title,
                    Gui.Localize("Message/&InfusionNoneActive"), "Message/&MessageOkTitle", string.Empty,
                    null, null);
            }

            return;
        }

        var infusion = infusions[index];
        var max = character.GetMaxUsesForPool(InventorClass.InfusionPool);

        Gui.GuiService.ShowMessage(MessageModal.Severity.Informative1, title,
            Gui.Format("Message/&InfusionListEntry",
                (index + 1).ToString(), infusions.Count.ToString(), infusions.Count.ToString(), max.ToString(),
                DescribeInfusion(infusion)),
            acceptTitle, "Message/&InfusionNext",
            () => onAccept(infusion, () =>
            {
                ended.Add(infusion.Guid);
                ShowInfusions(character, title, acceptTitle, onAccept, ended, index);
            }),
            () => ShowInfusions(character, title, acceptTitle, onAccept, ended, index + 1));
    }

    // Active Infusions power: look at every infusion and end any of them
    internal static bool PowerBoxActivated(UsablePowerBox box)
    {
        if (box.usablePower?.PowerDefinition != PowerActiveInfusions)
        {
            return true;
        }

        var character = box.activator;

        if (character != null)
        {
            ShowInfusions(character, "Feature/&PowerInventorActiveInfusionsTitle", "Message/&InfusionEnd",
                (infusion, next) => ConfirmEnd(character, infusion, false, next));
        }

        return false;
    }

    // with the tabletop setting, ending an infusion outside a long rest asks first, as its slot won't come back yet
    private static void ConfirmEnd(
        RulesetCharacter character, RulesetEffectPower infusion, bool atLongRest, System.Action next)
    {
        if (atLongRest || !SlotsReturnOnLongRestOnly)
        {
            RequestEndInfusion(character, infusion, atLongRest);
            next();

            return;
        }

        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2, "Message/&InfusionEndTitle",
            Gui.Format("Message/&InfusionEndUntilLongRest", DescribeInfusion(infusion)),
            "Message/&InfusionEnd", "Message/&MessageCancelTitle",
            () =>
            {
                RequestEndInfusion(character, infusion, false);
                next();
            },
            null);
    }

    // Change Infusions at the end of a long rest: end any number of infusions, each slot comes back at once
    private sealed class FunctorChangeInfusions : Functor
    {
        public override IEnumerator Execute(
            FunctorParametersDescription functorParameters, FunctorExecutionContext context)
        {
            var character = functorParameters.RestingHero;

            if (character != null)
            {
                ShowInfusions(character, "RestActivity/&RestActivityLongRestChangeInfusionsTitle",
                    "Message/&InfusionEnd", (infusion, next) => ConfirmEnd(character, infusion, true, next));
            }

            yield break;
        }
    }

    // Choosing an item to infuse while every slot is taken asks which infusion it replaces; nothing is infused
    // when none is chosen
    internal static InventoryPanel.ItemSelectedHandler WrapItemSelected(InventoryPanel.ItemSelectedHandler selected)
    {
        if (selected == null)
        {
            return null;
        }

        return (character, item, slot, actionParams) =>
        {
            var ruleset = character?.RulesetCharacter;
            var power = (actionParams?.RulesetEffect as RulesetEffectPower)?.PowerDefinition;

            if (ruleset == null || !UsesInfusionSlots(power) || FreeSlots(ruleset) > 0 ||
                GetActiveInfusions(ruleset).Count == 0)
            {
                selected(character, item, slot, actionParams);

                return;
            }

            ShowInfusions(ruleset, "Message/&InfusionReplaceTitle", "Message/&InfusionReplace",
                (infusion, _) =>
                {
                    RequestEndInfusion(ruleset, infusion, true);
                    selected(character, item, slot, actionParams);
                });
        };
    }

    #endregion
}
