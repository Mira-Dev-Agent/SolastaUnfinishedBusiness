using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Game-state fingerprint for multiplayer desyncs. Every few seconds each computer sends, for the same tick, each
// character's HP, temporary HP, position, conditions and spell slots plus the position in the shared dice stream.
// Every computer compares itself with the host. A difference seen twice in a row is a desync: it names the character
// and what differs. Out of combat the host can quick-correct simple differences (HP, position, slots, dice) at a
// lockstep tick; anything else, or a correction that didn't hold, offers the host a resync (quick-save and reload,
// which brings every client into the same save).
internal static class MultiplayerDesyncContext
{
    private const byte EventCode = 171;
    private const byte FingerprintKind = 1;
    private const byte CorrectionKind = 2;
    private const byte ResyncKind = 3;
    private const ulong FingerprintTickInterval = 100;
    private const int ValuesPerCharacter = 9;
    private const float ResyncSaveTimeout = 30f;

    private static readonly Dictionary<ulong, Fingerprint> LocalByTick = [];
    private static readonly Dictionary<ulong, Fingerprint> HostByTick = [];
    private static readonly Dictionary<int, HashSet<string>> PreviousDifferencesByActor = [];
    private static readonly FieldInfo[] RandomStateFields =
        typeof(UnityEngine.Random.State).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(x => x.FieldType == typeof(int))
            .OrderBy(x => x.Name)
            .ToArray();

    private static bool _eventHandlerRegistered;
    private static ulong _lastFingerprintTick = ulong.MaxValue;
    private static bool _correctionPending;
    private static bool _resyncOffered;

    internal static void Reset()
    {
        LocalByTick.Clear();
        HostByTick.Clear();
        PreviousDifferencesByActor.Clear();
        _lastFingerprintTick = ulong.MaxValue;
        _correctionPending = false;
        _resyncOffered = false;
    }

    #region Sending

    internal static void SendFingerprintIfNeeded(NetworkingManager networking)
    {
        if (!Main.Settings.MultiplayerEnableDesyncDetection ||
            !Main.Settings.MultiplayerEnableStateFingerprint ||
            !Global.IsMultiplayer ||
            !PhotonNetwork.InRoom ||
            PhotonNetwork.OfflineMode ||
            networking.frozenGameplay ||
            !networking.ValidGameplayContext())
        {
            return;
        }

        RegisterEventHandler();

        var tick = networking.tick.LocalTick;

        if (tick == 0 || tick % FingerprintTickInterval != 0 || tick == _lastFingerprintTick)
        {
            return;
        }

        _lastFingerprintTick = tick;

        var fingerprint = Fingerprint.Capture(tick);

        LocalByTick[tick] = fingerprint;
        Forget(LocalByTick, tick);

        if (PhotonNetwork.IsMasterClient)
        {
            HostByTick[tick] = fingerprint;
            Forget(HostByTick, tick);
        }

        PhotonNetwork.RaiseEvent(EventCode, fingerprint.ToEvent(),
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);

        // a client compares itself as soon as the host's fingerprint for this tick is known
        if (!PhotonNetwork.IsMasterClient && HostByTick.TryGetValue(tick, out var host))
        {
            Compare(host, fingerprint, PhotonNetwork.MasterClient?.ActorNumber ?? 0);
        }
    }

    private static void Forget(Dictionary<ulong, Fingerprint> byTick, ulong tick)
    {
        foreach (var old in byTick.Keys.Where(x => x + (FingerprintTickInterval * 4) < tick).ToList())
        {
            byTick.Remove(old);
        }
    }

    private static void RegisterEventHandler()
    {
        if (_eventHandlerRegistered || PhotonNetwork.NetworkingClient == null)
        {
            return;
        }

        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _eventHandlerRegistered = true;
    }

    #endregion

    #region Receiving

