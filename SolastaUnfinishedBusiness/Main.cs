using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness;

internal static class Main
{
    internal static readonly string ModFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    private static ModManager<Core, Settings> Mod { get; set; }
    private static UnityModManager.ModEntry ModEntry { get; set; }

    internal static bool Enabled { get; private set; }

    internal static Action Enable { get; private set; }

    internal static string SettingsFolder => Path.Combine(ModFolder, "Settings");
    internal static string[] SettingsFiles { get; private set; }
    internal static string SettingsFilename { get; private set; } = string.Empty;
    internal static Settings Settings => Mod.Settings;

    internal static string ModVersion => ModEntry?.Info?.Version ?? string.Empty;

    // the name the mod manager shows (Info.json DisplayName) without its rich text tags, so "Finishing Business";
    // the mod's id when there is no display name
    internal static string ModName
    {
        get
        {
            var displayName = Regex.Replace(ModEntry?.Info?.DisplayName ?? string.Empty, "<[^>]*>", string.Empty)
                .Trim();

            return displayName.Length > 0 ? displayName : ModEntry?.Info?.Id ?? string.Empty;
        }
    }

    // the first capital letter of each word of the mod name ("FB"), or a word's first letter when it has no capital
    internal static string ModAbbreviation =>
        string.Concat(ModName.Split([' '], StringSplitOptions.RemoveEmptyEntries).Select(word =>
        {
            var capital = word.FirstOrDefault(char.IsUpper);

            return char.ToUpperInvariant(capital != '\0' ? capital : word[0]);
        }));

    // swaps the settings the mod reads; used to play with the host's settings as a multiplayer client
    internal static void UseSettings(Settings settings)
    {
        Mod.Settings = settings;
    }

    [Conditional("DEBUG")]
    internal static void Log(string msg, bool console = false)
    {
        ModEntry.Logger.Log(msg);

        if (!console)
        {
            return;
        }

        var game = Gui.Game;

        if (!game)
        {
            return;
        }

        game.GameConsole?.LogSimpleLine(msg);
    }

    internal static void Error(Exception ex)
    {
        ModEntry.Logger.Error(ex.ToString());
    }

    internal static void Error(string msg, bool stack = false)
    {
        ModEntry.Logger.Error(msg + (stack ? Environment.StackTrace : ""));
    }

    internal static void Info(string msg)
    {
        ModEntry.Logger.Log(msg);
    }

    internal static void EnsureFolderExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    [UsedImplicitly]
    internal static bool Load([NotNull] UnityModManager.ModEntry modEntry)
    {
        ModEntry = modEntry;

        // before the game boots, so character pool load failures are captured
        ErrorDiagnosticsContext.StartListening();

        var now = DateTime.Now;
        var assembly = Assembly.GetExecutingAssembly();

        var runtimeVersion = typeof(UnityModManager)
            .GetTypeInfo()
            .Assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>();

        var unityModManagerVersion = runtimeVersion.Version.Split('.');

        if (unityModManagerVersion.Length > 2 &&
            int.TryParse(unityModManagerVersion[1], NumberStyles.Integer, CultureInfo.CurrentCulture, out var minor) &&
            int.TryParse(unityModManagerVersion[2], NumberStyles.Integer, CultureInfo.CurrentCulture, out var rev) &&
            ((minor == 27 && rev > 10) || minor > 27))
        {
            Info($"Unity mod manager version {runtimeVersion.Version} is not compatible with UB. aborting.");

            return false;
        }

        EnsureFolderExists(SettingsFolder);
        PortraitsContext.EnsureFolderExists();
        SaveByLocationContext.EnsureFoldersExist();

        try
        {
            Mod = new ModManager<Core, Settings>();
            Mod.Enable(modEntry, assembly);
            AddMissingSettingsToFile(modEntry);
            MultiplayerSessionContext.UseHostSettingsForThisLaunchIfRequested();

            modEntry.OnShowGUI = _ =>
            {
                if (Settings.EnableHeroesControlledByComputer)
                {
                    PlayerControllerContext.RefreshGuiState();
                }
            };

            Enable = () =>
            {
                var finished = DateTime.Now;

                new MenuManager().Enable(modEntry, assembly);
                LoadSettingFilenames();
                ModEntry.Logger.Log($"enabled in {finished - now:T}.");

                Enabled = true;
            };

            TranslatorContext.EarlyLoad();
        }
        catch (Exception ex)
        {
            Error(ex);
            throw;
        }

        return true;
    }

    // after an update the settings file lacks the new settings until the mod menu saves it again. Write them now with
    // their defaults. Every other value comes from what was just loaded, so it is written back unchanged. Only do it
    // when the file parses as Settings, so a file the game failed to read is never overwritten with defaults.
    private static void AddMissingSettingsToFile(UnityModManager.ModEntry modEntry)
    {
        var path = Path.Combine(modEntry.Path, "Settings.xml");

        if (!File.Exists(path))
        {
            return;
        }

        var document = new XmlDocument();

        try
        {
            document.Load(path);

            using var reader = new XmlNodeReader(document);

            _ = new XmlSerializer(typeof(Settings)).Deserialize(reader);
        }
        catch (Exception ex)
        {
            Error($"Settings.xml could not be read, so it was left unchanged: {ex.Message}");

            return;
        }

        var existing = document.DocumentElement?.ChildNodes.OfType<XmlElement>().Select(x => x.Name).ToHashSet() ??
                       [];
        var missing = typeof(Settings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanRead && x.CanWrite && x.GetIndexParameters().Length == 0 &&
                        !x.IsDefined(typeof(XmlIgnoreAttribute)))
            .Select(x => x.GetCustomAttribute<XmlElementAttribute>()?.ElementName is { Length: > 0 } name
                ? name
                : x.Name)
            .Where(x => !existing.Contains(x))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        File.Copy(path, path + ".bak", true);
        UnityModManager.ModSettings.Save(Settings, modEntry);
        Info($"added {missing.Count} new settings to Settings.xml: {string.Join(", ", missing)}");
    }

    internal static void LoadSettingFilenames()
    {
        SettingsFiles = Directory.GetFiles(SettingsFolder)
            .Where(x => x.EndsWith(".xml"))
            .Select(Path.GetFileNameWithoutExtension)
            .ToArray();
    }

    private static bool ValidateFilename(ref string filename)
    {
        if (string.IsNullOrEmpty(filename))
        {
            return false;
        }

        filename = string.Concat(filename.Split(Path.GetInvalidFileNameChars()));
        filename = Path.GetFileName(filename) + ".xml";

        return true;
    }

    internal static void SaveSettings(string filename)
    {
        if (!ValidateFilename(ref filename))
        {
            return;
        }

        SettingsFilename = Path.Combine(SettingsFolder, filename);
        UnityModManager.ModSettings.Save(Settings, ModEntry);
        SettingsFilename = string.Empty;

        LoadSettingFilenames();
    }

    internal static void LoadSettings(string filename)
    {
        SettingsFilename = Path.Combine(SettingsFolder, $"{filename}.xml");
        Mod.Settings = UnityModManager.ModSettings.Load<Settings>(ModEntry);
        SettingsFilename = string.Empty;
    }

    internal static void RemoveSettings(string filename)
    {
        if (!ValidateFilename(ref filename))
        {
            return;
        }

        filename = Path.Combine(SettingsFolder, filename);

        if (File.Exists(filename))
        {
            File.Delete(filename);
        }

        LoadSettingFilenames();
    }
}
