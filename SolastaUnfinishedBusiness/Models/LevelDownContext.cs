using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Photon.Pun;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Records what every level-up changed on a hero, so the last level can be undone exactly.
// A snapshot is taken when a level-up starts and compared with the hero when it is finalized. Both run on every
// computer in multiplayer (vanilla sends each level-up step as a lockstep command), and so does the level down.
internal static class LevelDownContext
{
    private const string LevelDownName = "RestActivityLevelDown";

    // sent through vanilla AssignClassLevelRPC in place of a class name, so the level down runs at the same tick
    // on every computer without a new network message
    private const string LevelDownCommandMarker = "UbLevelDown:";

    // first element of the saved history; heroes saved without a history don't have it
    private const string HistoryCountElement = "UbLevelHistoryCount";

    private const RestActivityDefinition.ActivityCondition ActivityConditionDisabled =
        (RestActivityDefinition.ActivityCondition)(-1001);

    // computed from class levels or kept as earned, so they are never restored from a level record
    private static readonly HashSet<string> IgnoredAttributes =
    [
        AttributeDefinitions.Experience,
        AttributeDefinitions.CharacterLevel,
        AttributeDefinitions.ProficiencyBonus,
        AttributeDefinitions.HitPoints
    ];

    private static readonly ConditionalWeakTable<RulesetCharacterHero, LevelHistory> Histories = new();
    private static readonly ConditionalWeakTable<RulesetCharacterHero, HeroSnapshot> Snapshots = new();

    private static RestActivityDefinition RestActivityLevelDown { get; } = RestActivityDefinitionBuilder
        .Create(LevelDownName)
        .SetGuiPresentation(Category.RestActivity)
        .SetRestData(
            RestDefinitions.RestStage.AfterRest,
            RestType.LongRest,
            RestActivityDefinition.ActivityCondition.None,
            LevelDownName,
            string.Empty)
        .AddToDB();

    internal static void Load()
    {
        ServiceRepository.GetService<IFunctorService>().RegisterFunctor(LevelDownName, new FunctorLevelDown());
        SwitchLevelDown();
    }

    internal static void SwitchLevelDown()
    {
        RestActivityLevelDown.condition = Main.Settings.EnableLevelDownAction
            ? RestActivityDefinition.ActivityCondition.None
            : ActivityConditionDisabled;
    }

    // shown above level 1; without a recorded level the button is disabled and its tooltip says why
    internal static bool IsRestActivityAvailable(RestActivityDefinition activity, RulesetCharacterHero hero)
    {
        return activity != RestActivityLevelDown || hero.ClassesHistory.Count > 1;
    }

    internal static void DecorateAfterRestItem(AfterRestActionItem item)
    {
        var hero = item.Hero;

        if (item.RestActivityDefinition != RestActivityLevelDown || hero == null || TryGetLastRecord(hero, out _))
        {
            return;
        }

        if (item.button)
        {
            item.button.interactable = false;
        }

        if (item.GuiTooltip)
        {
            item.GuiTooltip.Content = UnavailableDescription(hero);
        }
    }

    private static string UnavailableDescription(RulesetCharacterHero hero)
    {
        return Gui.Format("Message/&LevelDownUnavailableDescription",
            hero.Name, hero.ClassesHistory.Count.ToString());
    }

    #region Recording

    internal static void TakeSnapshot(RulesetCharacterHero hero)
    {
        Snapshots.Remove(hero);
        Snapshots.Add(hero, new HeroSnapshot(hero));
    }

    // called at the very end of FinalizeCharacter, after the mod's own finalize logic
    internal static void RecordLevelUp(RulesetCharacterHero hero)
    {
        if (!Snapshots.TryGetValue(hero, out var before))
        {
            return;
        }

        Snapshots.Remove(hero);

        if (hero.ClassesHistory.Count != before.Level + 1)
        {
            return;
        }

        var record = LevelRecord.Create(before, new HeroSnapshot(hero), hero);
        var history = GetOrCreateHistory(hero);

        history.Records.RemoveAll(x => x.Level >= record.Level);
        history.Records.Add(record);
    }

