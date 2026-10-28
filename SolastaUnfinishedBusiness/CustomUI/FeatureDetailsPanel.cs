using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;

namespace SolastaUnfinishedBusiness.CustomUI;

// Clicking a feature in the character sheet's feature lists opens its full details in a panel that stays open until
// closed: the description, the choice made, what a feature set includes, a power's uses left, and the level it needs.
//
// The rows don't receive the event system's pointer clicks (the game's tooltips find what's under the mouse their own
// way), so a row watches for a left press and release inside its own rectangle instead.
internal sealed class FeatureDetailsPanel : MonoBehaviour
{
    private static bool _loggedBind;
    private static bool _loggedClick;

    private FeatureDefinition _choice;
    private FeatureDefinition _feature;
    private RulesetCharacter _character;
    private string _levelRequirement;
    private bool _pressed;

    private void OnDisable()
    {
        _pressed = false;
    }

    private void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            _pressed = !IsMessageOpen() && IsMouseOver();
        }
        else if (Input.GetMouseButtonUp(0))
        {
            var clicked = _pressed && !IsMessageOpen() && IsMouseOver();

            _pressed = false;

            if (clicked && _feature)
            {
                if (!_loggedClick)
                {
                    _loggedClick = true;
                    Main.Info($"Feature details panel opened for {_feature.Name}");
                }

                Show(_feature, _choice, _character, _levelRequirement);
            }
        }
    }

    // while a message is open, a click on its button over a row must not open another one
    private static bool IsMessageOpen()
    {
        var messageModal = Gui.GuiService.GetScreen<MessageModal>();

        return messageModal && messageModal.Visible;
    }

    private bool IsMouseOver()
    {
        var canvas = GetComponentInParent<Canvas>()?.rootCanvas;
        var camera = !canvas || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;

        return transform is RectTransform rectTransform &&
               RectTransformUtility.RectangleContainsScreenPoint(rectTransform, Input.mousePosition, camera);
    }

    internal static void Bind(
        GameObject row,
        FeatureDefinition feature,
        FeatureDefinition choice,
        RulesetCharacter character,
        string levelRequirement)
    {
        var panel = row.GetComponent<FeatureDetailsPanel>() ?? row.AddComponent<FeatureDetailsPanel>();

        panel._feature = feature;
        panel._choice = choice;
        panel._character = character;
        panel._levelRequirement = levelRequirement;

        if (!_loggedBind)
        {
            _loggedBind = true;
            Main.Info($"Feature details panel bound to the feature list (first: {feature.Name})");
        }
    }

    private static void Show(
        FeatureDefinition feature, FeatureDefinition choice, RulesetCharacter character, string levelRequirement)
    {
        var lines = new List<string>();
        var title = choice ? $"{choice.FormatTitle()} ({feature.FormatTitle()})" : feature.FormatTitle();

        // a choice whose own text is empty is described by what was chosen
        var description = feature is FeatureDefinitionPower
            ? ServiceRepository.GetService<IGuiWrapperService>().GetGuiPowerDefinition(feature.Name).Description
            : feature.GuiPresentation.Description == Gui.NoLocalization && choice
                ? choice.FormatDescription()
                : feature.FormatDescription();

        AddText(lines, description);

        if (choice && feature.GuiPresentation.Description != Gui.NoLocalization)
        {
            AddSection(lines, Gui.Format("Message/&FeatureDetailsChosen", choice.FormatTitle()),
                choice.FormatDescription());
        }

        if (!choice && feature is FeatureDefinitionFeatureSet featureSet)
        {
            var members = featureSet.FeatureSet
                .Where(x => x && !x.GuiPresentation.Hidden && x.GuiPresentation.Title != Gui.NoLocalization)
                .ToList();

            if (members.Count > 0)
            {
                lines.Add(Gui.Localize("Message/&FeatureDetailsIncludes").Bold());
                lines.AddRange(members.Select(x => DescribeMember(x)));
            }
        }

        AddPowerUses(lines, feature as FeatureDefinitionPower, character);

        if (!string.IsNullOrEmpty(levelRequirement))
        {
            lines.Add(Gui.Colorize(levelRequirement, Gui.ColorNegative));
        }

        Gui.GuiService.ShowMessage(MessageModal.Severity.Informative1, title, string.Join("\n\n", lines),
            "Message/&MessageOkTitle", string.Empty, null, null);
    }

    private static string DescribeMember(FeatureDefinition member)
    {
        var text = member.FormatDescription();

        return string.IsNullOrEmpty(text) || text == Gui.NoLocalization
            ? $"• {member.FormatTitle()}"
            : $"• {member.FormatTitle().Bold()}: {text}";
    }

    private static void AddText(List<string> lines, string text)
    {
        if (!string.IsNullOrEmpty(text) && text != Gui.NoLocalization)
        {
            lines.Add(text);
        }
    }

    private static void AddSection(List<string> lines, string header, string text)
    {
        if (!string.IsNullOrEmpty(text) && text != Gui.NoLocalization)
        {
            lines.Add($"{header.Bold()}\n{text}");
        }
    }

    // only for powers with limited uses that this character has
    private static void AddPowerUses(List<string> lines, FeatureDefinitionPower power, RulesetCharacter character)
    {
        var usablePower = character?.UsablePowers.FirstOrDefault(x => x.PowerDefinition == power);

        if (!power || usablePower == null)
        {
            return;
        }

        var max = character.GetMaxUsesOfPower(usablePower);

        if (max > 0)
        {
            lines.Add(Gui.Format("Message/&FeatureDetailsUses",
                character.GetRemainingUsesOfPower(usablePower).ToString(), max.ToString()));
        }
    }
}
