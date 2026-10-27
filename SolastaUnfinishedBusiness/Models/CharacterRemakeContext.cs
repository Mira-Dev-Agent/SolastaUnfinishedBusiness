using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using static Gui.LocalizationSpeakerGender;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

// Remakes characters the game can no longer load (usually heroes made with removed mod classes or races). The old
// file is read directly, so the parts that still exist (identity, look, voice, portrait, race and background) can
// pre-fill a new character creation. Class, ability scores and every later choice are made again from scratch.
internal static class CharacterRemakeContext
{
    private const string RemadeFolderName = "Remade";
    private const string RemakeButtonName = "UbRemakeOldCharacterButton";

    private static readonly float StopRoutine = -1;
    private static readonly float RetryRoutine = 0.1f;

    // races the old mod defined itself before the official ones existed
    private static readonly Dictionary<string, string> RaceReplacements = new() { { "GnomeRace", "Gnome" } };

    internal static bool IsRemaking { get; private set; }
    private static RemakeData Current { get; set; }
    private static bool CurrentSaved { get; set; }

    private static string UnloadableFolder =>
        Path.Combine(TacticalAdventuresApplication.GameCharactersDirectory, ErrorDiagnosticsContext.UnloadableFolderName);

    private static string PersonalPortraitsFolder => $"{PortraitsContext.PortraitsFolder}/Personal";

    #region Characters Panel

    internal static void RefreshRemakeButton(CharactersPanel panel)
    {
        var template = panel.newCharacterButton;

        if (!template)
        {
            return;
        }

        var parent = template.transform.parent;
        var existing = parent.Find(RemakeButtonName);
        var button = existing ? existing.GetComponent<Button>() : CreateRemakeButton(panel, template);

        button.gameObject.SetActive(GetKeptCharacterFiles().Count > 0);
    }

    private static Button CreateRemakeButton(CharactersPanel panel, Button template)
    {
        var copy = Object.Instantiate(template.gameObject, template.transform.parent);

        copy.name = RemakeButtonName;

        var button = copy.GetComponent<Button>();

        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => OfferRemake(panel, 0));

        var label = copy.GetComponentInChildren<GuiLabel>();

        if (label)
        {
            label.Text = "Screen/&RemakeOldCharacterTitle";
        }

        var tooltip = copy.GetComponentInChildren<GuiTooltip>();

        if (tooltip)
        {
            tooltip.Content = "Screen/&RemakeOldCharacterDescription";
        }