    private static LevelHistory GetOrCreateHistory(RulesetCharacterHero hero)
    {
        if (Histories.TryGetValue(hero, out var history))
        {
            return history;
        }

        history = new LevelHistory();
        Histories.Add(hero, history);

        return history;
    }

    private static bool TryGetLastRecord(RulesetCharacterHero hero, out LevelRecord record)
    {
        record = null;

        if (hero == null || !Histories.TryGetValue(hero, out var history))
        {
            return false;
        }

        var level = hero.ClassesHistory.Count;

        // records above the current level are stale (the hero was changed some other way)
        history.Records.RemoveAll(x => x.Level > level);
        record = history.Records.FirstOrDefault(x => x.Level == level);

        return record != null && level > 1 && record.Class == hero.ClassesHistory[level - 1];
    }

    #endregion

    #region Level Down

    private static void RequestLevelDown(RulesetCharacterHero hero)
    {
        var level = hero.ClassesHistory.Count;

        if (Global.IsMultiplayer &&
            ServiceRepository.GetService<INetworkingService>() is NetworkingManager { view: not null } networking)
        {
            networking.view.RPC(nameof(NetworkingManager.AssignClassLevelRPC), RpcTarget.AllViaServer,
                networking.CommandTick, (long)hero.Guid, $"{LevelDownCommandMarker}{level}", false);

            return;
        }

        LevelDown(hero, level);
    }

    // replaces the vanilla coroutine when the class name is the level down marker
    internal static bool TryHandleLevelDownCommand(
        NetworkingManager networking, long heroGuid, string classDefinitionName, ref IEnumerator result)
    {
        if (classDefinitionName == null ||
            !classDefinitionName.StartsWith(LevelDownCommandMarker) ||
            !int.TryParse(classDefinitionName.Substring(LevelDownCommandMarker.Length), out var level))
        {
            return false;
        }

        result = LevelDownAsync(networking, heroGuid, level);

        return true;
    }

    private static IEnumerator LevelDownAsync(NetworkingManager networking, long heroGuid, int level)
    {
        var hero = networking.GetEntityFromGuid<RulesetCharacterHero>(heroGuid, false);

        if (hero != null)
        {
            LevelDown(hero, level);
        }

        yield break;
    }

    // level is the hero's level when the level down was requested, so a repeated command does nothing
    private static void LevelDown(RulesetCharacterHero hero, int level)
    {
        if (hero.ClassesHistory.Count != level ||
            !TryGetLastRecord(hero, out var record) ||
            ServiceRepository.GetService<ICharacterBuildingService>() is CharacterBuildingManager building &&
            building.IsHeroCurrentlyLevelingUp(hero))
        {
            return;
        }

        record.Revert(hero);
        Histories.TryGetValue(hero, out var history);
        history?.Records.Remove(record);

        hero.RemoveClassLevel();
        hero.UpgradeSpellRepertoires();
        hero.GrantInvocations();
        hero.GrantPowers();
        hero.RefreshAll();

        Main.Info($"Level down [{hero.Name}] from level {level} to {level - 1}");

        var restModal = Gui.GuiService.GetScreen<RestModal>();

        if (restModal && restModal.Visible)
        {
            restModal.Refresh();
        }

        // the after-rest modules (hit dice and others) are bound when the panel opens, so they are bound again
        var restAfterPanel = UnityEngine.Object.FindObjectOfType<RestAfterPanel>();

        if (!restAfterPanel || !restAfterPanel.Visible)
        {
            return;
        }

        foreach (var module in restAfterPanel.RestModules.Where(x => x && x.gameObject.activeSelf))
        {
            module.Bind(restAfterPanel.PerformedRestType, RestDefinitions.RestStage.AfterRest,
                restAfterPanel.RestModuleRefreshed);
        }

        restAfterPanel.RefreshNow();
    }

