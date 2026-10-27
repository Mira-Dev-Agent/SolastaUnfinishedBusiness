using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Works out why the game logged an exception so the instability warning can name the cause, and moves character
// files the game cannot load (e.g. heroes made with removed mod classes or races) out of the characters folder.
internal static class ErrorDiagnosticsContext
{
    private const int RecentLinesCapacity = 64;
    internal const string UnloadableFolderName = "Unloadable";

    private static readonly Regex MissingElementRegex =
        new(@"Cannot find element (\S+) in database (\S+)", RegexOptions.Compiled);

    private static readonly Regex CharacterLoadFailureRegex =
        new(@"Exception when loading (.+?\.chr)\.", RegexOptions.Compiled);

    private static readonly List<string> RecentMissingElements = [];
    private static readonly List<UnloadableCharacter> UnloadableCharacters = [];
    private static readonly Queue<string> RecentErrors = new();
    private static readonly List<string> MovedCharacters = [];
    private static readonly List<string> MovedCharacterFiles = [];
    private static bool _listening;

    private static string CharactersDirectory => TacticalAdventuresApplication.GameCharactersDirectory;

    internal static void StartListening()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        Application.logMessageReceived += OnLogMessageReceived;
    }

    private static void OnLogMessageReceived(string logString, string stackTrace, LogType type)
    {
        if (type is not (LogType.Error or LogType.Exception) || string.IsNullOrEmpty(logString))
        {
            return;
        }

        RecentErrors.Enqueue(logString);

        while (RecentErrors.Count > RecentLinesCapacity)
        {
            RecentErrors.Dequeue();
        }

        var missing = MissingElementRegex.Match(logString);

        if (missing.Success)
        {
            RecentMissingElements.Add($"{missing.Groups[1].Value} ({missing.Groups[2].Value})");

            return;
        }

        var failure = CharacterLoadFailureRegex.Match(logString);

        if (!failure.Success)
        {
            return;
        }

        var path = failure.Groups[1].Value;

        if (UnloadableCharacters.All(x => x.Path != path))
        {
            UnloadableCharacters.Add(new UnloadableCharacter(path, [.. RecentMissingElements]));
        }

        RecentMissingElements.Clear();
    }

    // the game logs one exception per character it cannot load; those are handled after the pool is read
    internal static bool IsCharacterLoadException(string logString)
    {
        return logString != null && logString.Contains("Impossible to load character");
    }

    // returns null when nothing useful can be extracted, so the caller falls back to the generic warning
    internal static string DescribeException(string logString, string stackTrace, string logPath)
    {
        var message = (logString ?? string.Empty).Split('\n')[0].Trim();
        var frames = (stackTrace ?? string.Empty)
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith("UnityEngine.") && !x.StartsWith("Trace:"))
            .ToList();
        var origin = frames.FirstOrDefault(x => x.StartsWith("SolastaUnfinishedBusiness")) ?? frames.FirstOrDefault();

        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var source = origin == null
            ? Gui.Localize("Message/&ModErrorSourceUnknown")
            : origin.StartsWith("SolastaUnfinishedBusiness")
                ? Gui.Format("Message/&ModErrorSourceMod", origin)
                : Gui.Format("Message/&ModErrorSourceGame", origin);

        return Gui.Format("Message/&ModErrorWarningDetectedDescription", message, source, logPath);
    }

    // called after the game reads the character pool: move every file it could not load into a subfolder
    internal static void MoveUnloadableCharacters()
    {
        if (UnloadableCharacters.Count == 0)
        {
            return;
        }

        var unloadableFolder = Path.Combine(CharactersDirectory, UnloadableFolderName);

        foreach (var character in UnloadableCharacters)
        {
            try
            {
                var source = Path.GetFullPath(character.Path);

                if (!File.Exists(source) ||
                    !string.Equals(Path.GetDirectoryName(source), Path.GetFullPath(CharactersDirectory),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(unloadableFolder);

                var destination = Path.Combine(unloadableFolder, Path.GetFileName(source));

                if (File.Exists(destination))
                {
                    destination = Path.Combine(unloadableFolder,
                        $"{Path.GetFileNameWithoutExtension(source)}-{DateTime.Now:yyyyMMddHHmmss}.chr");
                }

                File.Move(source, destination);
                MovedCharacterFiles.Add(destination);

                var reason = character.MissingElements.Count > 0
                    ? string.Join(", ", character.MissingElements)
                    : Gui.Localize("Message/&ModErrorUnknownReason");

                MovedCharacters.Add($"{Path.GetFileNameWithoutExtension(source)}: {reason}");
                Main.Info($"moved unloadable character {source} to {destination} ({reason})");
            }
            catch (Exception ex)
            {
                Main.Error($"could not move unloadable character {character.Path}: {ex.Message}");
            }
        }

        UnloadableCharacters.Clear();
    }

    internal static void ShowMovedCharactersIfAny()
    {
        if (MovedCharacters.Count == 0 || Gui.GuiService == null)
        {
            return;
        }

        var content = Gui.Format("Message/&ModUnloadableCharactersDescription",
            string.Join("\n", MovedCharacters),
            Path.GetFullPath(Path.Combine(CharactersDirectory, UnloadableFolderName)));

        var files = MovedCharacterFiles.ToList();

        MovedCharacters.Clear();
        MovedCharacterFiles.Clear();

        // Keep is the main (confirm) button. Escape or closing the message also runs the cancel handler, so the
        // cancel-style Delete only asks again: files are deleted only from that second message's confirm button.
        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2,
            "Message/&ModUnloadableCharactersTitle", content,
            "Message/&ModUnloadableCharactersKeep", "Message/&ModUnloadableCharactersDelete",
            null, () => ConfirmDeleteFiles(files));
    }

    private static void ConfirmDeleteFiles(List<string> files)
    {
        Gui.GuiService.ShowMessage(MessageModal.Severity.Serious3,
            "Message/&ModUnloadableCharactersTitle",
            Gui.Format("Message/&ModUnloadableCharactersDeleteConfirm", files.Count.ToString()),
            "Message/&ModUnloadableCharactersDelete", "Message/&ModUnloadableCharactersKeep",
            () => DeleteFiles(files), null);
    }

    private static void DeleteFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    Main.Info($"deleted unloadable character {file}");
                }
            }
            catch (Exception ex)
            {
                Main.Error($"could not delete unloadable character {file}: {ex.Message}");
            }
        }
    }

    private sealed class UnloadableCharacter(string path, List<string> missingElements)
    {
        internal string Path { get; } = path;
        internal List<string> MissingElements { get; } = missingElements;
    }
}
