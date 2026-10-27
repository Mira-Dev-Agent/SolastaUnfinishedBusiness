using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal static partial class UI
{
    private static readonly GUIContent LabelContent = new();
    private static readonly GUIContent CheckOn = new(Sprites.CheckOnTexture);
    private static readonly GUIContent CheckOff = new(Sprites.CheckOffTexture);
    private static readonly GUIContent DisclosureOn = new(Sprites.ExpandedTexture);
    private static readonly GUIContent DisclosureOff = new(Sprites.CollapsedTexture);
    private static readonly GUIContent DisclosureEmpty = new(string.Empty);

    private static readonly int SButtonHint = "MyGUI.Button".GetHashCode();

    private static GUIContent SetLabelContent(string text)
    {
        LabelContent.text = text;
        LabelContent.image = null;
        LabelContent.tooltip = null;

        return LabelContent;
    }

    private static bool Toggle(
        Rect rect,
        GUIContent label,
        bool value,
        bool isEmpty,
        GUIContent on,
        GUIContent off,
        GUIStyle stateStyle,
        GUIStyle labelStyle)
    {
        var controlID = GUIUtility.GetControlID(SButtonHint, FocusType.Passive, rect);
        var result = false;

        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (Event.current.GetTypeForControl(controlID))
        {
            case EventType.MouseDown:
                if (GUI.enabled && rect.Contains(Event.current.mousePosition))
                {
                    GUIUtility.hotControl = controlID;
                    Event.current.Use();
                }

                break;

            case EventType.MouseDrag:
                if (GUIUtility.hotControl == controlID)
                {
                    Event.current.Use();
                }

                break;

            case EventType.MouseUp:
                if (GUIUtility.hotControl == controlID)
                {
                    GUIUtility.hotControl = 0;

                    if (rect.Contains(Event.current.mousePosition))
                    {
                        result = true;
                        Event.current.Use();
                    }
                }

                break;

            case EventType.KeyDown:
                if (GUIUtility.hotControl == controlID)
                {
                    if (Event.current.keyCode == KeyCode.Escape)
                    {
                        GUIUtility.hotControl = 0;
                        Event.current.Use();
                    }
                }

                break;

            case EventType.Repaint:
            {
                var rightAlign =
                        stateStyle.alignment is TextAnchor.MiddleRight or TextAnchor.UpperRight or TextAnchor.LowerRight
                    ;
                // stateStyle.alignment determines position of state element
                var state = isEmpty ? DisclosureEmpty : value ? on : off;
                var stateSize =
                    stateStyle.CalcSize(value
                        ? on
                        : off); // don't use the empty content to calculate size so titles line up in lists
                var x = rightAlign ? rect.xMax - stateSize.x : rect.x;

                Rect stateRect = new(x, rect.y, stateSize.x, stateSize.y);

                // layout state before or after following alignment
                var labelSize = labelStyle.CalcSize(label);

                x = rightAlign ? stateRect.x - stateSize.x - 5 : stateRect.xMax + 5;

                // a wrapped label fills the rest of the row
                Rect labelRect = labelStyle.wordWrap
                    ? new Rect(x, rect.y, rect.xMax - x, rect.height)
                    : new Rect(x, rect.y, labelSize.x, labelSize.y);

                stateStyle.Draw(stateRect, state, controlID);
                labelStyle.Draw(labelRect, label, controlID);
            }
                break;
        }

        return result;
    }

    // Button Control
    private static bool Toggle(
        GUIContent label,
        bool value,
        GUIContent on,
        GUIContent off,
        GUIStyle stateStyle,
        GUIStyle labelStyle,
        bool isEmpty = false,
        params GUILayoutOption[] options)
    {
        var state = value ? on : off;
        var sStyle = new GUIStyle(stateStyle);
        var lStyle = new GUIStyle(labelStyle) { wordWrap = false };
        var stateSize = sStyle.CalcSize(state);

        lStyle.fixedHeight = stateSize.y - 2;

        var padding = new RectOffset(0, (int)stateSize.x + 5, 0, 0);

        lStyle.padding = padding;

        //PATCH: a label wider than the mod window wraps onto more lines instead of running off screen
        var maxWidth = MaxToggleWidth();

        if (lStyle.CalcSize(label).x > maxWidth)
        {
            // measured with the padding the label is drawn with, so the row gets every line it wraps to
            var labelPadding = labelStyle.padding;

            lStyle.padding = new RectOffset(labelPadding.left, (int)stateSize.x + 5 + labelPadding.right,
                labelPadding.top, labelPadding.bottom);
            lStyle.wordWrap = true;
            lStyle.fixedHeight = 0;
            lStyle.stretchWidth = true;

            var wrappedRect = GUILayoutUtility.GetRect(label, lStyle, GUILayout.ExpandWidth(true),
                GUILayout.MaxWidth(maxWidth), GUILayout.MinHeight(stateSize.y - 2));

            return Toggle(wrappedRect, label, value, isEmpty, on, off, stateStyle,
                new GUIStyle(labelStyle) { wordWrap = true });
        }

        var rect = GUILayoutUtility.GetRect(label, lStyle, options);

        return Toggle(rect, label, value, isEmpty, on, off, stateStyle, labelStyle);
    }

    private static System.Reflection.FieldInfo _windowSizeField;

    // the mod window's width less its margins and scroll bar, or the screen's when the window can't be read
    private static float MaxToggleWidth()
    {
        const float Margins = 80f;

        try
        {
            _windowSizeField ??= typeof(UnityModManagerNet.UnityModManager.UI).GetField("mWindowSize",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            if (_windowSizeField?.GetValue(UnityModManagerNet.UnityModManager.UI.Instance) is Vector2 size &&
                size.x > Margins * 2)
            {
                return size.x - Margins;
            }
        }
        catch
        {
            // the window size is private to the mod manager; fall back to the screen
        }

        return Screen.width - (Margins * 2);
    }

    // Disclosure Toggles
    private static bool DisclosureToggle(string label, bool value, bool isEmpty = false,
        params GUILayoutOption[] options)
    {
        return Toggle(SetLabelContent(label), value, DisclosureOn, DisclosureOff, GUI.skin.box, GUI.skin.label, isEmpty,
            options);
    }

    // CheckBox
    private static bool CheckBox(string label, bool value, bool isEmpty, GUIStyle style,
        params GUILayoutOption[] options)
    {
        return Toggle(SetLabelContent(label), value, CheckOn, CheckOff, GUI.skin.box, style, isEmpty, options);
    }
}