    internal sealed class FunctorLevelDown : Functor
    {
        public override IEnumerator Execute(
            FunctorParametersDescription functorParameters, FunctorExecutionContext context)
        {
            var hero = functorParameters.RestingHero;

            if (!TryGetLastRecord(hero, out var record))
            {
                Gui.GuiService.ShowMessage(
                    MessageModal.Severity.Informative1,
                    "Message/&LevelDownTitle",
                    hero == null ? string.Empty : UnavailableDescription(hero),
                    "Message/&MessageOkTitle", string.Empty,
                    null, null);

                yield break;
            }

            Gui.GuiService.ShowMessage(
                MessageModal.Severity.Attention2,
                "Message/&LevelDownTitle",
                Gui.Format("Message/&LevelDownConfirmDescription",
                    hero.Name,
                    record.Level.ToString(),
                    (record.Level - 1).ToString(),
                    Gui.Localize(record.Class.GuiPresentation.Title),
                    record.ClassLevel.ToString()),
                "Message/&MessageYesTitle", "Message/&MessageNoTitle",
                () => RequestLevelDown(hero), null);
        }
    }

    #endregion

    #region Serialization

    // The game reads elements by position, so a missing element can't simply be read. The history is written only
    // when there is one, and read in saves only when its first element is next. The network always carries it.
    internal static void SerializeHistory(RulesetCharacterHero hero, IElementsSerializer serializer)
    {
        if (serializer is not BinarySerializer binary)
        {
            return;
        }

        if (serializer.Mode == Serializer.SerializationMode.Write)
        {
            Histories.TryGetValue(hero, out var history);

            if (binary.persistentContext && (history == null || history.Records.Count == 0))
            {
                return;
            }

            (history ?? new LevelHistory()).Serialize(serializer);

            return;
        }

        Histories.Remove(hero);

        if (binary.persistentContext && !IsNextElement(binary, HistoryCountElement))
        {
            return;
        }

        var readHistory = new LevelHistory();

        readHistory.Serialize(serializer);

        if (readHistory.Records.Count > 0)
        {
            Histories.Add(hero, readHistory);
        }
    }

    // compares the next element name without reading it; a persistent element starts with its 7-bit length name
    private static bool IsNextElement(BinarySerializer binary, string elementName)
    {
        var stream = binary.binaryReader?.BaseStream;

        if (stream is not { CanSeek: true })
        {
            return false;
        }

        var start = stream.Position;

        try
        {
            var expected = Encoding.UTF8.GetBytes(elementName);
            var length = 0;
            var shift = 0;
            int value;

            do
            {
                value = stream.ReadByte();

                if (value < 0 || shift > 28)
                {
                    return false;
                }

                length |= (value & 0x7F) << shift;
                shift += 7;
            } while ((value & 0x80) != 0);

            if (length != expected.Length)
            {
                return false;
            }

            return expected.All(x => stream.ReadByte() == x);
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = start;
        }
    }

    private static void SerializeReferences<T>(IElementsSerializer serializer, string name, List<T> list)
        where T : BaseDefinition
    {
        BaseDefinition.SerializeDatabaseReferenceList(serializer, name, "Item", list);

        // definitions that no longer exist are dropped
        list.RemoveAll(x => x == null);
    }

    private static List<string> SerializeStrings(IElementsSerializer serializer, string name, List<string> list)
    {
        return serializer.SerializeElement(name, list) ?? [];
    }

    #endregion

    #region Data

    private static void Diff<T>(List<T> before, List<T> after, List<T> added, List<T> removed)
    {
        var remaining = new List<T>(before);

        foreach (var item in after.Where(item => !remaining.Remove(item)))
        {
            added.Add(item);
        }

        removed.AddRange(remaining);
    }

    private static List<T> Reverted<T>(List<T> current, List<T> added, List<T> removed)
    {
        var target = new List<T>(current);

        foreach (var item in added)
        {
            target.Remove(item);
        }

        target.AddRange(removed);

        return target;
    }

    private static void Assign<T>(List<T> list, List<T> values)
    {
        list.Clear();
        list.AddRange(values);
    }