    private static void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != EventCode || photonEvent.CustomData is not object[] { Length: > 0 } data)
        {
            return;
        }

        var sender = PhotonNetwork.CurrentRoom?.GetPlayer(photonEvent.Sender);

        switch ((byte)data[0])
        {
            case FingerprintKind:
                OnFingerprint(Fingerprint.FromEvent(data), sender);
                break;
            case CorrectionKind when sender is { IsMasterClient: true }:
                OnCorrection(data);
                break;
            case ResyncKind when sender is { IsMasterClient: true }:
                Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncHostResyncing", Gui.ColorAlert, 10f);
                break;
        }
    }

    private static void OnFingerprint(Fingerprint received, Player sender)
    {
        if (received == null || sender == null)
        {
            return;
        }

        if (sender.IsMasterClient)
        {
            HostByTick[received.Tick] = received;
            Forget(HostByTick, received.Tick);

            if (LocalByTick.TryGetValue(received.Tick, out var local))
            {
                Compare(received, local, sender.ActorNumber);
            }

            return;
        }

        if (PhotonNetwork.IsMasterClient && LocalByTick.TryGetValue(received.Tick, out var hostPrint))
        {
            Compare(hostPrint, received, sender.ActorNumber);
        }
    }

    #endregion

    #region Comparing

    // host is the host's fingerprint, other is this computer's (on a client) or a client's (on the host)
    private static void Compare(Fingerprint host, Fingerprint other, int otherActor)
    {
        // a check is only meaningful when nothing was moving or acting on either computer at that tick
        if (!host.Quiet || !other.Quiet)
        {
            return;
        }

        var differences = Differences(host, other);

        PreviousDifferencesByActor.TryGetValue(otherActor, out var previous);
        PreviousDifferencesByActor[otherActor] = new HashSet<string>(differences.Select(x => x.Key));

        // a difference must show up in two checks in a row, so a value changing between frames isn't reported
        var confirmed = differences.Where(x => previous != null && previous.Contains(x.Key)).ToList();

        if (confirmed.Count == 0)
        {
            if (differences.Count == 0)
            {
                _correctionPending = false;
            }

            return;
        }

        var playerName = PhotonNetwork.IsMasterClient
            ? PhotonNetwork.CurrentRoom?.GetPlayer(otherActor)?.NickName ?? "?"
            : Gui.Localize("ModUi/&MultiplayerHostRole").Trim();
        var details = string.Join("\n", confirmed.Select(x => x.Description).Take(6));

        Main.Error($"Multiplayer state desync with {playerName} at tick {host.Tick}:\n{details}");

        if (!PhotonNetwork.IsMasterClient)
        {
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncDetails", playerName, details),
                Gui.ColorFailure, 10f);

            return;
        }

        if (!_correctionPending && CanQuickCorrect(host, confirmed))
        {
            _correctionPending = true;
            SendCorrection(host, confirmed);
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncCorrecting", playerName, details),
                Gui.ColorAlert, 10f);

            return;
        }

        OfferResync(playerName, details);
    }

    private static List<Difference> Differences(Fingerprint host, Fingerprint other)
    {
        var differences = new List<Difference>();

        if (host.Dice != other.Dice)
        {
            differences.Add(new Difference("dice", DifferenceType.Dice,
                Gui.Localize("Message/&MultiplayerDesyncDice")));
        }

        foreach (var hostCharacter in host.Characters)
        {
            var name = CharacterName(hostCharacter.Guid);

            if (!other.TryGet(hostCharacter.Guid, out var otherCharacter))
            {
                differences.Add(new Difference($"{hostCharacter.Guid}:missing", DifferenceType.Other,
                    Gui.Format("Message/&MultiplayerDesyncMissing", name)));

                continue;
            }

            void Add(string field, DifferenceType type, string hostValue, string otherValue)
            {
                differences.Add(new Difference($"{hostCharacter.Guid}:{field}", type,
                    Gui.Format("Message/&MultiplayerDesyncField", name, Gui.Localize($"Message/&MultiplayerDesync{field}"),
                        hostValue, otherValue)));
            }

            if (hostCharacter.HitPoints != otherCharacter.HitPoints)
            {
                Add("HitPoints", DifferenceType.Correctable,
                    hostCharacter.HitPoints.ToString(), otherCharacter.HitPoints.ToString());
            }

            if (hostCharacter.TemporaryHitPoints != otherCharacter.TemporaryHitPoints)
            {
                Add("TemporaryHitPoints", DifferenceType.Correctable,
                    hostCharacter.TemporaryHitPoints.ToString(), otherCharacter.TemporaryHitPoints.ToString());
            }

            if (!hostCharacter.Position.Equals(otherCharacter.Position))
            {
                Add("Position", DifferenceType.Correctable,
                    hostCharacter.Position.ToString(), otherCharacter.Position.ToString());
            }

            if (hostCharacter.SpellSlots != otherCharacter.SpellSlots)
            {
                Add("SpellSlots", DifferenceType.Correctable, "-", "-");
            }

            if (hostCharacter.Conditions != otherCharacter.Conditions)
            {
                Add("Conditions", DifferenceType.Other, "-", "-");
            }
        }

        foreach (var otherCharacter in other.Characters.Where(x => !host.TryGet(x.Guid, out _)))
        {
            differences.Add(new Difference($"{otherCharacter.Guid}:extra", DifferenceType.Other,
                Gui.Format("Message/&MultiplayerDesyncExtra", CharacterName(otherCharacter.Guid))));
        }

        return differences;
    }

    private static string CharacterName(ulong guid)
    {
        var character = ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters
            .FirstOrDefault(x => x.Guid == guid);

        return character?.Name ?? $"#{guid}";
    }

    #endregion

    #region Quick Correct

    private static bool CanQuickCorrect(Fingerprint host, List<Difference> differences)
    {
        return Main.Settings.MultiplayerQuickCorrectDesync &&
               !host.InBattle &&
               differences.All(x => x.Type is DifferenceType.Correctable or DifferenceType.Dice);
    }

    // the host's values are applied at a lockstep tick on every computer (the host included), like a command
    private static void SendCorrection(Fingerprint host, List<Difference> differences)
    {
        if (ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking)
        {
            return;
        }

        var guids = differences
            .Select(x => x.Key.Split(':')[0])
            .Where(x => ulong.TryParse(x, out _))
            .Select(ulong.Parse)
            .Distinct()
            .ToList();
        var characters = guids
            .Select(x => host.TryGet(x, out var character) ? character : null)
            .Where(x => x != null)
            .ToList();
        var slots = new List<int>();

        for (var i = 0; i < characters.Count; i++)
        {
            foreach (var entry in SpellSlotEntries(FindCharacter(characters[i].Guid)))
            {
                slots.AddRange([i, entry.repertoire, entry.level, entry.used]);
            }
        }

        var data = new object[]
        {
            CorrectionKind, networking.CommandTick, CaptureRandomState(),
            characters.Select(x => (long)x.Guid).ToArray(),
            characters.SelectMany(x => new[]
            {
                x.HitPoints, x.TemporaryHitPoints, x.Position.x, x.Position.y, x.Position.z
            }).ToArray(),
            slots.ToArray()
        };

        PhotonNetwork.RaiseEvent(EventCode, data,
            new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        PhotonNetwork.SendAllOutgoingCommands();
    }

    private static void OnCorrection(object[] data)
    {
        if (data.Length < 6 || ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking)
        {
            return;
        }

        networking.AddDelayedCommand(ApplyCorrection(data), (long)data[1]);
    }

    private static IEnumerator ApplyCorrection(object[] data)
    {
        var randomState = (int[])data[2];
        var guids = (long[])data[3];
        var values = (int[])data[4];
        var slots = (int[])data[5];
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();

        for (var i = 0; i < guids.Length; i++)
        {
            var character = FindCharacter((ulong)guids[i]);

            if (character?.RulesetCharacter == null)
            {
                continue;
            }

            var offset = i * 5;

            character.RulesetCharacter.currentHitPoints = values[offset];
            character.RulesetCharacter.temporaryHitPoints = values[offset + 1];

            var position = new int3(values[offset + 2], values[offset + 3], values[offset + 4]);

            if (!character.LocationPosition.Equals(position))
            {
                positioning?.TeleportCharacter(character, position, character.Orientation);
            }

            character.RulesetCharacter.RefreshAll();
        }

        for (var i = 0; i + 3 < slots.Length; i += 4)
        {
            var character = guids.Length > slots[i] ? FindCharacter((ulong)guids[slots[i]]) : null;
            var repertoires = character?.RulesetCharacter?.SpellRepertoires;

            if (repertoires == null || slots[i + 1] >= repertoires.Count)
            {
                continue;
            }

            repertoires[slots[i + 1]].usedSpellsSlots[slots[i + 2]] = slots[i + 3];
        }

        RestoreRandomState(randomState);
        Main.Info($"Multiplayer quick correct applied for {guids.Length} character(s)");

        yield break;
    }

    private static IEnumerable<(int repertoire, int level, int used)> SpellSlotEntries(GameLocationCharacter character)
    {
        var repertoires = character?.RulesetCharacter?.SpellRepertoires;

        if (repertoires == null)
        {
            yield break;
        }

        for (var r = 0; r < repertoires.Count; r++)
        {
            foreach (var slot in repertoires[r].usedSpellsSlots.OrderBy(x => x.Key))
            {
                yield return (r, slot.Key, slot.Value);
            }
        }
    }

    #endregion

    #region Resync

    private static void OfferResync(string playerName, string details)
    {
        if (!Main.Settings.MultiplayerOfferHostResync || _resyncOffered)
        {
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncDetails", playerName, details),
                Gui.ColorFailure, 10f);

            return;
        }

        _resyncOffered = true;

        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2,
            "Message/&MultiplayerDesyncResyncTitle",
            Gui.Format("Message/&MultiplayerDesyncResyncDescription", playerName, details),
            "Message/&MultiplayerDesyncResyncAccept", "Message/&MultiplayerDesyncResyncLater",
            StartResync, () => _resyncOffered = false);
    }

    private static void StartResync()
    {
        if (ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking ||
            ServiceRepository.GetService<IGameSerializationService>() is not GameSerializationManager serialization)
        {
            return;
        }

        PhotonNetwork.RaiseEvent(EventCode, new object[] { ResyncKind },
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
        networking.StartCoroutine(Resync(serialization));
    }

    // the host's quick save is the state everyone continues from: loading it as host brings every client in
    private static IEnumerator Resync(GameSerializationManager serialization)
    {
        var before = serialization.GetLatestQuickSave()?.Filename;
        var timeout = Time.unscaledTime + ResyncSaveTimeout;

        serialization.QuickSaveGame();

        while (Time.unscaledTime < timeout &&
               (serialization.saving || serialization.refreshing ||
                serialization.GetLatestQuickSave()?.Filename == before))
        {
            yield return null;
        }

        if (serialization.saving || serialization.GetLatestQuickSave() == null)
        {
            Main.Error("Multiplayer resync: the quick save didn't finish, nothing was loaded");
            _resyncOffered = false;

            yield break;
        }

        Main.Info($"Multiplayer resync: loading {serialization.GetLatestQuickSave().Filename}");
        Reset();
        serialization.QuickLoadGame();
    }

    #endregion

    #region Fingerprint

    private static GameLocationCharacter FindCharacter(ulong guid)
    {
        return ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters
            .FirstOrDefault(x => x.Guid == guid);
    }

    private static int[] CaptureRandomState()
    {
        if (ServiceRepository.GetService<IGameService>()?.Game is not { } game)
        {
            return [];
        }

        object state = game.randomState;

        return RandomStateFields.Select(x => (int)x.GetValue(state)).ToArray();
    }

    private static void RestoreRandomState(int[] values)
    {
        if (values == null || values.Length != RandomStateFields.Length ||
            ServiceRepository.GetService<IGameService>()?.Game is not { } game)
        {
            return;
        }

        object state = game.randomState;

        for (var i = 0; i < values.Length; i++)
        {
            RandomStateFields[i].SetValue(state, values[i]);
        }

        game.randomState = (UnityEngine.Random.State)state;
    }

    private static long Hash(IEnumerable<int> values)
    {
        // FNV-1a, 64 bits
        var hash = unchecked((long)14695981039346656037UL);

        foreach (var value in values)
        {
            hash = unchecked((hash ^ value) * 1099511628211L);
        }

        return hash;
    }

    private static IEnumerable<int> TextValues(string text)
    {
        return (text ?? string.Empty).Select(x => (int)x);
    }

    private enum DifferenceType
    {
        Correctable,
        Dice,
        Other
    }

    private sealed class Difference(string key, DifferenceType type, string description)
    {
        internal string Key { get; } = key;
        internal DifferenceType Type { get; } = type;
        internal string Description { get; } = description;
    }

    private sealed class CharacterPrint
    {
        internal long Conditions;
        internal ulong Guid;
        internal int HitPoints;
        internal int3 Position;
        internal long SpellSlots;
        internal int TemporaryHitPoints;
    }

    private sealed class Fingerprint
    {
        private readonly Dictionary<ulong, CharacterPrint> _byGuid = [];
        internal readonly List<CharacterPrint> Characters = [];
        internal long Dice;
        internal bool InBattle;
        internal bool Quiet;
        internal ulong Tick;

        internal bool TryGet(ulong guid, out CharacterPrint character)
        {
            return _byGuid.TryGetValue(guid, out character);
        }

        private void Add(CharacterPrint character)
        {
            Characters.Add(character);
            _byGuid[character.Guid] = character;
        }

        internal static Fingerprint Capture(ulong tick)
        {
            var characters = ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters ?? [];
            var actions = ServiceRepository.GetService<IGameLocationActionService>();
            var fingerprint = new Fingerprint
            {
                Tick = tick,
                Dice = Hash(CaptureRandomState()),
                InBattle = ServiceRepository.GetService<IGameLocationBattleService>() is { IsBattleInProgress: true },
                Quiet = characters.All(x => !x.MoveInProgress && actions?.IsCharacterActing(x) != true)
            };

            foreach (var character in characters.Where(x => x.RulesetCharacter != null).OrderBy(x => x.Guid))
            {
                var ruleset = character.RulesetCharacter;

                fingerprint.Add(new CharacterPrint
                {
                    Guid = character.Guid,
                    HitPoints = ruleset.CurrentHitPoints,
                    TemporaryHitPoints = ruleset.TemporaryHitPoints,
                    Position = character.LocationPosition,
                    Conditions = Hash(ruleset.ConditionsByCategory
                        .OrderBy(x => x.Key)
                        .SelectMany(x => x.Value)
                        .Select(x => $"{x.ConditionDefinition?.Name}:{x.RemainingRounds}")
                        .OrderBy(x => x)
                        .SelectMany(TextValues)),
                    SpellSlots = Hash(SpellSlotEntries(character)
                        .SelectMany(x => new[] { x.repertoire, x.level, x.used }))
                });
            }

            return fingerprint;
        }

        internal object[] ToEvent()
        {
            var values = new int[Characters.Count * ValuesPerCharacter];

            for (var i = 0; i < Characters.Count; i++)
            {
                var c = Characters[i];
                var o = i * ValuesPerCharacter;

                values[o] = c.HitPoints;
                values[o + 1] = c.TemporaryHitPoints;
                values[o + 2] = c.Position.x;
                values[o + 3] = c.Position.y;
                values[o + 4] = c.Position.z;
                values[o + 5] = (int)(c.Conditions >> 32);
                values[o + 6] = (int)c.Conditions;
                values[o + 7] = (int)(c.SpellSlots >> 32);
                values[o + 8] = (int)c.SpellSlots;
            }

            return
            [
                FingerprintKind, (long)Tick, Dice, InBattle, Quiet,
                Characters.Select(x => (long)x.Guid).ToArray(), values
            ];
        }

        internal static Fingerprint FromEvent(object[] data)
        {
            if (data.Length < 7 || data[5] is not long[] guids || data[6] is not int[] values ||
                values.Length != guids.Length * ValuesPerCharacter)
            {
                return null;
            }

            var fingerprint = new Fingerprint
            {
                Tick = (ulong)(long)data[1], Dice = (long)data[2], InBattle = (bool)data[3], Quiet = (bool)data[4]
            };

            for (var i = 0; i < guids.Length; i++)
            {
                var o = i * ValuesPerCharacter;

                fingerprint.Add(new CharacterPrint
                {
                    Guid = (ulong)guids[i],
                    HitPoints = values[o],
                    TemporaryHitPoints = values[o + 1],
                    Position = new int3(values[o + 2], values[o + 3], values[o + 4]),
                    Conditions = ((long)values[o + 5] << 32) | (uint)values[o + 6],
                    SpellSlots = ((long)values[o + 7] << 32) | (uint)values[o + 8]
                });
            }

            return fingerprint;
        }
    }

    #endregion
}
