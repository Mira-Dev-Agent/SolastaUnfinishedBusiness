using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Wait and long rest as one action. A hero can long rest once per calendar day; when the party already did, the rest
// screen's Long Rest button stays usable (if every other rest rule passes) and waits until the next day before
// resting, through the game's own deferred long rest, the one campfires offer. In multiplayer the wait and the rest
// are approved together.
internal static class LongRestWaitContext
{
    internal const string RestTooOften = "Failure/&FailureFlagCannotRestTooOften";

    // set while checking whether the rest would pass apart from the once-a-day rule
    internal static bool IgnoreRestFrequency { get; private set; }

    // the rest screen's last long rest check passed only because it will wait first
    internal static bool NeedsWait { get; set; }

    // hours the game waits before a deferred long rest (FunctorStartLongRest.DoWaitLongRest)
    internal static int MissingHours()
    {
        var timeInfo = ServiceRepository.GetService<IGameService>().Game.GameCampaign.GameTime.TimeInfo;

        return 24 - timeInfo.Hour + 1;
    }

    // true when a long rest only fails because the party already rested today
    internal static bool OnlyRestedToday(
        GameRestingManager restingManager, string failureFlag, bool shouldCheckPlayerPrivileges)
    {
        if (!Main.Settings.CombineWaitWithLongRest || IgnoreRestFrequency || failureFlag != RestTooOften)
        {
            return false;
        }

        IgnoreRestFrequency = true;

        try
        {
            var failure = string.Empty;

            return restingManager.CanExecuteLocationRest(RestType.LongRest, ref failure, shouldCheckPlayerPrivileges);
        }
        finally
        {
            IgnoreRestFrequency = false;
        }
    }

    // the rest screen's Long Rest when the party already rested today: confirm, then wait and rest
    internal static bool TryWaitThenRest(RestModal restModal)
    {
        var restingService = ServiceRepository.GetService<IGameRestingService>();
        var failure = string.Empty;

        NeedsWait = false;

        if (!Main.Settings.CombineWaitWithLongRest ||
            restingService == null ||
            !restingService.CanExecuteLocationRest(RestType.LongRest, ref failure, true) ||
            !NeedsWait)
        {
            return false;
        }

        var hours = MissingHours();
        var total = (hours * 3600) + MultiplayerApprovalContext.RestSeconds(RestType.LongRest);

        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2, "Message/&LongRestWaitTitle",
            Gui.Format("Message/&LongRestWaitDescription",
                MultiplayerApprovalContext.FormatDuration(hours * 3600),
                MultiplayerApprovalContext.FormatDuration(total)),
            "Message/&MessageYesTitle", "Message/&MessageNoTitle",
            () =>
            {
                // the deferred rest opens the rest screen again once the wait is over, as at a campfire
                if (restModal && restModal.Visible)
                {
                    restModal.Hide();
                }

                ServiceRepository.GetService<ICommandService>()?.StartDeferredLongRest(hours);
            },
            null);

        return true;
    }
}