    // runs the mod's custom level-up logic of features, feats and invocations
    private static void RunCustomLogic(
        RulesetCharacterHero hero, string tag, IEnumerable<BaseDefinition> definitions, bool apply)
    {
        foreach (var definition in definitions.Where(x => x))
        {
            foreach (var customCode in definition.GetAllSubFeaturesOfType<ICustomLevelUpLogic>())
            {
                if (apply)
                {
                    customCode.ApplyFeature(hero, tag);
                }
                else
                {
                    customCode.RemoveFeature(hero, tag);
                }
            }

            switch (definition)
            {
                case FeatureDefinitionFeatureSet
                {
                    Mode: FeatureDefinitionFeatureSet.FeatureSetMode.Union
                } featureSet:
                    RunCustomLogic(hero, tag, featureSet.FeatureSet, apply);
                    break;
                case FeatDefinition feat:
                    RunCustomLogic(hero, tag, feat.Features, apply);
                    break;
                case InvocationDefinition invocation:
                    RunCustomLogic(hero, tag, [invocation.GrantedFeature], apply);
                    break;
            }
        }
    }

    private static string RepertoireKey(RulesetSpellRepertoire repertoire)
    {
        return $"{repertoire.spellCastingFeature?.Name}|{repertoire.spellCastingClass?.Name}|" +
               $"{repertoire.spellCastingSubclass?.Name}|{repertoire.spellCastingRace?.Name}";
    }

    private sealed class HeroSnapshot
    {
        internal readonly Dictionary<string, List<FeatureDefinition>> ActiveFeatures;
        internal readonly Dictionary<string, int> Attributes;
        internal readonly Dictionary<CharacterClassDefinition, CharacterSubclassDefinition> ClassesAndSubclasses;
        internal readonly List<string> Expertises;
        internal readonly List<FeatDefinition> Feats;
        internal readonly List<FightingStyleDefinition> FightingStyles;
        internal readonly List<InvocationDefinition> Invocations;
        internal readonly List<LanguageDefinition> Languages;
        internal readonly int Level;
        internal readonly List<MetamagicOptionDefinition> MetamagicOptions;
        internal readonly Dictionary<string, RepertoireSnapshot> Repertoires;
        internal readonly List<SkillDefinition> Skills;
        internal readonly List<ToolTypeDefinition> ToolTypes;

        internal HeroSnapshot(RulesetCharacterHero hero)
        {
            Level = hero.ClassesHistory.Count;
            ActiveFeatures = hero.ActiveFeatures.ToDictionary(x => x.Key, x => new List<FeatureDefinition>(x.Value));
            Attributes = hero.Attributes
                .Where(x => !IgnoredAttributes.Contains(x.Key))
                .ToDictionary(x => x.Key, x => x.Value.BaseValue);
            ClassesAndSubclasses = new Dictionary<CharacterClassDefinition, CharacterSubclassDefinition>(
                hero.ClassesAndSubclasses);
            FightingStyles = [.. hero.trainedFightingStyles];
            Skills = [.. hero.trainedSkills];
            ToolTypes = [.. hero.trainedToolTypes];
            Expertises = [.. hero.trainedExpertises];
            Feats = [.. hero.trainedFeats];
            Languages = [.. hero.trainedLanguages];
            MetamagicOptions = [.. hero.trainedMetamagicOptions];
            Invocations = [.. hero.trainedInvocations];
            Repertoires = new Dictionary<string, RepertoireSnapshot>();

            foreach (var repertoire in hero.SpellRepertoires)
            {
                var key = RepertoireKey(repertoire);

                if (!Repertoires.ContainsKey(key))
                {
                    Repertoires.Add(key, new RepertoireSnapshot(repertoire));
                }
            }
        }
    }

    private sealed class RepertoireSnapshot(RulesetSpellRepertoire repertoire)
    {
        internal readonly List<SpellDefinition> Cantrips = [.. repertoire.knownCantrips];

        internal readonly Dictionary<string, List<SpellDefinition>> ExtraSpells =
            repertoire.extraSpellsByTag.ToDictionary(x => x.Key, x => new List<SpellDefinition>(x.Value));

