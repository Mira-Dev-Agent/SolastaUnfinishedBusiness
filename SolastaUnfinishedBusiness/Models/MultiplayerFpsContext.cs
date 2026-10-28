using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
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
    // rate and ping in aligned columns, in the game's own UI font with an outline so it stays readable over any scene.
    // It's TextMeshPro text on a canvas of its own: each value is one mesh with a built-in outline, kept between
    // frames, instead of IMGUI labels redrawn several times a frame (nine per value for the outline). The table is
    // rebuilt at most once a second; building it every frame had made about 10 KB of garbage per frame and much more
    // frequent full garbage collections, each a visible hitch (evidence/mp-perf-solo).
    private sealed class FpsOverlay : MonoBehaviour
    {
        private const float ReferenceWidth = 1920f;
        private const float ReferenceHeight = 1080f;
        private const float Margin = 14f;
        private const float ColumnGap = 24f;
        private const float FontSize = 15f;
        private const float RebuildSeconds = 1f;
        private const string GameFontName = "SourceSansPro-Regular SDF";

        private readonly List<TextMeshProUGUI> _cells = [];
        private Canvas _canvas;
        private RectTransform _root;
        private TMP_FontAsset _font;
        private float _nextRebuild;

        private void Update()
        {
            var start = MultiplayerPerfContext.Start();

            Refresh();
            MultiplayerPerfContext.Lap(MultiplayerPerfContext.Hook.Drawing, ref start);
        }

        private void Refresh()
        {
            if (!Main.Settings.MultiplayerShowFpsOverlay || !IsOnlineRoom)
            {
                if (_canvas && _canvas.enabled)
                {
                    _canvas.enabled = false;
                }

                _nextRebuild = 0;

                return;
            }

            EnsureCanvas();

            if (!_canvas.enabled)
            {
                _canvas.enabled = true;
            }

            if (Time.unscaledTime < _nextRebuild)
            {
                return;
            }

            _nextRebuild = Time.unscaledTime + RebuildSeconds;
            Rebuild();
        }

        // a screen space canvas above the game's UI, scaled like the game's 1920x1080 layout, that never takes clicks
        private void EnsureCanvas()
        {
            if (_canvas)
            {
                return;
            }

            var canvasObject = new GameObject("FbFpsOverlayCanvas", typeof(RectTransform));

            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = short.MaxValue;

            var scaler = canvasObject.AddComponent<CanvasScaler>();

            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
            scaler.matchWidthOrHeight = 1f;

            _root = (RectTransform)canvasObject.transform;
            _font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(x => x.name == GameFontName) ??
                    TMP_Settings.defaultFontAsset;
        }

        private TextMeshProUGUI Cell(int index)
        {
            while (_cells.Count <= index)
            {
                var cellObject = new GameObject($"FbFpsOverlayCell{_cells.Count}", typeof(RectTransform));

                cellObject.transform.SetParent(_root, false);

                var cell = cellObject.AddComponent<TextMeshProUGUI>();

                if (_font)
                {
                    cell.font = _font;
                }

                cell.fontSize = FontSize;
                cell.color = Color.white;
                cell.richText = true;
                cell.enableWordWrapping = false;
                cell.overflowMode = TextOverflowModes.Overflow;
                cell.alignment = TextAlignmentOptions.TopLeft;
                cell.raycastTarget = false;
                cell.outlineWidth = 0.2f;
                cell.outlineColor = new Color32(0, 0, 0, 220);

                var rect = cell.rectTransform;

                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                _cells.Add(cell);
            }

            return _cells[index];
        }

        private void Rebuild()
        {
            var used = 0;
            var canSeePings = MultiplayerSessionContext.CanSeePings;
            var players = PhotonNetwork.PlayerList.Where(x => canSeePings || CanSee(x)).ToList();

            if (players.Count > 0)
            {
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

                if (rows.Count > 0)
                {
                    used = Layout(rows);
                }
            }

            for (var i = 0; i < _cells.Count; i++)
            {
                var active = i < used;

                if (_cells[i].gameObject.activeSelf != active)
                {
                    _cells[i].gameObject.SetActive(active);
                }
            }
        }

        // places one text per cell in aligned columns and returns how many cells it used
        private int Layout(List<string[]> rows)
        {
            var columns = Enumerable.Range(0, rows[0].Length).Where(c => rows.Any(r => r[c] != null)).ToList();
            var measure = Cell(0);
            var widths = columns.Select(c => rows.Max(r => measure.GetPreferredValues(r[c] ?? string.Empty).x))
                .ToList();
            var rowHeight = measure.GetPreferredValues("Ag").y;
            var used = 0;

            for (var r = 0; r < rows.Count; r++)
            {
                var x = Margin;

                for (var i = 0; i < columns.Count; i++)
                {
                    var cell = Cell(used++);
                    var value = rows[r][columns[i]] ?? string.Empty;

                    if (cell.text != value)
                    {
                        cell.text = value;
                    }

                    cell.rectTransform.anchoredPosition = new Vector2(x, -(Margin + (rowHeight * r)));
                    cell.rectTransform.sizeDelta = new Vector2(widths[i], rowHeight);
                    x += widths[i] + ColumnGap;
                }
            }

            return used;
        }
    }
}