        return button;
    }

    private static List<string> GetKeptCharacterFiles()
    {
        return Directory.Exists(UnloadableFolder)
            ? Directory.GetFiles(UnloadableFolder, "*.chr").OrderBy(x => x).ToList()
            : [];
    }

    // one question per kept character: remake it, or move on to the next one
    private static void OfferRemake(CharactersPanel panel, int index)
    {
        var files = GetKeptCharacterFiles();

        if (index >= files.Count)
        {
            return;
        }

        if (!RemakeData.TryRead(files[index], out var data))
        {
            Main.Error($"could not read {files[index]} for a remake");
            OfferRemake(panel, index + 1);

            return;
        }

        var isLast = index == files.Count - 1;

        Gui.GuiService.ShowMessage(MessageModal.Severity.Informative1,
            "Message/&RemakeOldCharacterTitle",
            Gui.Format("Message/&RemakeOldCharacterDescription", data.DisplayName, data.Describe()),
            "Message/&RemakeOldCharacterAccept",
            isLast ? "Screen/&CloseTitle" : "Message/&RemakeOldCharacterNext",
            () => StartRemake(panel, data),
            () => OfferRemake(panel, index + 1));
    }

    #endregion

    #region Remake Flow

    private static void StartRemake(CharactersPanel panel, RemakeData data)
    {
        CopyPortrait(data);

        Current = data;
        CurrentSaved = false;
        IsRemaking = true;

        // same path as the New Character button, then pre-fill each stage when it becomes active
        panel.OnNewCharacterCb();

        var screen = Gui.GuiService.GetScreen<CharacterCreationScreen>();
        var hero = screen.currentHero;

        if (hero == null)
        {
            IsRemaking = false;

            return;
        }

        hero.Name = data.Name;
        hero.SurName = data.SurName;

        if (data.Sex.HasValue)
        {
            hero.Sex = data.Sex.Value;
        }

        if (screen.StagePanelsByName.TryGetValue("RaceSelection", out var race) &&
            race is CharacterStageRaceSelectionPanel racePanel)
        {
            RaceStageEntered(screen, racePanel);
        }

        if (screen.StagePanelsByName.TryGetValue("BackgroundSelection", out var background) &&
            background is CharacterStageBackgroundSelectionPanel backgroundPanel)
        {
            backgroundPanel.selectedBackground = 1; // reset to detect panel activation
            BackgroundStageEntered(screen, backgroundPanel);
        }

        if (screen.StagePanelsByName.TryGetValue("IdentityDefinition", out var identity) &&
            identity is CharacterStageIdentityDefinitionPanel identityPanel)
        {
            identityPanel.refreshingNames = true; // set to detect panel activation
            IdentityStageEntered(screen, identityPanel);
        }
    }

    // the custom portraits feature shows Portraits/Personal/<hero name>.png for heroes with that name
    private static void CopyPortrait(RemakeData data)
    {
        if (data.PortraitPng == null || string.IsNullOrEmpty(data.Name))
        {
            return;
        }

        try
        {
            Main.EnsureFolderExists(PersonalPortraitsFolder);

            var file = $"{PersonalPortraitsFolder}/{data.Name}.png";

            if (!File.Exists(file))
            {
                File.WriteAllBytes(file, data.PortraitPng);
            }
        }
        catch (Exception ex)
        {
            Main.Error($"could not save the portrait of {data.Name}: {ex.Message}");
        }
    }

    internal static void CharacterSaved(RulesetCharacterHero hero, bool temporaryCharacter)
    {
        if (IsRemaking && !temporaryCharacter && hero != null)
        {
            CurrentSaved = true;
        }
    }

    // creation closed: if the new hero was saved, the old file is done and moves out of the remake list
    internal static void CreationClosed()
    {
        if (!IsRemaking)
        {
            return;
        }

        IsRemaking = false;

        if (!CurrentSaved || Current == null || !File.Exists(Current.SourcePath))
        {
            return;
        }

        try
        {
            var remadeFolder = Path.Combine(UnloadableFolder, RemadeFolderName);

            Directory.CreateDirectory(remadeFolder);

            var destination = Path.Combine(remadeFolder, Path.GetFileName(Current.SourcePath));

            if (File.Exists(destination))
            {
                destination = Path.Combine(remadeFolder,
                    $"{Path.GetFileNameWithoutExtension(Current.SourcePath)}-{DateTime.Now:yyyyMMddHHmmss}.chr");
            }

            File.Move(Current.SourcePath, destination);
        }
        catch (Exception ex)
        {
            Main.Error($"could not move remade character {Current.SourcePath}: {ex.Message}");
        }

        Current = null;
    }

    #endregion

    #region Stages

    private static void RaceStageEntered(MonoBehaviour parent, CharacterStageRaceSelectionPanel panel)
    {
        parent.StartCoroutine(WaitAndDo(0.1f, () =>
        {
            if (!IsRemaking || !TryGetDefinition<CharacterRaceDefinition>(Current.Race, out var race))
            {
                return;
            }

            var raceIndex = panel.eligibleRaces.IndexOf(race);

            if (raceIndex < 0)
            {
                return;
            }

            panel.selectedRace = raceIndex;

            if (TryGetDefinition<CharacterRaceDefinition>(Current.SubRace, out var subRace) &&
                panel.sortedSubRaces.TryGetValue(race, out var subRaces))
            {
                var subRaceIndex = subRaces.IndexOf(subRace);

                if (subRaceIndex >= 0)
                {
                    panel.selectedSubRace[raceIndex] = subRaceIndex;
                }
            }

            panel.Refresh();
            panel.CommonData.CharacterPlate.RefreshNameAndRace();
        }));
    }

    private static void BackgroundStageEntered(MonoBehaviour parent, CharacterStageBackgroundSelectionPanel panel)
    {
        parent.StartIntervalCoroutine(() =>
        {
            if (!IsRemaking)
            {
                return StopRoutine;
            }

            if (panel.selectedBackground == 1 || panel.backgroundsTable.childCount <= 0)
            {
                return RetryRoutine;
            }

            if (TryGetDefinition<CharacterBackgroundDefinition>(Current.Background, out var background))
            {
                var index = panel.compatibleBackgrounds.IndexOf(background);

                if (index >= 0)
                {
                    panel.selectedBackground = index;
                    panel.Refresh();
                }
            }

            return StopRoutine;
        });
    }

    private static void IdentityStageEntered(MonoBehaviour parent, CharacterStageIdentityDefinitionPanel panel)
    {
        parent.StartIntervalCoroutine(() =>
        {
            if (!IsRemaking)
            {
                return StopRoutine;
            }

            if (panel.refreshingNames)
            {
                return RetryRoutine;
            }

            panel.firstNameInputField.text = panel.currentHero.Name = Current.Name;
            panel.lastNameInputField.text = panel.currentHero.SurName = Current.SurName;
            panel.backstoryInputField.text = Current.Backstory;

            ApplyMorphotypes(panel);

            var voiceIndex = panel.compatibleVoices.FindIndex(x => x.Name == Current.VoiceId);

            if (voiceIndex >= 0)
            {
                panel.voiceModifier.OnLabelSelected(voiceIndex);
            }

            var pronounIndex = Current.Pronoun.HasValue
                ? new List<Gui.LocalizationSpeakerGender> { Female, Male, NonBinary }.IndexOf(Current.Pronoun.Value)
                : -1;

            if (pronounIndex >= 0)
            {
                panel.pronounModifier.OnLabelSelected(pronounIndex);
            }

            panel.CommonData.CharacterPlate.CommonBind(new GuiCharacter(panel.currentHero));
            panel.Refresh();

            return StopRoutine;
        });
    }

    // drives the same UI controls a player would use, so the game applies its own appearance rules
    private static void ApplyMorphotypes(CharacterStageIdentityDefinitionPanel panel)
    {
        var options = panel.customizationOptionsBySelectedOrigin[
            panel.selectedOptionsByPart[MorphotypeElementDefinition.ElementCategory.Origin]];

        for (var i = 0; i < panel.modifiers.Count && i < MorphotypeElementDefinition.CommonCategories.Length; i++)
        {
            var modifier = panel.modifiers[i];

            if (!modifier.gameObject.activeSelf)
            {
                continue;
            }

            var category = MorphotypeElementDefinition.CommonCategories[i];

            if (modifier.sliderGroup.gameObject.activeSelf &&
                Current.MorphotypeAdditionalValues.TryGetValue(category, out var sliderValue))
            {
                modifier.slider.value = sliderValue;
                modifier.OnSliderCb();
                modifier.OnSliderDragEndCb();

                continue;
            }

            if (!Current.Morphotypes.TryGetValue(category, out var value) ||
                !options.TryGetValue(category, out var values))
            {
                continue;
            }

            var index = values.IndexOf(value);

            if (index < 0)
            {
                continue;
            }

            if (modifier.colorsGroup.gameObject.activeSelf)
            {
                modifier.OnColorSelected(index);
            }
            else if (index < modifier.valuesList.Count)
            {
                if (modifier.selectorGroup.gameObject.activeSelf || modifier.gamepadSelector.gameObject.activeSelf)
                {
                    var guard = modifier.valuesList.Count;

                    while (modifier.currentValue != index && guard-- > 0)
                    {
                        modifier.OnSelectNext();
                    }
                }
                else if (modifier.imagesGroup.gameObject.activeSelf)
                {
                    modifier.OnImageSelected(index);
                }
            }
        }
    }

    private static bool TryGetDefinition<T>(string name, out T definition) where T : BaseDefinition
    {
        definition = null;

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (RaceReplacements.TryGetValue(name, out var replacement))
        {
            name = replacement;
        }

        return DatabaseRepository.GetDatabase<T>().TryGetElement(name, out definition);
    }

    private static void StartIntervalCoroutine(this MonoBehaviour parent, Func<float> action)
    {
        var next = action();

        if (next >= 0)
        {
            parent.StartCoroutine(WaitAndDo(next, () => StartIntervalCoroutine(parent, action)));
        }
    }

    private static IEnumerator WaitAndDo(float seconds, Action action)
    {
        yield return new WaitForSeconds(seconds);

        action();
    }

    #endregion

    #region Old File Reader

    // Reads what still exists in a .chr file without the game's loader, which stops at the first missing definition.
    // Layout (BinarySerializer, persistent context): element = name, total, attributesLength, attributes, then child
    // elements or a raw value. total, attributesLength and each attribute's size count their own 4 bytes. Dictionary
    // entries are a key string followed by an element.
    internal sealed class RemakeData
    {
        internal string SourcePath { get; private set; }
        internal string Name { get; private set; } = string.Empty;
        internal string SurName { get; private set; } = string.Empty;
        internal string Backstory { get; private set; } = string.Empty;
        internal string VoiceId { get; private set; }
        internal RuleDefinitions.CreatureSex? Sex { get; private set; }
        internal Gui.LocalizationSpeakerGender? Pronoun { get; private set; }
        internal string Race { get; private set; }
        internal string SubRace { get; private set; }
        internal string Background { get; private set; }
        internal string OldClasses { get; private set; } = string.Empty;
        internal byte[] PortraitPng { get; private set; }

        internal Dictionary<MorphotypeElementDefinition.ElementCategory, string> Morphotypes { get; } = [];

        internal Dictionary<MorphotypeElementDefinition.ElementCategory, float> MorphotypeAdditionalValues { get; } =
            [];

        internal string DisplayName => string.IsNullOrEmpty(SurName) ? Name : $"{Name} {SurName}";

        internal string Describe()
        {
            var race = string.IsNullOrEmpty(Race) ? "-" : Race;
            var background = string.IsNullOrEmpty(Background) ? "-" : Background;

            return Gui.Format("Message/&RemakeOldCharacterDetails", race, background, OldClasses);
        }

        internal static bool TryRead(string path, out RemakeData data)
        {
            data = null;

            try
            {
                var bytes = File.ReadAllBytes(path);
                var index = new Dictionary<string, List<Element>>();

                Index(bytes, 0, bytes.Length, string.Empty, index);

                if (!index.TryGetValue("/Snapshot", out var snapshots) ||
                    !index.TryGetValue("/HeroCharacter", out var heroes))
                {
                    return false;
                }

                var snapshot = snapshots[0];
                var hero = heroes[0];

                data = new RemakeData
                {
                    SourcePath = path,
                    Name = snapshot.GetString(bytes, "Name") ?? string.Empty,
                    SurName = snapshot.GetString(bytes, "SurName") ?? string.Empty,
                    Backstory = hero.GetString(bytes, "AdditionalBackstory") ?? string.Empty,
                    VoiceId = hero.GetString(bytes, "VoiceID"),
                    Race = GetValue(bytes, index, "/HeroCharacter/RaceDefinition"),
                    SubRace = GetValue(bytes, index, "/HeroCharacter/SubRaceDefinition"),
                    Background = GetValue(bytes, index, "/HeroCharacter/BackgroundDefinition"),
                    PortraitPng = ExtractPng(snapshot.GetRaw(bytes, "PortraitPhoto"))
                };

                if (Enum.TryParse(hero.GetString(bytes, "Sex"), out RuleDefinitions.CreatureSex sex))
                {
                    data.Sex = sex;
                }

                if (Enum.TryParse(hero.GetString(bytes, "Pronoun"), out Gui.LocalizationSpeakerGender pronoun))
                {
                    data.Pronoun = pronoun;
                }

                var classes = GetValues(bytes, index, "/Snapshot/Classes/ClassesItem");
                var levels = (index.TryGetValue("/Snapshot/Levels/LevelsItem", out var levelItems)
                        ? levelItems
                        : [])
                    .Select(x => x.BodyLength == 4 ? BitConverter.ToInt32(bytes, x.BodyStart) : 0)
                    .ToList();

                data.OldClasses = string.Join(", ",
                    classes.Select((x, i) => i < levels.Count ? $"{x} {levels[i]}" : x));

                if (index.TryGetValue("/HeroCharacter/MorphotypeElements", out var morphotypes))
                {
                    var pos = morphotypes[0].BodyStart;
                    var end = pos + morphotypes[0].BodyLength;

                    while (pos < end)
                    {
                        var key = ReadString(bytes, ref pos);
                        var value = ReadString(bytes, ref pos);

                        if (Enum.TryParse(key, out MorphotypeElementDefinition.ElementCategory category))
                        {
                            data.Morphotypes[category] = value;
                        }
                    }
                }

                if (index.TryGetValue("/HeroCharacter/MorphotypeElementAdditionalValues", out var additional))
                {
                    var pos = additional[0].BodyStart;
                    var end = pos + additional[0].BodyLength;

                    while (pos + 4 < end)
                    {
                        var key = ReadString(bytes, ref pos);
                        var value = BitConverter.ToSingle(bytes, pos);

                        pos += 4;

                        if (Enum.TryParse(key, out MorphotypeElementDefinition.ElementCategory category))
                        {
                            data.MorphotypeAdditionalValues[category] = value;
                        }
                    }
                }

                return !string.IsNullOrEmpty(data.Name);
            }
            catch (Exception ex)
            {
                Main.Error($"could not read old character {path}: {ex.Message}");
                data = null;

                return false;
            }
        }

        private static byte[] ExtractPng(byte[] raw)
        {
            if (raw == null)
            {
                return null;
            }

            for (var i = 0; i + 4 <= raw.Length; i++)
            {
                if (raw[i] == 0x89 && raw[i + 1] == (byte)'P' && raw[i + 2] == (byte)'N' && raw[i + 3] == (byte)'G')
                {
                    return raw.Skip(i).ToArray();
                }
            }

            return null;
        }

        private static string GetValue(byte[] bytes, Dictionary<string, List<Element>> index, string path)
        {
            return index.TryGetValue(path, out var elements) ? elements[0].BodyAsString(bytes) : null;
        }

        private static List<string> GetValues(byte[] bytes, Dictionary<string, List<Element>> index, string path)
        {
            return index.TryGetValue(path, out var elements)
                ? elements.Select(x => x.BodyAsString(bytes)).Where(x => !string.IsNullOrEmpty(x)).ToList()
                : [];
        }

        private static string ReadString(byte[] bytes, ref int pos)
        {
            var length = 0;
            var shift = 0;

            while (true)
            {
                var b = bytes[pos++];

                length |= (b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                {
                    break;
                }

                shift += 7;
            }

            var text = Encoding.UTF8.GetString(bytes, pos, length);

            pos += length;

            return text;
        }

        private static bool TryParseElement(byte[] bytes, int pos, int end, out Element element)
        {
            element = null;

            try
            {
                var name = ReadString(bytes, ref pos);

                if (name.Length == 0 || pos + 8 > end)
                {
                    return false;
                }

                var total = BitConverter.ToInt32(bytes, pos);
                var attributesLength = BitConverter.ToInt32(bytes, pos + 4);
                var elementEnd = pos + total;
                var attributesEnd = pos + 4 + attributesLength;

                if (total < 8 || elementEnd > end || attributesLength < 4 || attributesEnd > elementEnd)
                {
                    return false;
                }

                var attributes = new Dictionary<string, (int Start, int Length)>();
                var apos = pos + 8;

                while (apos < attributesEnd)
                {
                    var size = BitConverter.ToInt32(bytes, apos);

                    if (size < 5 || apos + size > attributesEnd)
                    {
                        return false;
                    }

                    var vpos = apos + 4;
                    var attributeName = ReadString(bytes, ref vpos);

                    attributes[attributeName] = (vpos, apos + size - vpos);
                    apos += size;
                }

                element = new Element(name, attributes, attributesEnd, elementEnd - attributesEnd);

                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }

        private static bool TryParseEntry(byte[] bytes, int pos, int end, out string label, out Element element)
        {
            if (TryParseElement(bytes, pos, end, out element))
            {
                label = element.Name;

                return true;
            }

            label = null;

            try
            {
                var after = pos;
                var key = ReadString(bytes, ref after);

                if (!TryParseElement(bytes, after, end, out element))
                {
                    return false;
                }

                label = $"{key} -> {element.Name}";

                return true;
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }

        private static bool ParsesAsElements(byte[] bytes, int pos, int end)
        {
            while (pos < end)
            {
                if (!TryParseEntry(bytes, pos, end, out _, out var element))
                {
                    return false;
                }

                pos = element.BodyStart + element.BodyLength;
            }

            return true;
        }

        private static void Index(byte[] bytes, int pos, int end, string path, Dictionary<string, List<Element>> index)
        {
            while (pos < end && TryParseEntry(bytes, pos, end, out var label, out var element))
            {
                var here = $"{path}/{label}";

                if (!index.TryGetValue(here, out var list))
                {
                    index[here] = list = [];
                }

                list.Add(element);

                var bodyEnd = element.BodyStart + element.BodyLength;

                if (element.BodyLength > 0 && ParsesAsElements(bytes, element.BodyStart, bodyEnd))
                {
                    Index(bytes, element.BodyStart, bodyEnd, here, index);
                }

                pos = bodyEnd;
            }
        }

        private sealed class Element(
            string name,
            Dictionary<string, (int Start, int Length)> attributes,
            int bodyStart,
            int bodyLength)
        {
            internal string Name { get; } = name;
            internal int BodyStart { get; } = bodyStart;
            internal int BodyLength { get; } = bodyLength;

            internal byte[] GetRaw(byte[] bytes, string attribute)
            {
                if (!attributes.TryGetValue(attribute, out var range))
                {
                    return null;
                }

                var raw = new byte[range.Length];

                Array.Copy(bytes, range.Start, raw, 0, range.Length);

                return raw;
            }

            internal string GetString(byte[] bytes, string attribute)
            {
                if (!attributes.TryGetValue(attribute, out var range) || range.Length == 0)
                {
                    return null;
                }

                var pos = range.Start;

                return ReadString(bytes, ref pos);
            }

            internal string BodyAsString(byte[] bytes)
            {
                if (BodyLength <= 0)
                {
                    return null;
                }

                var pos = BodyStart;
                var text = ReadString(bytes, ref pos);

                return pos == BodyStart + BodyLength ? text : null;
            }
        }
    }

    #endregion
}
