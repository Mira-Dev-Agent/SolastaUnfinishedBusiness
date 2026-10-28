using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit.Utility;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness.Models;

internal static class UpdateContext
{
    private static readonly string TempFolder = $"TEMP_UPDATE{Path.DirectorySeparatorChar}";
    private static readonly string ModFolder = $"SolastaUnfinishedBusiness{Path.DirectorySeparatorChar}";

    // an in-game update moves every installed file it replaces to Archive/<version it replaced>, with a list of the
    // files it added, so Rollback can put that version back exactly. Only versions left by an update can be rolled
    // back to, and only the latest one is kept (it is about the size of the mod).
    private const string ArchiveFolderName = "Archive";
    private const string AddedFilesList = "added-files.txt";

    private static string ArchiveFolder => Path.Combine(Main.ModFolder, ArchiveFolderName);
    private static InfoJson Info { get; set; }
    private static string BaseURL { get; set; }
    private static string VersionURL { get; set; }
    private static string InstalledVersion { get; set; }
    private static string LatestVersion { get; set; }
    private static string PreviousVersion { get; set; }
    internal static bool InProgress { get; private set; }
    internal static int Progress { get; private set; }

    private static bool ShouldUpdate;

    internal static void Load()
    {
        var infoPayload = File.ReadAllText(Path.Combine(Main.ModFolder, "Info.json"));
        Info = JsonConvert.DeserializeObject<InfoJson>(infoPayload);

        BaseURL = Info.Repository + "/releases/download";
        VersionURL = Info.VersionURL;
        InstalledVersion = Info.Version;
        PreviousVersion = GetPreviousVersion();

        LatestVersion = GetLatestVersion(out ShouldUpdate);
        if (ShouldUpdate)
        {
            DisplayUpdateMessage();
        }
        else
        {
            CustomModels.AlertIfModelsNotFound();

            if (Main.Settings.DisplayModMessage == 0)
            {
                DisplayWelcomeMessage();
            }
        }

        // display mod message every 100 launches
        Main.Settings.DisplayModMessage = (Main.Settings.DisplayModMessage + 1) % 100;
    }

    internal static bool CanRollback => PreviousVersion != null;

    internal static bool IsUpdateAvailable => ShouldUpdate;

    // the newest archived version older than this one, or null when no in-game update left one
    private static string GetPreviousVersion()
    {
        if (!Directory.Exists(ArchiveFolder))
        {
            return null;
        }

        return Directory.GetDirectories(ArchiveFolder)
            .Select(Path.GetFileName)
            .Where(version => CompareVersions(version, InstalledVersion) < 0)
            .OrderByDescending(version => version, Comparer<string>.Create(CompareVersions))
            .FirstOrDefault();
    }

