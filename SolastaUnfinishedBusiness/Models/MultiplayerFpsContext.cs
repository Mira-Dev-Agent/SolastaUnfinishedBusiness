using System.Linq;
using System.Text.RegularExpressions;
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
    // a 3-second average hides short stutters: below 30 some frames already near one 50 ms tick, so red; 30 to 44
    // has little margin, so amber
    private const int AmberBelowFps = 45;
    private const int RedBelowFps = 30;

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

    // "FPS: 58", amber when it has little margin and red when frames get close to a tick
    internal static string DescribeFps(Player player)
    {
        var fps = GetPlayerFps(player);

        if (!fps.HasValue)
        {
            return Gui.Localize("ModUi/&MultiplayerNoFps");
        }

        var text = Gui.Format("ModUi/&MultiplayerFps", fps.Value.ToString());

        return fps.Value < RedBelowFps
            ? $"<color=#E06060>{text}</color>"
            : fps.Value < AmberBelowFps
                ? $"<color=#E0A040>{text}</color>"
                : text;
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

    // a borderless table in the top-left corner with no background: one row per visible player, with the name, frame
    // rate and ping in aligned columns; each cell has a dark 1px outline so it stays readable over any scene
    private sealed class FpsOverlay : MonoBehaviour
    {
        private const float Margin = 14f;
        private const float ColumnGap = 24f;

        private static readonly Regex ColorTags = new("</?color[^>]*>", RegexOptions.Compiled);

        private static readonly Vector2[] OutlineOffsets =
        [
            new(-1, -1), new(0, -1), new(1, -1), new(-1, 0), new(1, 0), new(-1, 1), new(0, 1), new(1, 1)
        ];

        private GUIStyle _cellStyle;
        private GUIStyle _outlineStyle;

        private void OnGUI()
        {
            var start = MultiplayerPerfContext.Start();

            Draw();
            MultiplayerPerfContext.Lap(MultiplayerPerfContext.Hook.Drawing, ref start);
        }

        private void Draw()
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

            _cellStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft, richText = true, fontSize = 14, wordWrap = false,
                padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0)
            };
            _outlineStyle ??= new GUIStyle(_cellStyle) { normal = { textColor = new Color(0f, 0f, 0f, 0.85f) } };

            // a cell is empty where this player may not see that value; a column nobody sees is left out
            var rows = players.Select(x => new[]
            {
                x.NickName, CanSee(x) ? DescribeFps(x) : null,
                canSeePings ? MultiplayerSessionContext.DescribePing(x) : null
            }).ToList();

            // with only your own values there's no name, and each value gets a line of its own
            if (players.Count == 1 && players[0].IsLocal)
            {
                rows = rows[0].Skip(1).Where(x => x != null).Select(x => new[] { x }).ToList();
            }

            var columns = Enumerable.Range(0, rows[0].Length).Where(c => rows.Any(r => r[c] != null)).ToList();
            var widths = columns.Select(c => rows.Max(r => _cellStyle.CalcSize(new GUIContent(r[c] ?? "")).x))
                .ToList();
            var rowHeight = _cellStyle.CalcSize(new GUIContent("Ag")).y;

            for (var r = 0; r < rows.Count; r++)
            {
                var x = Margin;
                var y = Margin + (rowHeight * r);

                for (var i = 0; i < columns.Count; i++)
                {
                    var text = rows[r][columns[i]] ?? "";
                    var cell = new Rect(x, y, widths[i], rowHeight);

                    // the outline drops the colour tags, so amber and red values still get a dark outline
                    var outlineText = ColorTags.Replace(text, "");

                    foreach (var offset in OutlineOffsets)
                    {
                        GUI.Label(new Rect(cell.position + offset, cell.size), outlineText, _outlineStyle);
                    }

                    GUI.Label(cell, text, _cellStyle);
                    x += widths[i] + ColumnGap;
                }
            }
        }
    }
}
