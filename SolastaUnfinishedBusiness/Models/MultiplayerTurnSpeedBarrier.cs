using System;
using System.Linq;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Every computer holds game time at the agreed tick until the host has seen all of the captured roster arrive there,
// then the host releases the change and all of them apply it on that tick. While a hold lasts long enough to notice,
// the host names the players it is still waiting for, and everyone sees them under the speed-up button. A cancelled
// proposal (a timeout, a late or refused prepare, a roster or host change) is reported once, with its reason, and the
// speed stays as it was.
internal static partial class MultiplayerTurnSpeedContext
{
    private const byte PrepareKind = 10, ArrivedKind = 11, ReleaseKind = 12, CancelKind = 13, RejectKind = 14;
    private const byte WaitingKind = 15;

    // a hold shorter than this passes unnoticed, so it isn't shown
    private const float NoticeableWait = 1f;
    private const float WaitingInterval = 2f;

    private static CoordinatedTickBarrier _barrier;
    private static int _barrierHost;
    private static long _proposalSequence, _lastProposal;
    private static string _roomName;
    private static float _barrierDeadline, _barrierModifier;
    private static float _nextReject;
    private static bool _arrivalSent, _releaseSent;
    private static GameTime _heldGameTime;
    private static float _previousNetworkScale;
    private static float _holdStarted, _nextWaiting;
    private static int[] _waitingFor = [];
    private static string _reportedBarrier;

    // the host's default speed-up stops proposing after one of its proposals is cancelled, until the next room, so a
    // player whose game can't take part doesn't hold every run of enemy turns
    private static bool _automaticPaused;

    private static int[] Roster() => PhotonNetwork.PlayerList.Select(x => x.ActorNumber).OrderBy(x => x).ToArray();

    private enum CancelReason : byte
    {
        Timeout = 1,
        RosterChanged = 2,
        Refused = 3,
        Unavailable = 4,
        HostChanged = 5
    }

    private static void BeginBarrier(bool on, long tick, string nickname, float modifier)
    {
        if (_barrier != null) { return; }
        var sequence = ++_proposalSequence;
        var id = Guid.NewGuid().ToString("N");
        var roster = Roster();
        AcceptPrepare(id, sequence, tick, roster, on, nickname, modifier);
        SendBarrier(new object[] { PrepareKind, id, sequence, tick, roster, on, nickname ?? "", modifier },
            ReceiverGroup.Others);
    }

    private static void AcceptPrepare(string id, long sequence, long tick, int[] roster, bool on,
        string nickname, float modifier)
    {
        _barrier = new CoordinatedTickBarrier(id, tick, roster);
        _lastProposal = sequence;
        _barrierHost = PhotonNetwork.MasterClient.ActorNumber;
        _roomName = PhotonNetwork.CurrentRoom.Name;
        _barrierDeadline = Time.realtimeSinceStartup + 30f;
        _nextReject = _barrierDeadline;
        _arrivalSent = _releaseSent = false;
        _holdStarted = 0f;
        _waitingFor = [];
        Schedule(on, tick, nickname, modifier);
    }

    private static void SendBarrier(object[] data, ReceiverGroup recipients)
    {
        PhotonNetwork.RaiseEvent(EventCode, data, new RaiseEventOptions { Receivers = recipients }, SendOptions.SendReliable);
        PhotonNetwork.SendAllOutgoingCommands();
    }