        internal readonly List<SpellDefinition> Prepared = [.. repertoire.preparedSpells];
        internal readonly RulesetSpellRepertoire Repertoire = repertoire;
        internal readonly List<SpellDefinition> Spells = [.. repertoire.knownSpells];
    }

    private sealed class LevelHistory
    {
        internal readonly List<LevelRecord> Records = [];

        internal void Serialize(IElementsSerializer serializer)
        {
            var count = serializer.SerializeElement(HistoryCountElement, Records.Count);

            if (serializer.Mode == Serializer.SerializationMode.Read)
            {
                Records.Clear();

                for (var i = 0; i < count; i++)
                {
                    Records.Add(new LevelRecord());
                }
            }

            for (var i = 0; i < Records.Count; i++)
            {
                Records[i].Serialize(serializer, $"UbLevel{i}.");
            }

            Records.RemoveAll(x => !x.Class);
        }
    }

    private sealed class ListChange<T> where T : BaseDefinition
    {
        internal readonly List<T> Added = [];
        internal readonly List<T> Removed = [];

        internal bool IsEmpty => Added.Count == 0 && Removed.Count == 0;

        internal static ListChange<T> Create(List<T> before, List<T> after)
        {
            var change = new ListChange<T>();

            Diff(before, after, change.Added, change.Removed);

            return change;
        }

        internal List<T> Revert(List<T> current)
        {
            return Reverted(current, Added, Removed);
        }

        internal void Serialize(IElementsSerializer serializer, string name)
        {
            SerializeReferences(serializer, name + "Added", Added);
            SerializeReferences(serializer, name + "Removed", Removed);
        }
    }

    private sealed class FeatureTagChange
    {
        internal ListChange<FeatureDefinition> Features = new();
        internal bool IsNewTag;
        internal string Tag = string.Empty;

        internal void Serialize(IElementsSerializer serializer, string name)
        {
            Tag = serializer.SerializeElement(name + "Tag", Tag) ?? string.Empty;
            IsNewTag = serializer.SerializeElement(name + "IsNewTag", IsNewTag);
            Features.Serialize(serializer, name);
        }
    }

    private sealed class ExtraSpellsChange
    {
        internal ListChange<SpellDefinition> Spells = new();
        internal string Tag = string.Empty;

        internal void Serialize(IElementsSerializer serializer, string name)
        {
            Tag = serializer.SerializeElement(name + "Tag", Tag) ?? string.Empty;
            Spells.Serialize(serializer, name);
        }
    }

    private sealed class RepertoireChange
    {
        internal readonly List<ExtraSpellsChange> ExtraSpells = [];
        internal ListChange<SpellDefinition> Cantrips = new();
        internal CharacterClassDefinition Class;
        internal bool Created;
        internal FeatureDefinitionCastSpell Feature;
        internal ListChange<SpellDefinition> Prepared = new();
        internal CharacterRaceDefinition Race;
        internal ListChange<SpellDefinition> Spells = new();
        internal CharacterSubclassDefinition Subclass;

        internal bool IsEmpty => !Created && Cantrips.IsEmpty && Spells.IsEmpty && Prepared.IsEmpty &&
                                 ExtraSpells.Count == 0;

        internal string Key => $"{Feature?.Name}|{Class?.Name}|{Subclass?.Name}|{Race?.Name}";

        internal static RepertoireChange Create(RepertoireSnapshot before, RepertoireSnapshot after)
        {
            var repertoire = after.Repertoire;
            var change = new RepertoireChange
            {
                Feature = repertoire.spellCastingFeature,
                Class = repertoire.spellCastingClass,
                Subclass = repertoire.spellCastingSubclass,
                Race = repertoire.spellCastingRace,
                Created = before == null
            };

            if (before == null)
            {
                return change;
            }

            change.Cantrips = ListChange<SpellDefinition>.Create(before.Cantrips, after.Cantrips);
            change.Spells = ListChange<SpellDefinition>.Create(before.Spells, after.Spells);
            change.Prepared = ListChange<SpellDefinition>.Create(before.Prepared, after.Prepared);

            foreach (var tag in before.ExtraSpells.Keys.Union(after.ExtraSpells.Keys))
            {
                before.ExtraSpells.TryGetValue(tag, out var beforeSpells);
                after.ExtraSpells.TryGetValue(tag, out var afterSpells);

                var extra = new ExtraSpellsChange
                {
                    Tag = tag,
                    Spells = ListChange<SpellDefinition>.Create(beforeSpells ?? [], afterSpells ?? [])
                };

                if (!extra.Spells.IsEmpty)
                {
                    change.ExtraSpells.Add(extra);
                }
            }

            return change;
        }

