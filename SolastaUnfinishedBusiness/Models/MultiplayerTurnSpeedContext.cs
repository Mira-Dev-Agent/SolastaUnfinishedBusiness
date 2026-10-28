using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// In a multiplayer battle any player can tap the game's "Play/Pause/Speed Up Battle Turn" key (space by default), or
// click the button shown during enemy turns, to speed up the enemy turns, and do it again to set them back to normal.
// The game itself turns its faster-turn mode off in multiplayer, as one computer playing faster than the others would
// only run ahead and be slowed down again by the lockstep. So the host decides, and every computer switches at the same
// network tick: the game's lockstep runs commands on numbered ticks, not on frames or game time, so playing faster
// doesn't change what happens, only how quickly it plays. Only non-party turns are sped up (the game's own rule in
// single player), and the speed-up ends when a party member's turn starts, so nobody's choosing time is ever rushed.
internal static class MultiplayerTurnSpeedContext
{
    private const byte EventCode = 173;
    private const byte RequestKind = 1;
    private const byte StateKind = 2;

    // extra ticks on top of the action delay, so the state reaches every computer before the tick it applies on
    private const int ExtraTicks = 4;

    private static bool _eventHandlerRegistered;
    private static bool _speedUp;
    private static bool _pending;
    private static bool _pendingOn;
    private static long _pendingTick;
    private static string _pendingNickname;
    private static GameObject _input;

    // the host's game time modifier while sped up, so every computer plays at the same speed
    internal static float Modifier { get; private set; } = 1f;

    private static bool IsOnlineRoom => PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    internal static void OnJoinedRoom()
    {
        Reset();

        if (!_eventHandlerRegistered && PhotonNetwork.NetworkingClient != null)
        {
            PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
            _eventHandlerRegistered = true;
        }

        if (!_input)
        {
            _input = new GameObject("FbTurnSpeedInput");
            Object.DontDestroyOnLoad(_input);
            _input.AddComponent<TurnSpeedInput>();
        }
    }

    internal static void OnLeftRoom()
    {
        Reset();
        ApplyToGameTime(false);
    }

    private static void Reset()
    {
        _speedUp = false;
        _pending = false;
        Modifier = 1f;
    }