    private static bool HandleBarrierEvent(EventData message, object[] data)
    {
        if (data[0] is not byte kind || kind < PrepareKind || kind > WaitingKind) { return false; }
        if (!IsOnlineRoom || data.Length < 2 || data[1] is not string id || id.Length != 32 ||
            ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking)
        { return true; }
        var host = PhotonNetwork.MasterClient?.ActorNumber ?? 0;
        // A delayed prepare from an earlier map may arrive after the host cancelled/reset it.
        // Reply to that participant so an obsolete local hold can always be released.
        if (kind == RejectKind && PhotonNetwork.IsMasterClient &&
            PhotonNetwork.CurrentRoom.GetPlayer(message.Sender) != null && (_barrier == null || _barrier.Id != id))
        {
            PhotonNetwork.RaiseEvent(EventCode, new object[] { CancelKind, id, (byte)CancelReason.Refused },
                new RaiseEventOptions { TargetActors = new[] { message.Sender } }, SendOptions.SendReliable);
            return true;
        }
        if (kind == PrepareKind && message.Sender == host && data.Length == 8 &&
            data[2] is long sequence && data[3] is long tick && data[4] is int[] roster &&
            data[5] is bool on && data[6] is string nickname && data[7] is float modifier)
        {
            if (sequence <= _lastProposal) { return true; }
            if (_barrier != null || tick <= (long)networking.tick.LocalTick ||
                tick - (long)networking.tick.LocalTick > 1024 || !roster.SequenceEqual(Roster()) ||
                float.IsNaN(modifier) || float.IsInfinity(modifier) || modifier <= 0 || modifier > 100)
            {
                SendBarrier(new object[] { RejectKind, id }, ReceiverGroup.MasterClient);
                return true;
            }
            AcceptPrepare(id, sequence, tick, roster, on, nickname, modifier);
            return true;
        }
        if (_barrier == null || _barrier.Id != id || _barrierHost != host) { return true; }
        if (kind == ArrivedKind && PhotonNetwork.IsMasterClient && data.Length == 3 && data[2] is long arrival)
        {
            _barrier.Arrive(id, message.Sender, arrival);
            PumpBarrier(networking);
        }
        else if (kind == RejectKind && PhotonNetwork.IsMasterClient &&
                 PhotonNetwork.CurrentRoom.GetPlayer(message.Sender) != null && !_releaseSent)
        {
            CancelBarrier(CancelReason.Refused);
        }
        else if (kind == CancelKind && message.Sender == host && !_barrier.Released)
        {
            // older hosts send no reason
            ReportCancelled(data.Length > 2 && data[2] is byte reason ? (CancelReason)reason : CancelReason.Refused);
            ClearBarrier();
        }
        else if (kind == WaitingKind && message.Sender == host && data.Length == 3 && data[2] is int[] missing)
        {
            _waitingFor = missing;
        }
        // Release is an irreversible host decision for the captured roster. A subsequent join/leave
        // must not make just some of those participants discard a decision others already applied.
        else if (kind == ReleaseKind && message.Sender == host &&
                 _barrier.Release(id, (long)networking.tick.LocalTick))
        {
            Modifier = _barrierModifier;
            Update(networking);
            ClearBarrier();
        }
        return true;
    }

    private static void CancelBarrier(CancelReason reason)
    {
        if (_barrier == null) { return; }
        Main.Info($"Enemy-turn speed change cancelled before release ({reason}): " + _barrier.Id);
        SendBarrier(new object[] { CancelKind, _barrier.Id, (byte)reason }, ReceiverGroup.Others);
        ReportCancelled(reason);
        ClearBarrier();
    }

    // once per proposal; the speed stays as it was, which the alert says
    private static void ReportCancelled(CancelReason reason)
    {
        if (_barrier == null || _reportedBarrier == _barrier.Id) { return; }
        _reportedBarrier = _barrier.Id;

        var automatic = string.IsNullOrEmpty(_pendingNickname);

        if (automatic && PhotonNetwork.IsMasterClient)
        {
            _automaticPaused = true;
        }

        Gui.GuiService?.ShowAlert(
            Gui.Format(automatic
                    ? "Message/&MultiplayerTurnSpeedUpAutomaticCancelled"
                    : "Message/&MultiplayerTurnSpeedUpCancelled",
                Gui.Localize($"Message/&MultiplayerTurnSpeedUpCancel{reason}")), Gui.ColorFailure, 6f);
    }

    private static void ClearBarrier()
    {
        if (_heldGameTime != null) { _heldGameTime.NetworkTimeScale = _previousNetworkScale; }
        _heldGameTime = null;
        _barrier = null;
        _pending = false;
        _holdStarted = 0f;
        _waitingFor = [];
    }

    // the line under the speed-up button while a change waits at its tick long enough to notice, or null. The game's
    // own lockstep wait shows its own message, so this one steps aside while that is on
    internal static string BarrierStatus()
    {
        if (_barrier == null || _holdStarted <= 0f ||
            Time.realtimeSinceStartup - _holdStarted < NoticeableWait ||
            ServiceRepository.GetService<INetworkingService>() is NetworkingManager { tick.WaitingForPlayer: true })
        {
            return null;
        }

        var names = _waitingFor
            .Where(x => x != PhotonNetwork.LocalPlayer?.ActorNumber)
            .Select(x => PhotonNetwork.CurrentRoom?.GetPlayer(x)?.NickName)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray();

        return names.Length == 0
            ? Gui.Localize("ModUi/&MultiplayerTurnSpeedUpWaitingOthers")
            : Gui.Format("ModUi/&MultiplayerTurnSpeedUpWaiting", string.Join(", ", names));
    }

