using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Models;

// In a multiplayer battle any player can tap the game's "Play/Pause/Speed Up Battle Turn" key (space by default), or
// click the button shown during enemy turns, to speed up the enemy turns, and do it again to set them back to normal.
// The game itself turns its faster-turn mode off in multiplayer, as one computer playing faster than the others would
// only run ahead and be slowed down again by the lockstep. So the host decides, and every computer switches at the same
// network tick: the game's lockstep runs commands on numbered ticks, not on frames or game time, so playing faster
// doesn't change what happens, only how quickly it plays. Only non-party turns are sped up (the game's own rule in
// single player), and the speed-up ends when a party member's turn starts, so nobody's choosing time is ever rushed.
// In single player the same button can be turned on in the settings: the game only speeds up while the key is held
// (CameraMode.ApplyFasterTurn, every frame), and a click keeps it sped up until clicked again or a party turn starts.
// With "speed up enemy turns by default", every run of enemy turns starts sped up as if the button had been clicked
// (in multiplayer the host's setting decides, through the same tick-scheduled state, with no alert).
internal static class MultiplayerTurnSpeedContext
{
    private const byte EventCode = 173;
    private const byte RequestKind = 1;
    private const byte StateKind = 2;

    // extra ticks on top of the action delay, so the state reaches every computer before the tick it applies on
    private const int ExtraTicks = 4;

    private static bool _eventHandlerRegistered;
    private static bool _speedUp;
    private static bool _wasEnemyTurn;
    private static bool _pending;
    private static bool _pendingOn;
    private static long _pendingTick;
    private static string _pendingNickname;
    private static GameObject _input;

    // the host's game time modifier while sped up, so every computer plays at the same speed
    internal static float Modifier { get; private set; } = 1f;

    private static bool IsOnlineRoom => PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    private static bool IsSinglePlayerButton => !Global.IsMultiplayer && Main.Settings.FasterTimeEnemyTurnButton;

    internal static void Load()
    {
        EnsureInput();
    }

    internal static void OnJoinedRoom()
    {
        Reset();

        if (!_eventHandlerRegistered && PhotonNetwork.NetworkingClient != null)
        {
            PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
            _eventHandlerRegistered = true;
        }

        EnsureInput();
    }

    private static void EnsureInput()
    {
        if (_input)
        {
            return;
        }

        _input = new GameObject("FbTurnSpeedInput");
        Object.DontDestroyOnLoad(_input);
        _input.AddComponent<TurnSpeedInput>();
    }

    // single player: while the button's speed-up is on, it replaces the game's per-frame faster-turn check
    internal static bool KeepsFasterTurn()
    {
        return _speedUp && IsSinglePlayerButton && IsEnemyTurn();
    }

    internal static void OnLeftRoom()
    {
        Reset();
        ApplyToGameTime(false);
    }

    private static void Reset()
    {
        _speedUp = false;
        _wasEnemyTurn = false;
        _pending = false;
        Modifier = 1f;
    }

    // true on the first enemy turn after a party turn (or the first one seen at all), once per run of enemy turns
    private static bool EnemyTurnsBegan()
    {
        var enemyTurn = IsEnemyTurn();
        var began = enemyTurn && !_wasEnemyTurn;

        _wasEnemyTurn = enemyTurn;

        return began;
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

            // a speed-up that arrives after the enemy turns already ended does nothing, so it isn't announced; one the
            // host's default started has no player name and isn't announced either
            if (!_pendingOn || IsEnemyTurn())
            {
                _speedUp = _pendingOn;

                if (!string.IsNullOrEmpty(_pendingNickname))
                {
                    Gui.GuiService?.ShowAlert(
                        Gui.Format(_pendingOn
                            ? "Message/&MultiplayerTurnSpeedUpStarted"
                            : "Message/&MultiplayerTurnSpeedUpStopped", _pendingNickname), Gui.ColorAlmostWhite, 3f);
                }
            }
        }

