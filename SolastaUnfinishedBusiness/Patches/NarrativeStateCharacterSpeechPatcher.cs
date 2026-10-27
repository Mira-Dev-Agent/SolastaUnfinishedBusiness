using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NarrativeStateCharacterSpeechPatcher
{
    [HarmonyPatch(typeof(NarrativeStateCharacterSpeech), nameof(NarrativeStateCharacterSpeech.Begin))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Begin_Patch
    {
        [UsedImplicitly]
        public static void Prefix(NarrativeStateCharacterSpeech __instance)
        {
            //PATCH: never let one hero say two consecutive lines of different roles (MULTIPLAYER)
            MultiplayerContext.AvoidConsecutiveLinesBySameHero(__instance);
        }
    }

    [HarmonyPatch(typeof(NarrativeStateBaseSpeech), nameof(NarrativeStateBaseSpeech.Begin))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BaseSpeechBegin_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NarrativeStateBaseSpeech __instance)
        {
            //PATCH: remembers who spoke last, heroes and npcs alike (MULTIPLAYER)
            MultiplayerContext.RecordSpeaker(__instance);
        }
    }

    [HarmonyPatch(typeof(NarrativeStateCharacterSpeech), nameof(NarrativeStateCharacterSpeech.RecordSpeechLine))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RecordSpeechLine_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NarrativeStateCharacterSpeech __instance, string speakerName, string textLine)
        {
            //PATCH: supports speech feature
            SpeechContext.Speak(textLine, __instance.speaker);

            //PATCH: EnableLogDialoguesToConsole
            if (Main.Settings.EnableLogDialoguesToConsole)
            {
                GameConsoleHelper.LogCharacterConversationLine(speakerName, textLine, false);
            }
        }
    }
}
