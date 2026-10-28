using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CameraModePatcher
{
    [HarmonyPatch(typeof(CameraMode), nameof(CameraMode.ApplyFasterTurn))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyFasterTurn_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            //PATCH: single player enemy-turn speed-up button; the game turns faster time off on every frame the key
            //isn't held, so while the button's speed-up is on it stays on instead
            if (!MultiplayerTurnSpeedContext.KeepsFasterTurn())
            {
                return true;
            }

            var gameTime = ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign?.GameTime;

            if (gameTime != null)
            {
                gameTime.FasterTimeMode = true;
            }

            return false;
        }
    }
}