        internal void Revert(RulesetCharacterHero hero)
        {
            var repertoire = hero.SpellRepertoires.FirstOrDefault(x => RepertoireKey(x) == Key);

            if (repertoire == null)
            {
                return;
            }

            if (Created)
            {
                hero.SpellRepertoires.Remove(repertoire);

                return;
            }

            Assign(repertoire.knownCantrips, Cantrips.Revert(repertoire.knownCantrips));
            Assign(repertoire.knownSpells, Spells.Revert(repertoire.knownSpells));
            Assign(repertoire.preparedSpells, Prepared.Revert(repertoire.preparedSpells));

            foreach (var extra in ExtraSpells)
            {
                repertoire.extraSpellsByTag.TryGetValue(extra.Tag, out var current);

                var spells = extra.Spells.Revert(current ?? []);

                if (spells.Count == 0)
                {
                    repertoire.extraSpellsByTag.Remove(extra.Tag);
                }
                else
                {
                    repertoire.extraSpellsByTag[extra.Tag] = spells;
                }
            }
        }

        internal void Serialize(IElementsSerializer serializer, string name)
        {
            Feature = BaseDefinition.SerializeDatabaseReference(serializer, name + "Feature", Feature);
            Class = BaseDefinition.SerializeDatabaseReference(serializer, name + "Class", Class);
            Subclass = BaseDefinition.SerializeDatabaseReference(serializer, name + "Subclass", Subclass);
            Race = BaseDefinition.SerializeDatabaseReference(serializer, name + "Race", Race);
            Created = serializer.SerializeElement(name + "Created", Created);
            Cantrips.Serialize(serializer, name + "Cantrips");
            Spells.Serialize(serializer, name + "Spells");
            Prepared.Serialize(serializer, name + "Prepared");

            var count = serializer.SerializeElement(name + "ExtraCount", ExtraSpells.Count);

            if (serializer.Mode == Serializer.SerializationMode.Read)
            {
                ExtraSpells.Clear();

                for (var i = 0; i < count; i++)
                {
                    ExtraSpells.Add(new ExtraSpellsChange());
                }
            }

            for (var i = 0; i < ExtraSpells.Count; i++)
            {
                ExtraSpells[i].Serialize(serializer, $"{name}Extra{i}.");
            }
        }
    }

    private sealed class LevelRecord
    {
        internal readonly List<string> AttributeNames = [];
        internal readonly List<FeatureTagChange> Features = [];
        internal readonly List<RepertoireChange> Repertoires = [];
        internal CharacterSubclassDefinition AddedSubclass;
        internal List<int> AttributeDeltas = [];
        internal CharacterClassDefinition Class;
        internal int ClassLevel;
        internal List<string> ExpertisesAdded = [];
        internal List<string> ExpertisesRemoved = [];
        internal ListChange<FeatDefinition> Feats = new();
        internal ListChange<FightingStyleDefinition> FightingStyles = new();
        internal ListChange<InvocationDefinition> Invocations = new();
        internal ListChange<LanguageDefinition> Languages = new();
        internal int Level;
        internal ListChange<MetamagicOptionDefinition> MetamagicOptions = new();
        internal ListChange<SkillDefinition> Skills = new();
        internal ListChange<ToolTypeDefinition> ToolTypes = new();

        private string ClassTag => AttributeDefinitions.GetClassTag(Class, ClassLevel);

