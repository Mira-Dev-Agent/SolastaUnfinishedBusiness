using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Multiplayer approval of actions that pass in-game time (rests, learning or scribing spells, Mark Deity, rituals and
// spells or powers that take a minute or more). The action waits on the computer that asked for it until every other
// player approves, then goes through the lockstep as usual. Nobody is asked while a character is in combat or in a
// cutscene: the asking player is told which character is busy instead. Requests use Photon event code 172.
internal static class MultiplayerApprovalContext
{
    internal enum ApprovalAction : byte
    {
        ShortRest = 1,
        LongRest = 2,
        LearnSpell = 3,
        ScribeScroll = 4,
        MarkDeity = 5,
        CastSpell = 6,
        CastRitual = 7,
        UsePower = 8
    }

    private enum Answer : byte
    {
        Approved = 0,
        Declined = 1,
        InCombat = 2,
        InCutscene = 3
    }

    private const byte EventCode = 172;
    private const byte RequestKind = 1;
    private const byte ReplyKind = 2;
    private const byte CancelKind = 3;
    // GameCampActivityMarkDeity's duration
    internal const int MarkDeitySeconds = 600;
    // actions up to this long can skip approval (rituals, 1 and 10 minute castings, Mark Deity)
    private const int ShortActionSeconds = 600;

    private static bool _eventHandlerRegistered;
    private static int _nextRequestId;
    private static PendingRequest _pending;
    private static ShownRequest _shown;

    // set while an approved action runs again, so the patches let it through
    internal static bool IsRunningApprovedAction { get; private set; }

    private static bool IsOnlineRoom =>
        Global.IsMultiplayer && PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    internal static void Reset()
    {
        _pending = null;
        _shown = null;
    }

    #region Asking

    // true when the action must wait: it then runs through proceed once every other player approves
    internal static bool MustWaitForApproval(
        ApprovalAction action, string characterName, BaseDefinition subject, int seconds, Action proceed)
    {
        if (IsRunningApprovedAction ||
            !Main.Settings.MultiplayerRequireApprovalForTimeConsumingActions ||
            !IsOnlineRoom ||
            seconds <= 0 ||
            (Main.Settings.MultiplayerApprovalSkipActionsOfTenMinutesOrLess && seconds <= ShortActionSeconds) ||
            PhotonNetwork.PlayerListOthers.Length == 0)
        {
            return false;
        }

        RegisterEventHandler();

        var description = DescribeAction(action, characterName, subject?.Name ?? string.Empty);

        if (_pending != null)
        {
            Gui.GuiService.ShowAlert(Gui.Localize("Message/&MultiplayerApprovalAlreadyWaiting"), Gui.ColorFailure,
                5f);

            return true;
        }

        var busy = DescribeLocalBusy(out _);

        if (busy != null)
        {
            ShowBusy(busy);

            return true;
        }

        _pending = new PendingRequest
        {
            Id = ++_nextRequestId,
            Description = description,
            Seconds = seconds,
            Proceed = proceed,
            Waiting = PhotonNetwork.PlayerListOthers.Select(x => x.ActorNumber).ToHashSet()
        };

        PhotonNetwork.RaiseEvent(EventCode,
            new object[]
            {
                RequestKind, _pending.Id, (byte)action, characterName ?? string.Empty, subject?.Name ?? string.Empty,
                seconds
            },
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);

        ShowWaiting();

        return true;
    }

    private static void ShowWaiting()
    {
        var waiting = string.Join(", ", _pending.Waiting
            .Select(x => PhotonNetwork.CurrentRoom?.GetPlayer(x)?.NickName ?? "?"));
        var requestId = _pending.Id;

        _pending.ShowingWaiting = true;
        Gui.GuiService.ShowMessage(MessageModal.Severity.Informative1, "Message/&MultiplayerApprovalWaitingTitle",
            Gui.Format("Message/&MultiplayerApprovalWaitingDescription", _pending.Description,
                FormatDuration(_pending.Seconds), waiting),
            "Message/&MultiplayerApprovalCancelRequest", string.Empty,
            () => CancelPending(requestId), null);
    }

    private static void CancelPending(int requestId)
    {
        if (_pending == null || _pending.Id != requestId)
        {
            return;
        }

        PhotonNetwork.RaiseEvent(EventCode, new object[] { CancelKind, requestId },
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
        _pending = null;
    }

    // ends the request: hides the waiting popup without running its cancel button
    private static void FinishPending()
    {
        var pending = _pending;

        _pending = null;

        if (pending is { ShowingWaiting: true })
        {
            HideMessage();
        }
    }

    private static void OnReply(int requestId, Player sender, Answer answer, string names)
    {
        if (_pending == null || _pending.Id != requestId || sender == null ||
            !_pending.Waiting.Contains(sender.ActorNumber))
        {
            return;
        }

        var description = _pending.Description;

        switch (answer)
        {
            case Answer.Approved:
                _pending.Waiting.Remove(sender.ActorNumber);
                CompleteIfEveryoneApproved();
                break;
            case Answer.Declined:
                CancelPending(requestId);
                HideMessage();
                Gui.GuiService.ShowAlert(
                    Gui.Format("Message/&MultiplayerApprovalDeclined", sender.NickName, description),
                    Gui.ColorFailure, 8f);
                break;
            case Answer.InCombat:
            case Answer.InCutscene:
                CancelPending(requestId);
                HideMessage();
                ShowBusy(Gui.Format(answer == Answer.InCombat
                    ? "Message/&MultiplayerApprovalInCombat"
                    : "Message/&MultiplayerApprovalInCutscene", names));
                break;
        }
    }

    private static void CompleteIfEveryoneApproved()
    {
        if (_pending == null)
        {
            return;
        }

        // the waiting popup lists who is left
        if (_pending.Waiting.Count > 0)
        {
            ShowWaiting();

            return;
        }

        var pending = _pending;

        FinishPending();
        Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerApprovalApproved", pending.Description),
            Gui.ColorBrightBlue, 5f);

        IsRunningApprovedAction = true;

        try
        {
            pending.Proceed();
        }
        finally
        {
            IsRunningApprovedAction = false;
        }
    }

    // called every network frame: forgets players who left, and stops if a fight or cutscene starts meanwhile
    internal static void Update()
    {
        if (_pending != null)
        {
            if (!IsOnlineRoom)
            {
                _pending = null;

                return;
            }

            var busy = DescribeLocalBusy(out _);

            if (busy != null)
            {
                CancelPending(_pending.Id);
                HideMessage();
                ShowBusy(busy);

                return;
            }

            var inRoom = PhotonNetwork.PlayerListOthers.Select(x => x.ActorNumber).ToHashSet();

            if (_pending.Waiting.RemoveWhere(x => !inRoom.Contains(x)) > 0)
            {
                CompleteIfEveryoneApproved();
            }
        }

        // a request on screen is withdrawn here too if this computer gets busy
        if (_shown != null && DescribeLocalBusy(out var answer) != null)
        {
            Reply(_shown, answer);
            _shown = null;
            HideMessage();
        }
    }

    private static void ShowBusy(string reason)
    {
        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2, "Message/&MultiplayerApprovalBusyTitle",
            Gui.Format("Message/&MultiplayerApprovalBusyDescription", reason),
            "Message/&MessageOkTitle", string.Empty, null, null);
    }

    #endregion

    #region Answering

    private static void OnRequest(object[] data, Player sender)
    {
        if (sender == null || data.Length < 6)
        {
            return;
        }

        var request = new ShownRequest { Id = (int)data[1], SenderActor = sender.ActorNumber };
        var description = DescribeAction((ApprovalAction)(byte)data[2], (string)data[3], (string)data[4]);
        var seconds = (int)data[5];

        // only one request is shown at a time; an older one still on screen is declined
        if (_shown != null)
        {
            Reply(_shown, Answer.Declined);
            _shown = null;
        }

        if (DescribeLocalBusy(out var busy) != null)
        {
            Reply(request, busy);

            return;
        }

        _shown = request;
        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2, "Message/&MultiplayerApprovalRequestTitle",
            Gui.Format("Message/&MultiplayerApprovalRequestDescription", sender.NickName, description,
                FormatDuration(seconds)),
            "Message/&MultiplayerApprovalApprove", "Message/&MultiplayerApprovalDecline",
            () => Answered(request, Answer.Approved),
            () => Answered(request, Answer.Declined));
    }

    private static void Answered(ShownRequest request, Answer answer)
    {
        if (_shown != request)
        {
            return;
        }

        _shown = null;
        Reply(request, answer);
    }

    private static void Reply(ShownRequest request, Answer answer)
    {
        var names = answer is Answer.InCombat or Answer.InCutscene ? DescribeLocalBusyNames(answer) : string.Empty;

        PhotonNetwork.RaiseEvent(EventCode, new object[] { ReplyKind, request.Id, (byte)answer, names },
            new RaiseEventOptions { TargetActors = [request.SenderActor] }, SendOptions.SendReliable);
    }

    private static void OnCancel(int requestId, Player sender)
    {
        if (_shown == null || sender == null || _shown.Id != requestId || _shown.SenderActor != sender.ActorNumber)
        {
            return;
        }

        _shown = null;
        HideMessage();
        Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerApprovalCancelled", sender.NickName),
            Gui.ColorAlert, 5f);
    }

    #endregion

    #region Busy

    // the reason this computer can't be asked right now (a fight or a cutscene), or null
    private static string DescribeLocalBusy(out Answer answer)
    {
        if (Gui.Battle != null)
        {
            answer = Answer.InCombat;

            return Gui.Format("Message/&MultiplayerApprovalInCombat", DescribeLocalBusyNames(answer));
        }

        if (ServiceRepository.GetService<INarrativeDirectionService>() is { IsSequenceInProgress: true })
        {
            answer = Answer.InCutscene;

            return Gui.Format("Message/&MultiplayerApprovalInCutscene", DescribeLocalBusyNames(answer));
        }

        answer = Answer.Approved;

        return null;
    }

    // this player's characters taking part, or every party character when none of them is
    private static string DescribeLocalBusyNames(Answer answer)
    {
        var party = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters ?? [];
        var involved = answer == Answer.InCombat && Gui.Battle != null
            ? party.Where(x => Gui.Battle.PlayerContenders.Contains(x)).ToList()
            : party.ToList();
        var mine = Gui.ActivePlayerController?.ControlledCharacters ?? [];
        var names = involved.Where(x => mine.Contains(x)).ToList();

        if (names.Count == 0)
        {
            names = involved;
        }

        return names.Count == 0
            ? PhotonNetwork.LocalPlayer?.NickName ?? "?"
            : string.Join(", ", names.Select(x => x.Name));
    }

    #endregion

    #region Text

    private static string DescribeAction(ApprovalAction action, string characterName, string subjectName)
    {
        var subject = subjectName;

        if (DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(subjectName, out var spell))
        {
            subject = spell.FormatTitle();
        }
        else if (DatabaseRepository.GetDatabase<FeatureDefinitionPower>().TryGetElement(subjectName, out var power))
        {
            subject = power.FormatTitle();
        }

        return Gui.Format($"Message/&MultiplayerApprovalAction{action}", characterName, subject);
    }

    private static string FormatDuration(int seconds)
    {
        var hours = seconds / 3600;
        var minutes = (seconds % 3600 + 59) / 60;
        var parts = new List<string>();

        if (hours > 0)
        {
            parts.Add(Gui.Format(hours == 1 ? "Message/&MultiplayerApprovalHour" : "Message/&MultiplayerApprovalHours",
                hours.ToString()));
        }

        if (minutes > 0 || hours == 0)
        {
            parts.Add(Gui.Format(
                minutes == 1 ? "Message/&MultiplayerApprovalMinute" : "Message/&MultiplayerApprovalMinutes",
                minutes.ToString()));
        }

        return string.Join(" ", parts);
    }

    #endregion

    #region Durations

    internal static int RestSeconds(RestType restType)
    {
        return RestDurationInSeconds.TryGetValue(restType, out var seconds) ? seconds : 0;
    }

    // in-game time an action passes, as the game counts it when the action runs (0 for none)
    internal static int ActionSeconds(CharacterActionParams actionParams, out ApprovalAction action,
        out BaseDefinition subject)
    {
        action = ApprovalAction.CastSpell;
        subject = null;

        int seconds;

        switch (actionParams?.RulesetEffect)
        {
            case RulesetEffectSpell spell:
                subject = spell.SpellDefinition;

                if (spell.SlotLevel < 0 && spell.SpellRepertoire == null && spell.SpellDefinition.Ritual)
                {
                    action = ApprovalAction.CastRitual;
                    seconds = ComputeActivationTimeSeconds(spell.SpellDefinition.RitualCastingTime);
                }
                else
                {
                    seconds = ComputeActivationTimeSeconds(spell.SpellDefinition.ActivationTime);
                }

                break;
            case RulesetEffectPower power:
                action = ApprovalAction.UsePower;
                subject = power.PowerDefinition;
                seconds = ComputeActivationTimeSeconds(power.PowerDefinition.ActivationTime);
                break;
            default:
                return 0;
        }

        // a feast (Heroes' Feast) passes its hours after the casting
        seconds += actionParams.RulesetEffect.EffectDescription.EffectForms
            .Where(x => x.FormType == EffectForm.EffectFormType.Alteration &&
                        x.AlterationForm.AlterationType == AlterationForm.Type.Feast)
            .Sum(x => x.AlterationForm.FeastDurationHours * 3600);

        return seconds;
    }

    #endregion

    #region Network

    private static void RegisterEventHandler()
    {
        if (_eventHandlerRegistered || PhotonNetwork.NetworkingClient == null)
        {
            return;
        }

        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _eventHandlerRegistered = true;
    }

    // clients register as soon as they join, so a request isn't missed before they ask for anything themselves
    internal static void OnJoinedRoom()
    {
        Reset();
        RegisterEventHandler();
    }

    private static void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != EventCode || photonEvent.CustomData is not object[] { Length: > 1 } data)
        {
            return;
        }

        var sender = PhotonNetwork.CurrentRoom?.GetPlayer(photonEvent.Sender);

        switch ((byte)data[0])
        {
            case RequestKind:
                OnRequest(data, sender);
                break;
            case ReplyKind when data.Length >= 4:
                OnReply((int)data[1], sender, (Answer)(byte)data[2], (string)data[3]);
                break;
            case CancelKind:
                OnCancel((int)data[1], sender);
                break;
        }
    }

    private static void HideMessage()
    {
        var messageModal = Gui.GuiService.GetScreen<MessageModal>();

        if (messageModal && messageModal.Visible)
        {
            messageModal.Hide();
        }
    }

    #endregion

    private sealed class PendingRequest
    {
        internal int Id;
        internal string Description;
        internal int Seconds;
        internal Action Proceed;
        internal HashSet<int> Waiting;
        internal bool ShowingWaiting;
    }

    private sealed class ShownRequest
    {
        internal int Id;
        internal int SenderActor;
    }
}