    private static void PumpBarrier(NetworkingManager networking)
    {
        if (_barrier == null) { return; }
        if (!IsOnlineRoom || PhotonNetwork.CurrentRoom.Name != _roomName ||
            PhotonNetwork.MasterClient?.ActorNumber != _barrierHost || !networking.ValidGameplayContext())
        {
            // a lost connection or a new host ends it without a release; leaving the room or the map isn't reported
            if (IsOnlineRoom && PhotonNetwork.CurrentRoom.Name == _roomName && networking.ValidGameplayContext() &&
                !_barrier.Released)
            {
                ReportCancelled(CancelReason.HostChanged);
            }

            ClearBarrier();
            return;
        }
        if (PhotonNetwork.IsMasterClient && !_releaseSent &&
            (!_barrier.RosterMatches(Roster()) || Time.realtimeSinceStartup >= _barrierDeadline))
        {
            CancelBarrier(_barrier.RosterMatches(Roster()) ? CancelReason.Timeout : CancelReason.RosterChanged);
            return;
        }
        if (!PhotonNetwork.IsMasterClient && Time.realtimeSinceStartup >= _nextReject)
        {
            _nextReject = Time.realtimeSinceStartup + 5f;
            SendBarrier(new object[] { RejectKind, _barrier.Id }, ReceiverGroup.MasterClient);
        }
        var tick = (long)networking.tick.LocalTick;
        if (!_barrier.Holds(tick)) { return; }
        if (tick != _barrier.Tick)
        {
            if (PhotonNetwork.IsMasterClient) { CancelBarrier(CancelReason.Refused); }
            else if (!_arrivalSent)
            {
                _arrivalSent = true;
                SendBarrier(new object[] { RejectKind, _barrier.Id }, ReceiverGroup.MasterClient);
            }
            return;
        }
        var time = ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign?.GameTime;
        if (time == null)
        {
            if (PhotonNetwork.IsMasterClient) { CancelBarrier(CancelReason.Unavailable); }
            else if (!_arrivalSent)
            {
                _arrivalSent = true;
                SendBarrier(new object[] { RejectKind, _barrier.Id }, ReceiverGroup.MasterClient);
            }
            return;
        }
        if (_heldGameTime == null)
        {
            _heldGameTime = time;
            _previousNetworkScale = time.NetworkTimeScale;
        }
        time.NetworkTimeScale = 0f;
        if (_holdStarted <= 0f)
        {
            _holdStarted = Time.realtimeSinceStartup;
            _nextWaiting = _holdStarted + NoticeableWait;
        }
        if (!_arrivalSent)
        {
            _arrivalSent = true;
            if (PhotonNetwork.IsMasterClient) { _barrier.Arrive(_barrier.Id, PhotonNetwork.LocalPlayer.ActorNumber, tick); }
            else { SendBarrier(new object[] { ArrivedKind, _barrier.Id, tick }, ReceiverGroup.MasterClient); }
        }
        if (PhotonNetwork.IsMasterClient && _barrier.AllArrived && !_releaseSent)
        {
            _releaseSent = true;
            SendBarrier(new object[] { ReleaseKind, _barrier.Id }, ReceiverGroup.All);
            return;
        }
        // the host tells everyone who hasn't arrived yet, once the hold is noticeable and then every few seconds
        if (PhotonNetwork.IsMasterClient && !_releaseSent && Time.realtimeSinceStartup >= _nextWaiting)
        {
            _nextWaiting = Time.realtimeSinceStartup + WaitingInterval;
            _waitingFor = _barrier.Missing();
            SendBarrier(new object[] { WaitingKind, _barrier.Id, _waitingFor }, ReceiverGroup.Others);
        }
    }

    [HarmonyPatch(typeof(NetworkTick), nameof(NetworkTick.UpdateTimeManipulation))]
    private static class BarrierTimePatch
    {
        private static bool Prefix(ref float __result)
        {
            if (ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking) { return true; }
            PumpBarrier(networking);
            if (_barrier == null || !_barrier.Holds((long)networking.tick.LocalTick)) { return true; }
            __result = 0f;
            return false;
        }
    }

    // Update runs with zero time scale. Photon keeps its normal dispatch loop; never block or manually re-enter it.
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.Update))]
    private static class BarrierUpdatePatch
    {
        private static void Postfix(NetworkingManager __instance) => PumpBarrier(__instance);
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.ResetTick))]
    private static class BarrierResetPatch
    {
        private static void Prefix() => ClearBarrier();
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.OnMasterClientSwitched))]
    private static class BarrierHostPatch
    {
        private static void Prefix()
        {
            if (_barrier is { Released: false })
            {
                ReportCancelled(CancelReason.HostChanged);
            }

            ClearBarrier();
            _lastProposal = 0;
        }
    }
}
