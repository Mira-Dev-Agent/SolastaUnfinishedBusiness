using System;
using System.Linq;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

internal static partial class MultiplayerTurnSpeedContext
{
    private const byte PrepareKind = 10, ArrivedKind = 11, ReleaseKind = 12, CancelKind = 13, RejectKind = 14;
    private static CoordinatedTickBarrier _barrier;
    private static int _barrierHost;
    private static long _proposalSequence, _lastProposal;
    private static string _roomName;
    private static float _barrierDeadline, _barrierModifier;
    private static float _nextReject;
    private static bool _arrivalSent, _releaseSent;
    private static GameTime _heldGameTime;
    private static float _previousNetworkScale;

    private static int[] Roster() => PhotonNetwork.PlayerList.Select(x => x.ActorNumber).OrderBy(x => x).ToArray();

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
        Schedule(on, tick, nickname, modifier);
    }

    private static void SendBarrier(object[] data, ReceiverGroup recipients)
    {
        PhotonNetwork.RaiseEvent(EventCode, data, new RaiseEventOptions { Receivers = recipients }, SendOptions.SendReliable);
        PhotonNetwork.SendAllOutgoingCommands();
    }

    private static bool HandleBarrierEvent(EventData message, object[] data)
    {
        if (data[0] is not byte kind || kind < PrepareKind || kind > RejectKind) { return false; }
        if (!IsOnlineRoom || data.Length < 2 || data[1] is not string id || id.Length != 32 ||
            ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking)
        { return true; }
        var host = PhotonNetwork.MasterClient?.ActorNumber ?? 0;
        // A delayed prepare from an earlier map may arrive after the host cancelled/reset it.
        // Reply to that participant so an obsolete local hold can always be released.
        if (kind == RejectKind && PhotonNetwork.IsMasterClient &&
            PhotonNetwork.CurrentRoom.GetPlayer(message.Sender) != null && (_barrier == null || _barrier.Id != id))
        {
            PhotonNetwork.RaiseEvent(EventCode, new object[] { CancelKind, id },
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
            CancelBarrier();
        }
        else if (kind == CancelKind && message.Sender == host && !_barrier.Released)
        {
            ClearBarrier();
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

    private static void CancelBarrier()
    {
        if (_barrier == null) { return; }
        Main.Info("Enemy-turn speed change cancelled before release: " + _barrier.Id);
        SendBarrier(new object[] { CancelKind, _barrier.Id }, ReceiverGroup.Others);
        ClearBarrier();
    }

    private static void ClearBarrier()
    {
        if (_heldGameTime != null) { _heldGameTime.NetworkTimeScale = _previousNetworkScale; }
        _heldGameTime = null;
        _barrier = null;
        _pending = false;
    }

    private static void PumpBarrier(NetworkingManager networking)
    {
        if (_barrier == null) { return; }
        if (!IsOnlineRoom || PhotonNetwork.CurrentRoom.Name != _roomName ||
            PhotonNetwork.MasterClient?.ActorNumber != _barrierHost || !networking.ValidGameplayContext())
        {
            ClearBarrier();
            return;
        }
        if (PhotonNetwork.IsMasterClient && !_releaseSent &&
            (!_barrier.RosterMatches(Roster()) || Time.realtimeSinceStartup >= _barrierDeadline))
        {
            CancelBarrier();
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
            if (PhotonNetwork.IsMasterClient) { CancelBarrier(); }
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
            if (PhotonNetwork.IsMasterClient) { CancelBarrier(); }
            else if (!_arrivalSent)
            {
                _arrivalSent = true;
                SendBarrier(new object[] { RejectKind, _barrier.Id }, ReceiverGroup.MasterClient);
            }
            return;
        }
        if (time != null)
        {
            if (_heldGameTime == null)
            {
                _heldGameTime = time;
                _previousNetworkScale = time.NetworkTimeScale;
            }
            time.NetworkTimeScale = 0f;
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
            ClearBarrier();
            _lastProposal = 0;
        }
    }
}
