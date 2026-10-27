using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Displays;

internal static class MultiplayerDisplay
{
    internal static void DisplayMultiplayer()
    {
        UI.Label();
        UI.Label(Gui.Localize("ModUi/&MultiplayerDescription"));
        UI.Label();
        UI.Label(Gui.Localize("ModUi/&MultiplayerAlwaysOnFixes"));
        UI.Label();

        UI.Label(Gui.Localize("ModUi/&MultiplayerDesyncTitle"));
        UI.Label();

        var toggle = Main.Settings.MultiplayerDeterministicStandAgainstTheTide;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerDeterministicStandAgainstTheTide"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.MultiplayerDeterministicStandAgainstTheTide = toggle;
        }

        toggle = Main.Settings.MultiplayerEnableDesyncDetection;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerEnableDesyncDetection"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.MultiplayerEnableDesyncDetection = toggle;
        }

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&MultiplayerLagTitle"));
        UI.Label();

        toggle = Main.Settings.MultiplayerEnableTickBufferTuning;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerEnableTickBufferTuning"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.MultiplayerEnableTickBufferTuning = toggle;
        }

        if (Main.Settings.MultiplayerEnableTickBufferTuning)
        {
            var intValue = Main.Settings.MultiplayerInitialTickBuffer;
            if (UI.Slider(Gui.Localize("ModUi/&MultiplayerInitialTickBuffer"), ref intValue,
                    MultiplayerContext.MinTickBuffer, MultiplayerContext.MaxTickBuffer,
                    MultiplayerContext.DefaultInitialTickBuffer, string.Empty, UI.AutoWidth()))
            {
                Main.Settings.MultiplayerInitialTickBuffer = intValue;
            }

            intValue = Main.Settings.MultiplayerMinimumTickBuffer;
            if (UI.Slider(Gui.Localize("ModUi/&MultiplayerMinimumTickBuffer"), ref intValue,
                    MultiplayerContext.MinTickBuffer, MultiplayerContext.MaxTickBuffer,
                    MultiplayerContext.DefaultMinimumTickBuffer, string.Empty, UI.AutoWidth()))
            {
                Main.Settings.MultiplayerMinimumTickBuffer = intValue;
            }

            intValue = Main.Settings.MultiplayerSecondsBeforeLowerTickBuffer;
            if (UI.Slider(Gui.Localize("ModUi/&MultiplayerSecondsBeforeLowerTickBuffer"), ref intValue,
                    1, 10, MultiplayerContext.DefaultSecondsBeforeLowerTickBuffer, string.Empty, UI.AutoWidth()))
            {
                Main.Settings.MultiplayerSecondsBeforeLowerTickBuffer = intValue;
            }
        }

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&MultiplayerMovementTitle"));
        UI.Label();

        toggle = Main.Settings.MultiplayerAllowCharactersToOverlapOutOfCombat;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerAllowCharactersToOverlapOutOfCombat"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.MultiplayerAllowCharactersToOverlapOutOfCombat = toggle;
        }

        toggle = Main.Settings.MultiplayerResolveOverlapsOnBattleStart;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerResolveOverlapsOnBattleStart"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.MultiplayerResolveOverlapsOnBattleStart = toggle;
        }

        toggle = Main.Settings.MultiplayerAllowAlliesToPassThroughInCombat;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerAllowAlliesToPassThroughInCombat"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.MultiplayerAllowAlliesToPassThroughInCombat = toggle;
        }

        UI.Label();
    }
}
