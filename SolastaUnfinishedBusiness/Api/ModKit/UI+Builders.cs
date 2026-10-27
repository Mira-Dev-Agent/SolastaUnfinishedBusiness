// Copyright < 2021 > Narria (github user Cabarius) - License: MIT

using System;
using System.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using GL = UnityEngine.GUILayout;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal static partial class UI
{
    // ReSharper disable once InconsistentNaming
    private const float UMMWidth = 960f;

    // public static string focusedControlName = null;
    // public static Rect UmmRect = new();
    // public static int UmmTabID = 0;
    private static bool IsNarrow => UMMWidth < 1200;
    private static bool IsWide => UMMWidth >= 1920;

//    public static Vector2[] ummScrollPosition;

    /*** UI Builders
     *
     * This is a simple UI framework that simulates the style of SwiftUI.
     *
     * Usage - these are intended to be called from any OnGUI render path used in your mod
     *
     * Elements will be defined like this
            UI.Section("Cheap Tricks", () =>
            {
                UI.HStack("Combat", 4,
                    () => { UI.ActionButton("Rest All", () => { CheatsCombat.RestAll(); }); },
                    () => { UI.ActionButton("Empowered", () => { CheatsCombat.Empowered(""); }); },
                    () => { UI.ActionButton("Full Buff Please", () => { CheatsCombat.RestAll(); }); },
                    () => { UI.ActionButton("Remove Death's Door", () => { CheatsCombat.Empowered(""); }); },
                    () => { UI.ActionButton("Kill All Enemies", () => { CheatsCombat.KillAll(); }); },
                    () => { UI.ActionButton("Summon Zoo", () => { CheatsCombat.SpawnInspectedEnemiesUnderCursor(""); }); }
                 );
                UI.Space(10);
                UI.HStack("Common", 4,
                    () => { UI.ActionButton("Change Weather", () => { CheatsCommon.ChangeWeather(""); }); },
                    () => { UI.ActionButton("Set Perception to 40", () => { CheatsCommon.StatPerception(); }); }
                 );
                UI.Space(10);
                UI.HStack("Unlocks", 4,
                    () => { UI.ActionButton("Give All Items", () => { CheatsUnlock.CreateAllItems(""); }); }
                 );
            });
    */

    private static void If(bool value, params Action[] actions)
    {
        if (!value)
        {
            return;
        }

        foreach (var action in actions)
        {
            action();
        }
    }

    public static void HStack(string title = null, int stride = 0, params Action[] actions)
    {
        var length = actions.Length;
        if (stride < 1) { stride = length; }

        if (IsNarrow)
        {
            stride = Math.Min(3, stride);
        }

        for (var ii = 0; ii < actions.Length; ii += stride)
        {
            var hasTitle = title != null;

            BeginHorizontal();

            if (hasTitle)
            {
                if (ii == 0)
                {
                    Label(title.Bold(), Width(150f));
                }
                else
                {
                    Space(153f);
                }
            }

            var filteredActions = actions.Skip(ii).Take(stride);

            foreach (var action in filteredActions)
            {
                action();
            }

            EndHorizontal();
        }
    }

    // navigation stays usable while the rest of the menu is read-only (host settings in multiplayer)
    internal static void AlwaysEnabled(Action draw)
    {
        var enabled = GUI.enabled;

        GUI.enabled = true;

        try
        {
            draw();
        }
        finally
        {
            GUI.enabled = enabled;
        }
    }

    public static void TabBar(ref int selected, Action header = null, params NamedAction[] actions)
    {
        if (selected >= actions.Length)
        {
            selected = 0;
        }

        //PATCH: while searching, every searchable tab is drawn at once and only the matching settings show
        if (IsSearching)
        {
            GL.BeginVertical("box");
            header?.Invoke();

            foreach (var action in actions.Where(x => x.Searchable))
            {
                SearchSection(action.Name, action.Action);
            }

            SearchResultsFooter();
            GL.EndVertical();

            return;
        }

        var sel = selected;
        var titles = actions.Select((a, i) =>
            i == sel ? a.Name.Orange().Bold() : a.Name);
        var enumerable = titles as string[] ?? titles.ToArray();
        var newSelected = selected;

        AlwaysEnabled(() =>
            SelectionGrid(ref newSelected, enumerable.ToArray(), enumerable.Length, 6, ExpandWidth(true)));
        selected = newSelected;
        GL.BeginVertical("box");
        header?.Invoke();
        actions[selected].Action();
        GL.EndVertical();
    }

    public static void SubMenu(ref int selected, bool div = true, Action header = null, params NamedAction[] actions)
    {
        if (selected >= actions.Length)
        {
            selected = 0;
        }

        //PATCH: while searching, every searchable sub-page is drawn, labelled with its place in the menu
        if (IsSearching)
        {
            header?.Invoke();

            foreach (var action in actions.Where(x => x.Searchable))
            {
                SearchSection(action.Name, action.Action);
            }

            return;
        }

        var sel = selected;
        var titles = actions.Select((a, i) =>
            i == sel ? a.Name.Orange().Bold() : a.Name);
        var enumerable = titles as string[] ?? titles.ToArray();
        //SelectionGrid(ref selected, enumerable.ToArray(), enumerable.Length, 6, ExpandWidth(false));
        using (HorizontalScope(GUI.skin.scrollView))
        {
            using (VerticalScope())
            {
                var newSelected = selected;

                AlwaysEnabled(() =>
                    SelectionGrid(ref newSelected, enumerable.ToArray(), 0, SubmenuButtonStyle, AutoWidth()));
                selected = newSelected;
                if (div)
                {
                    DivLast(20);
                    Space((float)-25);
                }

                header?.Invoke();
                actions[selected].Action();
            }
        }
    }
}