    // part by part as numbers, so 1.6.10.0 is newer than 1.6.9.0; a missing part counts as 0
    private static int CompareVersions(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && int.TryParse(a[i], out var parsedA) ? parsedA : 0;
            var y = i < b.Length && int.TryParse(b[i], out var parsedB) ? parsedB : 0;

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private static string GetLatestVersion(out bool shouldUpdate)
    {
        var version = "";

        shouldUpdate = false;

        using var wc = new WebClient();

        wc.Encoding = Encoding.UTF8;

        try
        {
            var infoPayload = wc.DownloadString(VersionURL);
            var infoJson = JsonConvert.DeserializeObject<JObject>(infoPayload);

            // ReSharper disable once AssignNullToNotNullAttribute
            version = infoJson["Version"].Value<string>();

            shouldUpdate = CompareVersions(version, InstalledVersion) > 0;
        }
        catch
        {
            Main.Error("cannot fetch update data.");
        }

        return version;
    }

    internal static void UpdateMod()
    {
        if (InProgress) { return; }

        if (!ShouldUpdate)
        {
            ShowMessage("Mod version is already the latest or higher",
                "ChangeLog", OpenChangeLog);

            return;
        }

        InProgress = true;
        Progress = 0;

        var version = LatestVersion;
        var zipFile = "SolastaUnfinishedBusiness.zip";
        var fullZipFile = Path.Combine(Main.ModFolder, zipFile);
        var fullZipFolder = Path.Combine(Main.ModFolder, TempFolder);
        var baseUrlByVersion = BaseURL.Replace("download", $"download/{version}");
        var url = new Uri($"{baseUrlByVersion}/{zipFile}");

        try
        {
            using var wc = new WebClient();

            wc.Encoding = Encoding.UTF8;
            wc.DownloadProgressChanged += (_, e) => Progress = e.ProgressPercentage;

            wc.DownloadFileCompleted += OnDownloadFileCompleted;

            wc.DownloadFileAsync(url, fullZipFile);
        }
        catch (Exception ex)
        {
            InProgress = false;
            Main.Error($"Failed to update mod: {ex.Message}: {ex.StackTrace}");

            ShowMessage($"Cannot fetch update payload. Try again or download from:\r\n{url}.",
                "Open Download Url", () => OpenUrl(url.ToString()),
                severity: MessageModal.Severity.Serious3);
        }

        return;

        void OnDownloadFileCompleted(object _, AsyncCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                InProgress = false;
                ShowMessage($"Cannot fetch update payload. Try again or download from:\r\n{url}.",
                    "Open Download Url", () => OpenUrl(url.ToString()),
                    severity: MessageModal.Severity.Serious3);
                return;
            }

            if (e.Cancelled)
            {
                InProgress = false;
                ShowMessage("Update was cancelled",
                    "Open Download Url", () => OpenUrl(url.ToString()),
                    severity: MessageModal.Severity.Serious3);
                return;
            }

            try
            {
                if (Directory.Exists(fullZipFolder))
                {
                    Directory.Delete(fullZipFolder, true);
                }

                ZipFile.ExtractToDirectory(fullZipFile, fullZipFolder);

                var archive = Path.Combine(ArchiveFolder, InstalledVersion);
                var addedFiles = new List<string>();

                if (Directory.Exists(archive))
                {
                    Directory.Delete(archive, true);
                }

                Directory.CreateDirectory(archive);

                foreach (var sourceFile in Directory.GetFiles(fullZipFolder, "*", SearchOption.AllDirectories))
                {
                    var destFile = sourceFile.ReplaceFirst(TempFolder, string.Empty);

                    while (Regex.Matches(destFile, Regex.Escape(ModFolder)).Count > 1)
                    {
                        destFile = destFile.ReplaceLastOccurrence(ModFolder, string.Empty);
                    }

                    var destFolder = Path.GetDirectoryName(destFile)!;

                    Directory.CreateDirectory(destFolder);

                    // every file is replaced, changed or not; the installed one is kept in the archive
                    var relative = RelativeToModFolder(destFile);

                    if (File.Exists(destFile))
                    {
                        var archived = Path.Combine(archive, relative);

                        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
                        File.Move(destFile, archived);
                    }
                    else
                    {
                        addedFiles.Add(relative);
                    }

                    File.Move(sourceFile, destFile);
                }

                File.WriteAllLines(Path.Combine(archive, AddedFilesList), addedFiles);
                RemoveOtherArchives(archive);
                ShouldUpdate = false;
                PreviousVersion = GetPreviousVersion();

                ShowMessage("Mod update is successful. Please restart.", "ChangeLog", OpenChangeLog);
            }
            catch (Exception err)
            {
                Main.Error($"Failed to update mod: {err.Message}: {err.StackTrace}");

                ShowMessage($"Failed to unpack update. Try again or download and update manually from:\r\n{url}.",
                    "Open Download Url", () => OpenUrl(url.ToString()),
                    severity: MessageModal.Severity.Serious3);
            }
            finally
            {
                InProgress = false;

                try
                {
                    File.Delete(fullZipFile);
                    Directory.Delete(fullZipFolder, true);
                }
                catch
                {
                    /* ignored */
                }
            }
        }
    }

    internal static void DisplayRollbackMessage()
    {
        if (InProgress || !CanRollback) { return; }

        ShowMessage($"Would you like to rollback to {PreviousVersion}?",
            "Message/&MessageOkTitle", Rollback,
            "Message/&MessageCancelTitle");
    }

