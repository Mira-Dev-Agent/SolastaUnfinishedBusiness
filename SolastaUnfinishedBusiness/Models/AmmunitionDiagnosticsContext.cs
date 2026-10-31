using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SolastaUnfinishedBusiness.Models;

// Temporary issue #3 instrumentation. This branch observes the existing save path;
// it must not refresh attack modes, roll dice, copy pooled forms or change inventory.
internal static class AmmunitionDiagnosticsContext
{
    // Separate budgets keep inventory refresh spam from hiding the actual attack.
    // No actor/item references are retained, and logging stops until the next launch.
    private static int _refreshLines;
    private static int _actionLines;

    internal static void Trace(string stage, RulesetCharacter actor, RulesetAttackMode mode,
        List<EffectForm> actualForms = null, string result = null)
    {
        if (actor is not RulesetCharacterHero hero || mode == null || !mode.Ranged)
        {
            return;
        }

        var refresh = stage.StartsWith("refresh", StringComparison.Ordinal);
        if (refresh ? _refreshLines >= 160 : _actionLines >= 160)
        {
            return;
        }

        if (refresh)
        {
            _refreshLines++;
        }
        else
        {
            _actionLines++;
        }

        try
        {
            // GetCurrentAmmunitionSlot is observational: record what the mode builder
            // currently sees, rather than guessing that the selected ammo was merged.
            var ammoType = hero.GetAmmunitionType(mode);
            var item = string.IsNullOrEmpty(ammoType) ? null :
                hero.CharacterInventory.GetCurrentAmmunitionSlot(ammoType)?.EquipedItem;
            var ammo = item?.ItemDefinition?.AmmunitionDescription?.EffectDescription;
            Main.ModEntry.Logger.Log($"[AmmoDiag] {stage} actor={hero.Guid} mode={RuntimeHelpers.GetHashCode(mode)} " +
                     $"ammo={item?.ItemDefinition?.Name ?? "none"} type={ammoType} " +
                     $"modeEffect=[{Describe(mode.EffectDescription)}] ammoEffect=[{Describe(ammo)}] " +
                     $"actual=[{Forms(actualForms)}] result={result}");
        }
        catch (Exception ex)
        {
            // A diagnostic failure must never interrupt an attack or consume a roll.
            Main.ModEntry.Logger.Log($"[AmmoDiag] {stage} observation failed: {ex.GetType().Name}");
        }
    }

    private static string Describe(EffectDescription effect)
    {
        return effect == null ? "none" :
            $"save={effect.HasSavingThrow},ability={effect.SavingThrowAbility}," +
            $"dcType={effect.DifficultyClassComputation},dc={effect.FixedSavingThrowDifficultyClass}," +
            $"forms={Forms(effect.EffectForms)}";
    }

    private static string Forms(List<EffectForm> forms)
    {
        // Include per-form save affinity: HasSavingThrow alone does not establish
        // whether a successful save will suppress the poison's damage/condition.
        return forms == null ? "none" : string.Join(";", forms.Select(form =>
            form == null ? "null" : $"{form.FormType}:{form.SavingThrowAffinity}"));
    }
}