        if (EnemyTurnsBegan() && PhotonNetwork.IsMasterClient && Main.Settings.EnemyTurnSpeedUpByDefault &&
            Main.Settings.MultiplayerEnemyTurnSpeedUp)
        {
            Decide(true, null);
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

    // a tap of the key or a click of the button: the host decides at once, a client asks the host; in single player
    // only the button, which switches at once
    private static void RequestToggle()
    {
        var wanted = !_speedUp;

        if (!Global.IsMultiplayer)
        {
            _speedUp = wanted && IsEnemyTurn();
            Modifier = Main.Settings.FasterTimeModifier;
            return;
        }

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
    // During enemy turns a button does the same: a copy of the battle screen's own menu button, so it has the game's
    // look, highlight and click sound, with its gamepad binding removed and its click replaced. While sped up, a label
    // below it says so, in the game's font with an outline. Both sit at the top centre of the battle screen, which the
    // game scales from its 1920x1080 layout: the button just under the initiative bar, whose portraits end about 178
    // from the top (the active one's name lowest), and below the alert panel (centred, ending 79 from the top). They
    // show only while the initiative bar does, so they go with it when the game hides the battle UI for a close-up.
    private sealed class TurnSpeedInput : MonoBehaviour
    {
        private const float Top = 186f;
        private const float LabelGap = 2f;
        private const float LabelHeight = 24f;
        private const float LabelFontSize = 17f;
        private const string SourceButtonName = "GameMenuButton";
        private const string GameFontName = "SourceSansPro-Regular SDF";
        private const string StartTerm = "ModUi/&MultiplayerTurnSpeedUpStart";
        private const string StopTerm = "ModUi/&MultiplayerTurnSpeedUpStop";
        private const string PendingTerm = "ModUi/&MultiplayerTurnSpeedUpPending";
        private const string TooltipTerm = "ModUi/&MultiplayerTurnSpeedUpTooltip";

        // space between the text and the button's edges, on top of the button's own layout padding
        private const float TextMargin = 16f;

        private bool _wasActive;
        private GameLocationScreenBattle _screen;
        private GameObject _button;
        private Button _buttonComponent;
        private GuiLabel _buttonLabel;
        private TextMeshProUGUI _buttonText;
        private float _buttonMinWidth;
        private string _buttonTerm;
        private TextMeshProUGUI _indicator;
        private float _indicatorModifier = -1f;

        private void Update()
        {
            var start = MultiplayerPerfContext.Start();

            HandleKey();

            if (!Global.IsMultiplayer && EnemyTurnsBegan() && IsSinglePlayerButton &&
                Main.Settings.EnemyTurnSpeedUpByDefault)
            {
                _speedUp = true;
                Modifier = Main.Settings.FasterTimeModifier;
            }

            // single player has no network tick, so the speed-up ends here when the enemy turns do
            if (_speedUp && !Global.IsMultiplayer && (!IsSinglePlayerButton || !IsEnemyTurn()))
            {
                _speedUp = false;
            }

            RefreshControls();
            MultiplayerPerfContext.Lap(MultiplayerPerfContext.Hook.Drawing, ref start);
        }

        private void HandleKey()
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

        private void RefreshControls()
        {
            var enabled = IsOnlineRoom ? Main.Settings.MultiplayerEnemyTurnSpeedUp : IsSinglePlayerButton;
            // Shown turns false as soon as the bar starts hiding, e.g. in ContextualSequenceStart for a close-up
            var show = enabled && IsEnemyTurn() && EnsureControls() && _screen.initiativeTable &&
                       _screen.initiativeTable.Shown;

            if (!show)
            {
                SetActive(_button, false);
                SetActive(_indicator ? _indicator.gameObject : null, false);

                return;
            }

            SetActive(_button, true);

            var term = _pending ? PendingTerm : _speedUp ? StopTerm : StartTerm;

            if (_buttonTerm != term && _buttonLabel)
            {
                _buttonTerm = term;
                _buttonLabel.Text = term;
                FitButtonToText();
            }

            if (_buttonComponent && _buttonComponent.interactable == _pending)
            {
                _buttonComponent.interactable = !_pending;
            }

            SetActive(_indicator.gameObject, _speedUp);

            if (_speedUp && !Mathf.Approximately(_indicatorModifier, Modifier))
            {
                _indicatorModifier = Modifier;
                _indicator.text = Gui.Format("ModUi/&MultiplayerTurnSpeedUpIndicator", Modifier.ToString("0.#"));
            }
        }

        // the copied button keeps the menu button's fixed 70x30 size, made for "MENU", and its layout group sizes the
        // label to that width, so longer texts wrapped word by word over the box and the label above it. The button
        // is widened to the text on one line instead
        private void FitButtonToText()
        {
            if (!_buttonText)
            {
                return;
            }

            var rect = (RectTransform)_button.transform;
            var layout = _button.GetComponent<HorizontalLayoutGroup>();
            var padding = layout ? layout.padding.horizontal : 0;
            var textWidth = _buttonText
                .GetPreferredValues(_buttonText.text, float.PositiveInfinity, float.PositiveInfinity).x;

            rect.sizeDelta = new Vector2(
                Mathf.Max(_buttonMinWidth, Mathf.Ceil(textWidth) + padding + TextMargin), rect.sizeDelta.y);
        }

        private static void SetActive(GameObject gameObject, bool active)
        {
            if (gameObject && gameObject.activeSelf != active)
            {
                gameObject.SetActive(active);
            }
        }

        // builds the button and label in the current battle screen, once per screen
        private bool EnsureControls()
        {
            var screen = Gui.GuiService?.GetScreen<GameLocationScreenBattle>();

            if (!screen)
            {
                return false;
            }

            if (_screen == screen && _button && _indicator)
            {
                return true;
            }

            var source = screen.transform.Find(SourceButtonName);

            if (!source)
            {
                return false;
            }

            _screen = screen;

            if (_button)
            {
                Destroy(_button);
            }

            if (_indicator)
            {
                Destroy(_indicator.gameObject);
            }

            _button = CopyButton(source.gameObject, screen.transform);
            _buttonComponent = _button.GetComponent<Button>();
            _buttonLabel = _button.GetComponentInChildren<GuiLabel>(true);
            _buttonText = _button.GetComponentInChildren<TextMeshProUGUI>(true);
            _buttonMinWidth = ((RectTransform)_button.transform).sizeDelta.x;
            _buttonTerm = null;

            if (_buttonText)
            {
                _buttonText.enableWordWrapping = false;
            }
            _indicator = CreateIndicator(
                screen.transform, Top + ((RectTransform)_button.transform).sizeDelta.y + LabelGap);
            _indicatorModifier = -1f;

            return true;
        }

        // copied under an inactive holder so none of the copy's components start up before they are changed
        private static GameObject CopyButton(GameObject source, Transform parent)
        {
            var holder = new GameObject("FbTurnSpeedHolder");

            holder.SetActive(false);

            var copy = Instantiate(source, holder.transform, false);

            copy.name = "FbTurnSpeedButton";

            // only the binding goes: its object's Image is what the button's GuiImageHighlighter colours, so destroying
            // the object made every colour change throw; the object stays inactive, as only GuiButtonSetup shows it
            foreach (var binding in copy.GetComponentsInChildren<GamepadBindingButton>(true))
            {
                DestroyImmediate(binding);
            }

            foreach (var setup in copy.GetComponentsInChildren<GuiButtonSetup>(true))
            {
                DestroyImmediate(setup);
            }

            // the menu button's backgrounds carry its "Opens the game menu." tooltip
            foreach (var tooltip in copy.GetComponentsInChildren<GuiTooltip>(true))
            {
                tooltip.Content = Gui.Localize(TooltipTerm);
            }

            var button = copy.GetComponent<Button>();

            if (button)
            {
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(RequestToggle);
            }

            var rect = (RectTransform)copy.transform;

            copy.transform.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -Top);
            copy.SetActive(false);
            Destroy(holder);

            return copy;
        }

        private static TextMeshProUGUI CreateIndicator(Transform parent, float top)
        {
            var labelObject = new GameObject("FbTurnSpeedIndicator", typeof(RectTransform));

            labelObject.transform.SetParent(parent, false);

            var label = labelObject.AddComponent<TextMeshProUGUI>();
            var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(x => x.name == GameFontName);

            if (font)
            {
                label.font = font;
            }

            label.fontSize = LabelFontSize;
            label.color = new Color(0.94f, 0.85f, 0.63f);
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            label.outlineWidth = 0.2f;
            label.outlineColor = new Color32(0, 0, 0, 220);

            var rect = label.rectTransform;

            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -top);
            rect.sizeDelta = new Vector2(400f, LabelHeight);
            labelObject.SetActive(false);

            return label;
        }
    }
}
