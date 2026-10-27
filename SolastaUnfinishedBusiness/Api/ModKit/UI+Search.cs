using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using GL = UnityEngine.GUILayout;

namespace SolastaUnfinishedBusiness.Api.ModKit;

// Menu search: while there is search text every searchable page is drawn, and each titled control only appears when
// its text matches, under a "Tab › Section" heading. Collapsed sections are opened while searching and closed again
// once the search is cleared.
internal static partial class UI
{
    private static readonly Regex RichTextTags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly List<string> SearchPath = [];
    private static readonly Dictionary<string, int> SearchOpenedSections = new();
    private static string _pendingSearchHeader;
    private static string _searchInput = string.Empty;
    private static int _searchBypassDepth;

    internal static string SearchText { get; set; } = string.Empty;

    internal static bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    private static int SearchMatchCount { get; set; }

    internal static void SearchBox()
    {
        AlwaysEnabled(() =>
        {
            using (HorizontalScope())
            {
                GL.Label(Gui.Localize("ModUi/&SearchSettings").Bold(), GL.ExpandWidth(false));
                Space(10f);
                _searchInput = GL.TextField(_searchInput ?? string.Empty, GL.Width(350f));
                Space(10f);

                if (!string.IsNullOrWhiteSpace(_searchInput) &&
                    GL.Button(Gui.Localize("ModUi/&SearchClear"), GL.ExpandWidth(false)))
                {
                    _searchInput = string.Empty;
                    GUI.FocusControl(null);
                }
            }
        });

        // IMGUI draws a frame in several passes that must contain the same controls, so the typed text only
        // takes effect at the start of a frame
        if (Event.current.type == EventType.Layout)
        {
            SearchText = _searchInput ?? string.Empty;
        }

        SearchMatchCount = 0;
        _pendingSearchHeader = null;
        SearchPath.Clear();
    }

    internal static void SearchResultsFooter()
    {
        if (IsSearching && SearchMatchCount == 0)
        {
            GL.Label(Gui.Format("ModUi/&SearchNoResults", SearchText.Trim()));
        }
    }

    // draws one tab or sub-page of the search results, labelled with where it lives in the menu
    private static void SearchSection(string name, Action draw)
    {
        SearchPath.Add(RichTextTags.Replace(name ?? string.Empty, string.Empty));
        _pendingSearchHeader = string.Join(" › ", SearchPath);

        try
        {
            draw();
        }
        finally
        {
            SearchPath.RemoveAt(SearchPath.Count - 1);
            _pendingSearchHeader = null;
        }
    }

    private static bool SearchMatches(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return RichTextTags.Replace(text, string.Empty)
            .IndexOf(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // true when a control with this text must not be drawn because it doesn't match the search
    private static bool SearchHides(params string[] texts)
    {
        if (!IsSearching || _searchBypassDepth > 0)
        {
            return false;
        }

        if (!Array.Exists(texts, SearchMatches))
        {
            return true;
        }

        SearchMatchCount++;

        if (_pendingSearchHeader != null)
        {
            Space(10f);
            GL.Label(_pendingSearchHeader.Orange().Bold());
            _pendingSearchHeader = null;
        }

        return false;
    }

    // the parts of a matched control (its label, its reset button, ...) are drawn without being searched again
    private static void WithoutSearch(Action draw)
    {
        _searchBypassDepth++;

        try
        {
            draw();
        }
        finally
        {
            _searchBypassDepth--;
        }
    }

    // a collapsed section is opened while searching (value becomes true and the caller saves it), and closed again
    // the first time it is drawn after the search is cleared
    private static bool SearchOpensOrRestoresSection(string title, ref bool value)
    {
        var key = title ?? string.Empty;

        if (IsSearching)
        {
            if (value || _searchBypassDepth > 0)
            {
                return false;
            }

            SearchOpenedSections[key] = SearchOpenedSections.TryGetValue(key, out var opened) ? opened + 1 : 1;
            value = true;

            return true;
        }

        if (!value || !SearchOpenedSections.TryGetValue(key, out var count))
        {
            return false;
        }

        if (count <= 1)
        {
            SearchOpenedSections.Remove(key);
        }
        else
        {
            SearchOpenedSections[key] = count - 1;
        }

        value = false;

        return true;
    }
}
