using System.Diagnostics;
using System.Linq;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Profiling;

namespace SolastaUnfinishedBusiness.Models;

// TEMPORARY diagnostics (1.7.1): both players saw animations lag more as a multiplayer session went on, and the cause
// isn't measured yet (evidence/multiplayer-issue-01/FINDINGS.md). Every 30 seconds in an online room, the mod log gets
// one "[MpPerf]" line: frame rate and the worst frame, frames over 100 ms, garbage collections and the managed heap, the
// game's network time scale, the action delay (tick buffer) and the longest wait per player, the slowest ping, and the
// time spent in the mod's own per-tick hooks and on-screen drawing. Remove once the cause is known.
internal static class MultiplayerPerfContext
{
    internal enum Hook
    {
        DesyncCheck,
        Fingerprint,
        OtherTick,
        Drawing
    }

    private const float IntervalSeconds = 30f;
    private const float SlowFrameSeconds = 0.1f;

    private static readonly double[] HookMs = new double[4];
    private static readonly int[] HookCalls = new int[4];

    private static GameObject _sampler;
    private static float _nextLogTime;
    private static int _frames;
    private static int _slowFrames;
    private static float _worstFrame;
    private static int _gc0;
    private static int _gc1;
    private static int _gc2;
    private static int _minTickBuffer = int.MaxValue;
    private static int _maxTickBuffer;
    private static float _minTimeScale = float.MaxValue;

    private static bool IsOnlineRoom => PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    internal static void OnJoinedRoom()
    {
        ResetWindow();

        if (_sampler)
        {
            return;
        }

        _sampler = new GameObject("FbPerfSampler");
        Object.DontDestroyOnLoad(_sampler);
        _sampler.AddComponent<FrameSampler>();
    }

    internal static void OnLeftRoom()
    {
        ResetWindow();
    }

    internal static long Start()
    {
        return Stopwatch.GetTimestamp();
    }

    internal static void Lap(Hook hook, ref long start)
    {
        var now = Stopwatch.GetTimestamp();

        HookMs[(int)hook] += (now - start) * 1000.0 / Stopwatch.Frequency;
        HookCalls[(int)hook]++;
        start = now;
    }

    // called on every network tick; samples the action delay and the time scale the game sets to slow a computer down
    internal static void OnTick(NetworkingManager networking)
    {
        if (!IsOnlineRoom || networking?.tick == null)
        {
            return;
        }

        var tickBuffer = networking.tick.currentSendTickBuffer;

        _minTickBuffer = Mathf.Min(_minTickBuffer, tickBuffer);
        _maxTickBuffer = Mathf.Max(_maxTickBuffer, tickBuffer);

        var gameTime = ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign?.GameTime;

        if (gameTime != null)
        {
            _minTimeScale = Mathf.Min(_minTimeScale, gameTime.NetworkTimeScale);
        }
    }

    private static void ResetWindow()
    {
        _nextLogTime = Time.unscaledTime + IntervalSeconds;
        _frames = 0;
        _slowFrames = 0;
        _worstFrame = 0;
        _gc0 = System.GC.CollectionCount(0);
        _gc1 = System.GC.CollectionCount(1);
        _gc2 = System.GC.CollectionCount(2);
        _minTickBuffer = int.MaxValue;
        _maxTickBuffer = 0;
        _minTimeScale = float.MaxValue;

        for (var i = 0; i < HookMs.Length; i++)
        {
            HookMs[i] = 0;
            HookCalls[i] = 0;
        }
    }

    private static void LogWindow()
    {
        var networking = ServiceRepository.GetService<INetworkingService>() as NetworkingManager;
        var tick = networking?.tick;
        var waits = tick?.waitingTimePerActor;
        var pings = tick?.networkPingPerActor;
        var maxWait = waits is { Count: > 0 } ? waits.Values.Max() : 0f;
        var maxPing = pings is { Count: > 0 } ? pings.Values.Max() : 0;

        Main.Info(
            $"[MpPerf] frames={_frames} fps={_frames / IntervalSeconds:0.0} worstFrameMs={_worstFrame * 1000f:0} " +
            $"slowFrames(>100ms)={_slowFrames} gc0={System.GC.CollectionCount(0) - _gc0} " +
            $"gc1={System.GC.CollectionCount(1) - _gc1} gc2={System.GC.CollectionCount(2) - _gc2} " +
            $"heapMB={Profiler.GetMonoUsedSizeLong() / 1048576f:0} " +
            $"tickBuffer={(_maxTickBuffer == 0 ? "-" : $"{_minTickBuffer}-{_maxTickBuffer}")} " +
            $"minNetworkTimeScale={(_minTimeScale == float.MaxValue ? "-" : _minTimeScale.ToString("0.00"))} " +
            $"maxWait={maxWait:0.00} maxPingMs={maxPing} " +
            $"hookMs desync={HookMs[(int)Hook.DesyncCheck]:0.0} fingerprint={HookMs[(int)Hook.Fingerprint]:0.0} " +
            $"otherTick={HookMs[(int)Hook.OtherTick]:0.0} drawing={HookMs[(int)Hook.Drawing]:0.0} " +
            $"ticks={HookCalls[(int)Hook.OtherTick]}");
    }

    // frame times come from Update, which runs once per drawn frame
    private sealed class FrameSampler : MonoBehaviour
    {
        private void Update()
        {
            if (!IsOnlineRoom)
            {
                return;
            }

            var frame = Time.unscaledDeltaTime;

            _frames++;
            _worstFrame = Mathf.Max(_worstFrame, frame);

            if (frame > SlowFrameSeconds)
            {
                _slowFrames++;
            }

            if (Time.unscaledTime < _nextLogTime)
            {
                return;
            }

            LogWindow();
            ResetWindow();
        }
    }
}
