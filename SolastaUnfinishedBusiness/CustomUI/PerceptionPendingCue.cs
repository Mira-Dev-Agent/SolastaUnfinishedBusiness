using System.Linq;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

// Multiplayer: a short note beside the cursor while the hovered target's 10-cell Perception roll is on its way to every
// computer, so a hover gets an immediate response before the shared result (and the real advantage or targeting) shows.
// TextMeshPro on a canvas of its own like the FPS overlay: never takes clicks, created on first use.
internal sealed class PerceptionPendingCue : MonoBehaviour
{
    private const float ReferenceWidth = 1920f;
    private const float ReferenceHeight = 1080f;
    private const float FontSize = 15f;
    private const float OffsetX = 22f;
    private const float OffsetY = -30f;
    private const string GameFontName = "SourceSansPro-Regular SDF";

    private static PerceptionPendingCue _instance;

    private Canvas _canvas;
    private TextMeshProUGUI _text;

    internal static void Ensure()
    {
        if (_instance)
        {
            return;
        }

        var gameObject = new GameObject("FbPerceptionPendingCue");

        DontDestroyOnLoad(gameObject);
        _instance = gameObject.AddComponent<PerceptionPendingCue>();
    }

    // after this frame's hover was evaluated (ViewManager.Update)
    private void LateUpdate()
    {
        if (!PerceptionContext.HoverPending)
        {
            if (_canvas && _canvas.enabled)
            {
                _canvas.enabled = false;
            }

            return;
        }

        EnsureCanvas();

        if (!_canvas.enabled)
        {
            // the language may have changed since the last time it showed
            _text.text = Gui.Localize("UI/&PerceptionCheckPending");
            _canvas.enabled = true;
        }

        var scale = _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;

        _text.rectTransform.anchoredPosition =
            new Vector2((Input.mousePosition.x / scale) + OffsetX, (Input.mousePosition.y / scale) + OffsetY);
        // a gentle pulse while it waits
        _text.alpha = 0.7f + (0.3f * Mathf.Sin(Time.unscaledTime * 6f));
    }

    private void EnsureCanvas()
    {
        if (_canvas)
        {
            return;
        }

        var canvasObject = new GameObject("FbPerceptionPendingCueCanvas", typeof(RectTransform));

        canvasObject.transform.SetParent(transform, false);
        _canvas = canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = short.MaxValue;

        var scaler = canvasObject.AddComponent<CanvasScaler>();

        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
        scaler.matchWidthOrHeight = 1f;

        var textObject = new GameObject("FbPerceptionPendingCueText", typeof(RectTransform));

        textObject.transform.SetParent(canvasObject.transform, false);
        _text = textObject.AddComponent<TextMeshProUGUI>();

        var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(x => x.name == GameFontName) ??
                   TMP_Settings.defaultFontAsset;

        if (font)
        {
            _text.font = font;
        }

        _text.fontSize = FontSize;
        _text.color = new Color32(240, 218, 160, 255);
        _text.enableWordWrapping = false;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.alignment = TextAlignmentOptions.TopLeft;
        _text.raycastTarget = false;
        _text.outlineWidth = 0.2f;
        _text.outlineColor = new Color32(0, 0, 0, 220);

        var rect = _text.rectTransform;

        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 0f);
        rect.sizeDelta = new Vector2(400f, 40f);
        _canvas.enabled = false;
    }
}