    // a non-party character is acting in a battle that has started: the game's own condition for a faster turn
    internal static bool IsEnemyTurn()
    {
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService is not { IsBattleInProgress: true, HasBattleStarted: true } ||
            battleService.Battle?.ActiveContender is not { } contender)
        {
            return false;
        }

        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        return characterService != null && !characterService.PartyHasCharacter(contender, true, true);
    }

    // called on every network tick (NetworkingManager.FixedUpdate)
    internal static void Update(NetworkingManager networking)
    {
        if (!IsOnlineRoom || !networking.IsMultiplayerGame)
        {
            return;
        }

        if (_pending && (long)networking.tick.LocalTick >= _pendingTick)
        {
            _pending = false;

            // a speed-up that arrives after the enemy turns already ended does nothing, so it isn't announced
            if (!_pendingOn || IsEnemyTurn())
            {
                _speedUp = _pendingOn;
                Gui.GuiService?.ShowAlert(
                    Gui.Format(_pendingOn
                        ? "Message/&MultiplayerTurnSpeedUpStarted"
                        : "Message/&MultiplayerTurnSpeedUpStopped", _pendingNickname), Gui.ColorAlmostWhite, 3f);
            }
        }

        // turn changes run on the same tick everywhere, so every computer stops at the same tick
        if (_speedUp && !IsEnemyTurn())
        {
            _speedUp = false;
        }

        ApplyToGameTime(_speedUp && Main.Settings.MultiplayerEnemyTurnSpeedUp);
    }

    private static void ApplyToGameTime(bool faster)
    {
        var gameTime = ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign?.GameTime;

        if (gameTime != null)
        {
            // the setter only rescales time when the value changes
            gameTime.FasterTimeMode = faster;
        }
    }

    // a tap of the key or a click of the button: the host decides at once, a client asks the host
    private static void RequestToggle()
    {
        var wanted = !_speedUp;

        if (PhotonNetwork.IsMasterClient)
        {
            Decide(wanted, PhotonNetwork.LocalPlayer?.NickName);
            return;
        }

        PhotonNetwork.RaiseEvent(EventCode, new object[] { RequestKind, wanted },
            new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
    }

    // host only: picks the tick and tells every computer, itself included
    private static void Decide(bool on, string nickname)
    {
        // one change at a time, so two quick taps can't cancel each other out on the way
        if (_pending || (on && !IsEnemyTurn()) || on == _speedUp ||
            ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking)
        {
            return;
        }

        var tick = (long)networking.tick.LocalTick + MultiplayerContext.CurrentTickBuffer + ExtraTicks;
        var modifier = Main.Settings.FasterTimeModifier;

        PhotonNetwork.RaiseEvent(EventCode, new object[] { StateKind, on, tick, nickname ?? string.Empty, modifier },
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
        Schedule(on, tick, nickname, modifier);
    }

    private static void Schedule(bool on, long tick, string nickname, float modifier)
    {
        _pending = true;
        _pendingOn = on;
        _pendingTick = tick;
        _pendingNickname = nickname;
        Modifier = modifier;
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
            case RequestKind when PhotonNetwork.IsMasterClient:
                Decide((bool)data[1], sender?.NickName);
                break;
            // only the host's state counts
            case StateKind when data.Length >= 5 && sender is { IsMasterClient: true }:
                Schedule((bool)data[1], (long)data[2], (string)data[3], (float)data[4]);
                break;
        }
    }

    // watches the key the game uses for its single-player speed-up, which it ignores in multiplayer; a tap toggles.
    // During enemy turns a button does the same, and while sped up a label above it says so, outlined like the FPS
    // overlay so it reads over any scene. Both sit at the top centre in the game's 1920x1080 reference layout, scaled
    // like the game's own UI: below the initiative bar's area (200 high at the top) and below the alert panel (centred,
    // ending 79 from the top), so neither covers the other.
    private sealed class TurnSpeedInput : MonoBehaviour
    {
        private const float ReferenceHeight = 1080f;
        private const float Top = 206f;
        private const float LabelHeight = 24f;
        private const float ButtonWidth = 260f;
        private const float ButtonHeight = 32f;
        private const int LabelFontSize = 16;
        private const int ButtonFontSize = 15;

        private static readonly Vector2[] OutlineOffsets =
        [
            new(-1, -1), new(0, -1), new(1, -1), new(-1, 0), new(1, 0), new(-1, 1), new(0, 1), new(1, 1)
        ];

        private bool _wasActive;
        private GUIStyle _buttonStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _outlineStyle;

        private void OnGUI()
        {
            var start = MultiplayerPerfContext.Start();

            Draw();
            MultiplayerPerfContext.Lap(MultiplayerPerfContext.Hook.Drawing, ref start);
        }

        private void Draw()
        {
            if (!IsOnlineRoom || !Main.Settings.MultiplayerEnemyTurnSpeedUp || !IsEnemyTurn())
            {
                return;
            }

            var scale = Screen.height / ReferenceHeight;

            _buttonStyle ??= new GUIStyle(GUI.skin.button);
            _buttonStyle.fontSize = Mathf.RoundToInt(ButtonFontSize * scale);

            var label = Gui.Localize(_pending
                ? "ModUi/&MultiplayerTurnSpeedUpPending"
                : _speedUp
                    ? "ModUi/&MultiplayerTurnSpeedUpStop"
                    : "ModUi/&MultiplayerTurnSpeedUpStart");
            var top = Top * scale;
            var buttonRect = new Rect((Screen.width - (ButtonWidth * scale)) / 2f, top + (LabelHeight * scale),
                ButtonWidth * scale, ButtonHeight * scale);

            GUI.enabled = !_pending;

            if (GUI.Button(buttonRect, label, _buttonStyle))
            {
                RequestToggle();
            }

            GUI.enabled = true;

            if (!_speedUp)
            {
                return;
            }

            _labelStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter, wordWrap = false,
                normal = { textColor = new Color(0.94f, 0.85f, 0.63f) }
            };
            _outlineStyle ??= new GUIStyle(_labelStyle) { normal = { textColor = new Color(0f, 0f, 0f, 0.85f) } };
            _labelStyle.fontSize = _outlineStyle.fontSize = Mathf.RoundToInt(LabelFontSize * scale);

            var text = Gui.Format("ModUi/&MultiplayerTurnSpeedUpIndicator", Modifier.ToString("0.#"));
            var size = _labelStyle.CalcSize(new GUIContent(text));
            var rect = new Rect((Screen.width - size.x) / 2f, top + (((LabelHeight * scale) - size.y) / 2f), size.x,
                size.y);

            foreach (var offset in OutlineOffsets)
            {
                GUI.Label(new Rect(rect.position + offset, rect.size), text, _outlineStyle);
            }

            GUI.Label(rect, text, _labelStyle);
        }

        private void Update()
        {
            var active = IsOnlineRoom &&
                         ServiceRepository.GetService<IInputService>()?.IsCommandActive(InputCommands.Id.PlayPause) ==
                         true;

            if (active && !_wasActive && !_pending && Main.Settings.MultiplayerEnemyTurnSpeedUp && IsEnemyTurn())
            {
                RequestToggle();
            }

            _wasActive = active;
        }
    }
}
