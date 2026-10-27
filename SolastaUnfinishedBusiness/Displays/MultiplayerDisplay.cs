using Photon.Pun;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Displays;

internal static class MultiplayerDisplay
{
    // this build, and in a room the build of every player (missing ones say so); differences are red
    private static void DisplayVersions()
    {
        UI.Label(Gui.Format("ModUi/&MultiplayerBuildInfo", MultiplayerSessionContext.BuildVersion));

        if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode)
        {
            UI.Label(Gui.Localize("ModUi/&MultiplayerNoHost"));

            return;
        }

        if (MultiplayerSessionContext.CanSeePings)
        {
            var delay = MultiplayerContext.CurrentTickBuffer;

            UI.Label(Gui.Format("ModUi/&MultiplayerCurrentDelay", delay.ToString(), (delay * 50).ToString()));
        }

        UI.Label(Gui.Localize("ModUi/&MultiplayerPlayersVersions"));

        foreach (var player in PhotonNetwork.PlayerList)
        {
            var version = MultiplayerSessionContext.GetPlayerVersion(player);
            var role = player.IsMasterClient ? Gui.Localize("ModUi/&MultiplayerHostRole") : string.Empty;
            var text = $"{player.NickName}{role}: {MultiplayerSessionContext.DescribeVersion(version)}";

            if (MultiplayerSessionContext.CanSeePings)
            {
                text += $" · {MultiplayerSessionContext.DescribePing(player)}";
            }

            UI.Label(version == MultiplayerSessionContext.BuildVersion
                ? $"    <color=#80C080>{text}</color>"
                : $"    <color=#E06060>{text}</color>");
        }
    }

    internal static void DisplayMultiplayer()
    {
        UI.Label();
        UI.Label(Gui.Localize("ModUi/&MultiplayerDescription"));
        UI.Label();
        DisplayVersions();
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

        if (Main.Settings.MultiplayerEnableDesyncDetection)
        {
            toggle = Main.Settings.MultiplayerEnableStateFingerprint;
            if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerEnableStateFingerprint"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.MultiplayerEnableStateFingerprint = toggle;
            }

            toggle = Main.Settings.MultiplayerQuickCorrectDesync;
            if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerQuickCorrectDesync"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.MultiplayerQuickCorrectDesync = toggle;
            }

            toggle = Main.Settings.MultiplayerOfferHostResync;
            if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerOfferHostResync"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.MultiplayerOfferHostResync = toggle;
            }
        }

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&MultiplayerLagTitle"));
        UI.Label();

        toggle = Main.Settings.MultiplayerFlushCommandsImmediately;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerFlushCommandsImmediately"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.MultiplayerFlushCommandsImmediately = toggle;
        }

        toggle = Main.Settings.MultiplayerPingVisibleToHostOnly;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerPingVisibleToHostOnly"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.MultiplayerPingVisibleToHostOnly = toggle;
        }

        toggle = Main.Settings.MultiplayerEnableTickBufferTuning;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerEnableTickBufferTuning"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.MultiplayerEnableTickBufferTuning = toggle;
        }

        if (Main.Settings.MultiplayerEnableTickBufferTuning)
        {
            toggle = Main.Settings.MultiplayerPingBasedTickBuffer;
            if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerPingBasedTickBuffer"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.MultiplayerPingBasedTickBuffer = toggle;
            }

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
        UI.Label(Gui.Localize("ModUi/&MultiplayerTimeTitle"));
        UI.Label();

        toggle = Main.Settings.MultiplayerRequireApprovalForTimeConsumingActions;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerRequireApprovalForTimeConsumingActions"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.MultiplayerRequireApprovalForTimeConsumingActions = toggle;
        }

        if (Main.Settings.MultiplayerRequireApprovalForTimeConsumingActions)
        {
            toggle = Main.Settings.MultiplayerApprovalSkipActionsOfTenMinutesOrLess;
            if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerApprovalSkipActionsOfTenMinutesOrLess"), ref toggle,
                    UI.AutoWidth()))
            {
                Main.Settings.MultiplayerApprovalSkipActionsOfTenMinutesOrLess = toggle;
            }
        }

        toggle = Main.Settings.MultiplayerAllowPlayersToStartRests;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerAllowPlayersToStartRests"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.MultiplayerAllowPlayersToStartRests = toggle;
        }

        // the same setting is also under Campaigns
        toggle = Main.Settings.AllowShortRestWithPartySpreadOut;
        if (UI.Toggle(Gui.Localize("ModUi/&AllowShortRestWithPartySpreadOut"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AllowShortRestWithPartySpreadOut = toggle;
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

        toggle = Main.Settings.MultiplayerSpreadOverlappedCharacters;
        if (UI.Toggle(Gui.Localize("ModUi/&MultiplayerSpreadOverlappedCharacters"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.MultiplayerSpreadOverlappedCharacters = toggle;
        }

        UI.Label();
    }
}