        internal static LevelRecord Create(HeroSnapshot before, HeroSnapshot after, RulesetCharacterHero hero)
        {
            var record = new LevelRecord
            {
                Level = after.Level,
                Class = hero.ClassesHistory[after.Level - 1],
                FightingStyles = ListChange<FightingStyleDefinition>.Create(before.FightingStyles, after.FightingStyles),
                Skills = ListChange<SkillDefinition>.Create(before.Skills, after.Skills),
                ToolTypes = ListChange<ToolTypeDefinition>.Create(before.ToolTypes, after.ToolTypes),
                Feats = ListChange<FeatDefinition>.Create(before.Feats, after.Feats),
                Languages = ListChange<LanguageDefinition>.Create(before.Languages, after.Languages),
                MetamagicOptions =
                    ListChange<MetamagicOptionDefinition>.Create(before.MetamagicOptions, after.MetamagicOptions),
                Invocations = ListChange<InvocationDefinition>.Create(before.Invocations, after.Invocations)
            };

            record.ClassLevel = hero.ClassesAndLevels.TryGetValue(record.Class, out var classLevel) ? classLevel : 0;

            if (after.ClassesAndSubclasses.TryGetValue(record.Class, out var subclass) &&
                !before.ClassesAndSubclasses.ContainsKey(record.Class))
            {
                record.AddedSubclass = subclass;
            }

            Diff(before.Expertises, after.Expertises, record.ExpertisesAdded, record.ExpertisesRemoved);

            foreach (var tag in before.ActiveFeatures.Keys.Union(after.ActiveFeatures.Keys))
            {
                var hadTag = before.ActiveFeatures.TryGetValue(tag, out var beforeFeatures);

                after.ActiveFeatures.TryGetValue(tag, out var afterFeatures);

                var change = new FeatureTagChange
                {
                    Tag = tag,
                    IsNewTag = !hadTag,
                    Features = ListChange<FeatureDefinition>.Create(beforeFeatures ?? [], afterFeatures ?? [])
                };

                if (!change.Features.IsEmpty || change.IsNewTag)
                {
                    record.Features.Add(change);
                }
            }

            foreach (var attribute in after.Attributes)
            {
                before.Attributes.TryGetValue(attribute.Key, out var beforeValue);

                var delta = attribute.Value - beforeValue;

                if (delta == 0 || !before.Attributes.ContainsKey(attribute.Key))
                {
                    continue;
                }

                record.AttributeNames.Add(attribute.Key);
                record.AttributeDeltas.Add(delta);
            }

            foreach (var repertoire in after.Repertoires)
            {
                before.Repertoires.TryGetValue(repertoire.Key, out var beforeRepertoire);

                var change = RepertoireChange.Create(beforeRepertoire, repertoire.Value);

                if (!change.IsEmpty)
                {
                    record.Repertoires.Add(change);
                }
            }

            return record;
        }