    // puts back the files the update to this version replaced and removes the ones it added
    private static void Rollback()
    {
        var version = PreviousVersion;
        var archive = Path.Combine(ArchiveFolder, version);

        try
        {
            var addedList = Path.Combine(archive, AddedFilesList);

            if (File.Exists(addedList))
            {
                foreach (var added in File.ReadAllLines(addedList).Where(x => x.Length > 0))
                {
                    var file = Path.Combine(Main.ModFolder, added);

                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
            }

            foreach (var archived in Directory.GetFiles(archive, "*", SearchOption.AllDirectories)
                         .Where(file => file != addedList))
            {
                var destFile = Path.Combine(Main.ModFolder, archived.Substring(archive.Length + 1));

                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                if (File.Exists(destFile))
                {
                    File.Delete(destFile);
                }

                File.Move(archived, destFile);
            }

            Directory.Delete(archive, true);
            PreviousVersion = GetPreviousVersion();

            ShowMessage($"Rolled back to {version}. Please restart.", "ChangeLog", OpenChangeLog);
        }
        catch (Exception err)
        {
            Main.Error($"Failed to roll back mod: {err.Message}: {err.StackTrace}");

            ShowMessage($"Failed to roll back to {version}. Its files are still in:\r\n{archive}.",
                "Message/&MessageOkTitle", severity: MessageModal.Severity.Serious3);
        }
    }

    private static string RelativeToModFolder(string path)
    {
        return path.Substring(Main.ModFolder.Length).TrimStart(Path.DirectorySeparatorChar, '/');
    }

    // only the archive the update just made is kept
    private static void RemoveOtherArchives(string keep)
    {
        foreach (var old in Directory.GetDirectories(ArchiveFolder).Where(folder => folder != keep))
        {
            Directory.Delete(old, true);
        }
    }

    // Changelog opens it and keeps this message up (the message box closes after a button, so it reopens a frame
    // later); Later closes it
    private static void DisplayUpdateMessage()
    {
        ShowMessage(
            $"Version {LatestVersion} is now available. Update it from Mod UI > Gameplay > General or the game's " +
            "Settings > GUI.",
            "Changelog", () =>
            {
                OpenChangeLog();
                NextFrame(DisplayUpdateMessage);
            },
            "Message/&MessageLaterTitle");
    }

    // asks before updating from the game's settings menu, which has no buttons, only toggles
    internal static void DisplayUpdateConfirmation()
    {
        if (InProgress || !ShouldUpdate) { return; }

        ShowMessage($"Update to version {LatestVersion} now? You'll need to restart the game afterwards.",
            "Message/&MessageUpdateTitle", UpdateMod,
            "Message/&MessageLaterTitle");
    }

    private static void NextFrame(Action action)
    {
        if (Gui.GuiService is MonoBehaviour behaviour && behaviour)
        {
            behaviour.StartCoroutine(RunNextFrame(action));
        }
        else
        {
            action();
        }
    }

    private static IEnumerator RunNextFrame(Action action)
    {
        yield return null;

        action();
    }

    private static void DisplayWelcomeMessage()
    {
        ShowMessage("Message/&MessageModWelcomeDescription",
            "ChangeLog", OpenChangeLog);
    }

    private static void ShowMessage(
        string content,
        string validateCaption,
        [CanBeNull] MessageModal.MessageValidatedHandler onValidated = null,
        string cancelCaption = "Message/&MessageOkTitle",
        [CanBeNull] MessageModal.MessageCancelledHandler onCancelled = null,
        string title = "Message/&MessageModWelcomeTitle",
        MessageModal.Severity severity = MessageModal.Severity.Attention2
    )
    {
        onValidated ??= () => { };
        onCancelled ??= () => { };
        Gui.GuiService.ShowMessage(severity, title, content, validateCaption, cancelCaption, onValidated, onCancelled);

        UnityModManager.UI.Instance.ToggleWindow(false);
    }

    internal static void OpenChangeLog()
    {
        OpenUrl(Info.Changelog);
    }

    internal static void OpenDocumentation(string filename)
    {
        OpenUrl($"file://{Main.ModFolder}/Documentation/{filename}");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(url);
        }
        catch
        {
            // because of this: https://github.com/dotnet/corefx/issues/10361
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                url = url.Replace("&", "^&");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                throw;
            }
        }
    }
}
