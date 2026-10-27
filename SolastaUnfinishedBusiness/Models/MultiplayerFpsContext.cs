using System.Linq;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace SolastaUnfinishedBusiness.Models;

// Each player's frame rate, measured over a few seconds and shared through the player's Photon properties like the
// ping. The host's setting decides who sees what: only the host sees everyone, the host sees everyone and the others
// their own, everyone sees everyone, or each player only sees their own. Shown in the Multiplayer tab, on player plates and in a small overlay during a session, which also
// shows the ping to those allowed to see it.
internal static class MultiplayerFpsContext
{
    internal enum Visibility
    {
        HostSeesAll = 0,
        AllSeeAll = 1,
        OwnOnly = 2,
        HostSeesAllOthersOwn = 3
    }

    private const string FpsKey = "UbFps";
    private const float MeasureInterval = 3f;
    private const int PublishThreshold = 2;
    // the frame rate at which animations stay in step with the 20 ticks per second (3 frames a tick)
    private const int SteadyFps = 60;

    private static float _measureStartTime = -1f;
    private static int _measureStartFrame;
    private static int _publishedFps = -1;
    private static FpsOverlay _overlay;

    internal static int LocalFps { get; private set; } = -1;

    private static bool IsOnlineRoom => PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    internal static void Reset()
    {
        _measureStartTime = -1f;
        _publishedFps = -1;
        LocalFps = -1;
    }

    // called every network frame: counts the frames rendered over the last few seconds and shares the average
    internal static void MeasureAndPublishIfNeeded()
    {
        if (!IsOnlineRoom)
        {
            return;
        }

        EnsureOverlay();

        var now = Time.realtimeSinceStartup;

        if (_measureStartTime < 0)
        {
            _measureStartTime = now;
            _measureStartFrame = Time.frameCount;

            return;
        }

        if (now - _measureStartTime < MeasureInterval)
        {
            return;
        }

        LocalFps = Mathf.RoundToInt((Time.frameCount - _measureStartFrame) / (now - _measureStartTime));
        _measureStartTime = now;
        _measureStartFrame = Time.frameCount;

        if (_publishedFps >= 0 && Mathf.Abs(LocalFps - _publishedFps) < PublishThreshold)
        {
            return;
        }

        _publishedFps = LocalFps;
        PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { FpsKey, LocalFps } });
    }

    // the host's setting decides, since clients play with the host's settings
    internal static bool CanSee(Player player)
    {
        if (!IsOnlineRoom || player == null)
        {
            return false;
        }

        return (Visibility)Main.Settings.MultiplayerFpsVisibility switch
        {
            Visibility.HostSeesAll => PhotonNetwork.IsMasterClient,
            Visibility.HostSeesAllOthersOwn => PhotonNetwork.IsMasterClient || player.IsLocal,
            Visibility.OwnOnly => player.IsLocal,
            _ => true
        };
    }

    internal static int? GetPlayerFps(Player player)
    {
        if (player == null)
        {
            return null;
        }

        if (player.IsLocal)
        {
            return LocalFps >= 0 ? LocalFps : null;
        }

        return player.CustomProperties.TryGetValue(FpsKey, out var value) && value is int fps ? fps : null;
    }

    // "58 FPS", red below the steady rate
    internal static string DescribeFps(Player player)
    {
        var fps = GetPlayerFps(player);

        if (!fps.HasValue)
        {
            return Gui.Localize("ModUi/&MultiplayerNoFps");
        }

        var text = Gui.Format("ModUi/&MultiplayerFps", fps.Value.ToString());

        return fps.Value < SteadyFps - PublishThreshold ? $"<color=#E06060>{text}</color>" : text;
    }

    private static void EnsureOverlay()
    {
        if (_overlay)
        {
            return;
        }

        var gameObject = new GameObject("UbFpsOverlay");

        Object.DontDestroyOnLoad(gameObject);
        _overlay = gameObject.AddComponent<FpsOverlay>();
    }

    // a small box in the top-left corner with each visible player's frame rate
    private sealed class FpsOverlay : MonoBehaviour
    {
        private GUIStyle _style;

        private void OnGUI()
        {
            if (!Main.Settings.MultiplayerShowFpsOverlay || !IsOnlineRoom)
            {
                return;
            }

            var canSeePings = MultiplayerSessionContext.CanSeePings;
            var players = PhotonNetwork.PlayerList.Where(x => canSeePings || CanSee(x)).ToList();

            if (players.Count == 0)
            {
                return;
            }

            _style ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, richText = true, fontSize = 14 };

            // each line has what this player may see: the frame rate, the ping, or both
            var text = string.Join("\n", players.Select(x =>
                $"{x.NickName}: " + string.Join(" · ", new[]
                {
                    CanSee(x) ? DescribeFps(x) : null,
                    canSeePings ? MultiplayerSessionContext.DescribePing(x) : null
                }.Where(y => y != null))));
            var size = _style.CalcSize(new GUIContent(text));

            GUI.Box(new Rect(8, 8, size.x + 8, size.y + 4), text, _style);
        }
    }
}