        // undoes everything this level changed; the class level itself is removed by the caller
        internal void Revert(RulesetCharacterHero hero)
        {
            // targets are computed first, so whatever custom logic changes in these lists is overwritten below
            var fightingStyles = FightingStyles.Revert(hero.trainedFightingStyles);
            var skills = Skills.Revert(hero.trainedSkills);
            var toolTypes = ToolTypes.Revert(hero.trainedToolTypes);
            var expertises = Reverted(hero.trainedExpertises, ExpertisesAdded, ExpertisesRemoved);
            var feats = Feats.Revert(hero.trainedFeats);
            var languages = Languages.Revert(hero.trainedLanguages);
            var metamagicOptions = MetamagicOptions.Revert(hero.trainedMetamagicOptions);
            var invocations = Invocations.Revert(hero.trainedInvocations);

            foreach (var change in Features)
            {
                RunCustomLogic(hero, change.Tag, change.Features.Added, false);
            }

            RunCustomLogic(hero, ClassTag, Feats.Added, false);
            RunCustomLogic(hero, ClassTag, Invocations.Added, false);

            foreach (var change in Features)
            {
                hero.ActiveFeatures.TryGetValue(change.Tag, out var current);

                var features = change.Features.Revert(current ?? []);

                // a tag gained at this level goes with all its modifiers, ability score increases included
                if (change.IsNewTag || features.Count == 0)
                {
                    hero.ClearFeatureModifiers(change.Tag);
                    hero.ActiveFeatures.Remove(change.Tag);

                    continue;
                }

                hero.ActiveFeatures[change.Tag] = features;
                hero.UpdateFeatureModifiers(change.Tag, true);
            }

            // features, feats and invocations this level took away come back with their custom logic
            foreach (var change in Features.Where(x => !x.IsNewTag))
            {
                RunCustomLogic(hero, change.Tag, change.Features.Removed, true);
            }

            RunCustomLogic(hero, ClassTag, Feats.Removed, true);
            RunCustomLogic(hero, ClassTag, Invocations.Removed, true);

            Assign(hero.trainedFightingStyles, fightingStyles);
            Assign(hero.trainedSkills, skills);
            Assign(hero.trainedToolTypes, toolTypes);
            Assign(hero.trainedExpertises, expertises);
            Assign(hero.trainedFeats, feats);
            Assign(hero.trainedLanguages, languages);
            Assign(hero.trainedMetamagicOptions, metamagicOptions);
            Assign(hero.trainedInvocations, invocations);

            if (AddedSubclass)
            {
                hero.ClassesAndSubclasses.Remove(Class);
            }

            foreach (var repertoire in Repertoires)
            {
                repertoire.Revert(hero);
            }

            for (var i = 0; i < AttributeNames.Count && i < AttributeDeltas.Count; i++)
            {
                if (!hero.TryGetAttribute(AttributeNames[i], out var attribute))
                {
                    continue;
                }

                attribute.BaseValue -= AttributeDeltas[i];
                attribute.Refresh();
            }
        }

        internal void Serialize(IElementsSerializer serializer, string name)
        {
            Level = serializer.SerializeElement(name + "Level", Level);
            Class = BaseDefinition.SerializeDatabaseReference(serializer, name + "Class", Class);
            ClassLevel = serializer.SerializeElement(name + "ClassLevel", ClassLevel);
            AddedSubclass = BaseDefinition.SerializeDatabaseReference(serializer, name + "AddedSubclass", AddedSubclass);

            FightingStyles.Serialize(serializer, name + "FightingStyles");
            Skills.Serialize(serializer, name + "Skills");
            ToolTypes.Serialize(serializer, name + "ToolTypes");
            ExpertisesAdded = SerializeStrings(serializer, name + "ExpertisesAdded", ExpertisesAdded);
            ExpertisesRemoved = SerializeStrings(serializer, name + "ExpertisesRemoved", ExpertisesRemoved);
            Feats.Serialize(serializer, name + "Feats");
            Languages.Serialize(serializer, name + "Languages");
            MetamagicOptions.Serialize(serializer, name + "MetamagicOptions");
            Invocations.Serialize(serializer, name + "Invocations");

            var attributeNames = SerializeStrings(serializer, name + "AttributeNames", AttributeNames);

            if (!ReferenceEquals(attributeNames, AttributeNames))
            {
                Assign(AttributeNames, attributeNames);
            }

            AttributeDeltas = serializer.SerializeElement(name + "AttributeDeltas", AttributeDeltas) ?? [];

            var featureCount = serializer.SerializeElement(name + "FeatureTagCount", Features.Count);
            var repertoireCount = serializer.SerializeElement(name + "RepertoireCount", Repertoires.Count);

            if (serializer.Mode == Serializer.SerializationMode.Read)
            {
                Features.Clear();
                Repertoires.Clear();

                for (var i = 0; i < featureCount; i++)
                {
                    Features.Add(new FeatureTagChange());
                }

                for (var i = 0; i < repertoireCount; i++)
                {
                    Repertoires.Add(new RepertoireChange());
                }
            }

            for (var i = 0; i < Features.Count; i++)
            {
                Features[i].Serialize(serializer, $"{name}FeatureTag{i}.");
            }

            for (var i = 0; i < Repertoires.Count; i++)
            {
                Repertoires[i].Serialize(serializer, $"{name}Repertoire{i}.");
            }
        }
    }

    #endregion
}
