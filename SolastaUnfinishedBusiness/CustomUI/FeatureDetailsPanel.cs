using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SolastaUnfinishedBusiness.CustomUI;

// Clicking a feature in the character sheet's feature lists opens its full details in a panel that stays open until
// closed: the description, the choice made, what a feature set includes, a power's uses left, and the level it needs.
internal sealed class FeatureDetailsPanel : MonoBehaviour, IPointerClickHandler
{
    // TEMPORARY diagnostics (1.6.15.5): the in-game test of 1.6.15.1 saw clicks never reach a row, and nothing in the
    // game's code or prefabs explains it (evidence/feature-click/FINDINGS.md). For the first presses over a row, the
    // log says what the event system's raycast hits, and after the release whether the click arrived and how far the
    // pointer moved. Remove once the cause is known.
    private const int MaxDiagnosticPresses = 8;

    private static bool _loggedBind;
    private static int _diagnosticPresses;

    private FeatureDefinition _choice;
    private FeatureDefinition _feature;
    private RulesetCharacter _character;
    private string _levelRequirement;

    private bool _diagnosing;
    private bool _clickReceived;
    private int _releaseFrame = -1;
    private Vector3 _pressPosition;

    public void OnPointerClick(PointerEventData eventData)
    {
        _clickReceived = true;

        if (eventData.button == PointerEventData.InputButton.Left && _feature)
        {
            Main.Info($"Feature details panel opened for {_feature.Name}");
            Show(_feature, _choice, _character, _levelRequirement);
        }
    }

    private void Update()
    {
        if (_diagnosing)
        {
            if (_releaseFrame < 0 && Input.GetMouseButtonUp(0))
            {
                _releaseFrame = Time.frameCount;
            }
            // a frame later, so the event system has handled the release whichever runs first
            else if (_releaseFrame >= 0 && Time.frameCount > _releaseFrame)
            {
                _diagnosing = false;
                Main.Info($"[FeatureClick] release on {_feature?.Name}: click received={_clickReceived}, " +
                          $"moved={Vector3.Distance(_pressPosition, Input.mousePosition):0.#}px");
            }
        }

        if (_diagnosticPresses < MaxDiagnosticPresses && Input.GetMouseButtonDown(0) && IsMouseOver())
        {
            _diagnosticPresses++;
            _diagnosing = true;
            _clickReceived = false;
            _releaseFrame = -1;
            _pressPosition = Input.mousePosition;
            LogPress();
        }
    }

    private void OnDisable()
    {
        _diagnosing = false;
    }

    private void LogPress()
    {
        var eventSystem = EventSystem.current;

        if (!eventSystem)
        {
            Main.Info($"[FeatureClick] press on {_feature?.Name}: no current EventSystem");
            return;
        }

        var pointer = new PointerEventData(eventSystem) { position = Input.mousePosition };
        var hits = new List<RaycastResult>();

        eventSystem.RaycastAll(pointer, hits);

        var hitsRow = hits.Count > 0 && hits[0].gameObject && hits[0].gameObject.transform.IsChildOf(transform);
        var clickTarget = hits.Count > 0 ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject) : null;
        var downTarget = hits.Count > 0 ? ExecuteEvents.GetEventHandler<IPointerDownHandler>(hits[0].gameObject) : null;

        Main.Info($"[FeatureClick] press on {_feature?.Name} at {Input.mousePosition}: " +
                  $"module={eventSystem.currentInputModule?.GetType().Name}, " +
                  $"dragThreshold={eventSystem.pixelDragThreshold}, hits={hits.Count}, top hit is this row={hitsRow}, " +
                  $"click handler={PathOf(clickTarget)}, press handler={PathOf(downTarget)}");

        foreach (var hit in hits.Take(5))
        {
            Main.Info($"[FeatureClick]   hit {PathOf(hit.gameObject)} (raycaster {hit.module?.GetType().Name}, " +
                      $"depth {hit.depth}, sorting {hit.sortingLayer}/{hit.sortingOrder})");
        }
    }

    private static string PathOf(GameObject gameObject)
    {
        if (!gameObject)
        {
            return "none";
        }

        var names = new List<string>();

        for (var t = gameObject.transform; t && names.Count < 6; t = t.parent)
        {
            names.Add(t.name);
        }

        return string.Join("/", Enumerable.Reverse(names));
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
