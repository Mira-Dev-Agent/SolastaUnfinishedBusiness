using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using System.Xml.Serialization;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace SolastaUnfinishedBusiness.Models;

// Mod version and settings for multiplayer rooms:
// - every player publishes its mod version and build; the host's is listed in the lobby and mismatches are reported
// - the host publishes its settings; clients play with them (saved to Settings/Host.xml) without touching their own
//   Settings.xml, and the mod menu shows them read-only. Settings marked "Requires Restart" can't change mid-session,
//   so the client is offered to start the next launch with the host's settings.
internal static class MultiplayerSessionContext
{
    private const string VersionKey = "UbV";
    private const string SettingsKey = "UbS";
    private const string SettingsHashKey = "UbSH";
    private const string PingKey = "UbPg";
    private const float PingPublishInterval = 3f;
    private const int PingPublishThresholdMs = 5;
    private const string HostSettingsName = "Host";
    private const string UseHostSettingsFlag = "UseHostSettingsOnNextLaunch.flag";
    private const string RequiresRestartMarker = "Requires Restart";

    private static readonly string[] UiStatePrefixes = ["Display", "Selected", "EnableDiagsDump"];

    private static Settings _localSettings;
    private static string _appliedSettingsHash;
    private static HashSet<string> _restartOnlySettings;
    private static float _nextPingPublishTime;
    private static int _publishedPing = -1;

    internal static bool IsUsingHostSettings => _localSettings != null;
    internal static bool IsUsingHostSettingsForThisLaunch { get; private set; }
    internal static string HostVersion { get; private set; }

    // every numbered release is a unique build (the release workflow refuses a version that was already released),
    // so the version alone tells builds apart
    internal static string BuildVersion => Main.ModVersion;

    private static string HostSettingsFile => Path.Combine(Main.SettingsFolder, $"{HostSettingsName}.xml");
    private static string UseHostSettingsFlagFile => Path.Combine(Main.SettingsFolder, UseHostSettingsFlag);

    private static bool IsOnlineRoom => PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;


    #region Room Events

    internal static void OnJoinedRoom()
    {
        if (!IsOnlineRoom)
        {
            return;
        }

        PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { VersionKey, BuildVersion } });

        if (PhotonNetwork.IsMasterClient)
        {
            PublishVersion();
            PublishSettings();

            return;
        }

        RefreshHostVersion();
        ApplyHostSettingsIfChanged();
    }

    internal static void OnRoomPropertiesUpdate(Hashtable changed)
    {
        if (!IsOnlineRoom || PhotonNetwork.IsMasterClient || changed == null)
        {
            return;
        }

        if (changed.ContainsKey(VersionKey))
        {
            RefreshHostVersion();
        }

        if (changed.ContainsKey(SettingsHashKey) || changed.ContainsKey(SettingsKey))
        {
            ApplyHostSettingsIfChanged();
        }
    }

    internal static void OnMasterClientSwitched(Player newMasterClient)
    {
        if (newMasterClient is not { IsLocal: true })
        {
            RefreshHostVersion();

            return;
        }

        // this computer now hosts: go back to its own settings and share them
        RestoreLocalSettings();
        PublishVersion();
        PublishSettings();
        HostVersion = BuildVersion;
    }

    internal static void OnLeftRoom()
    {
        _publishedPing = -1;
        _nextPingPublishTime = 0;
        MultiplayerFpsContext.Reset();
        RestoreLocalSettings();
        HostVersion = null;
    }

    // the host shares its settings again whenever they are saved from the mod menu
    internal static void SettingsSaved()
    {
        if (IsOnlineRoom && PhotonNetwork.IsMasterClient)
        {
            PublishSettings();
        }
    }

    #endregion

    #region Version

    private static void PublishVersion()
    {
        var room = PhotonNetwork.CurrentRoom;

        if (room == null)
        {
            return;
        }

        room.SetCustomProperties(new Hashtable { { VersionKey, BuildVersion } });

        var listed = room.PropertiesListedInLobby ?? [];

        if (!listed.Contains(VersionKey))
        {
            room.SetPropertiesListedInLobby([.. listed, VersionKey]);
        }
    }

    private static void RefreshHostVersion()
    {
        var room = PhotonNetwork.CurrentRoom;

        HostVersion = room?.CustomProperties.TryGetValue(VersionKey, out var value) == true
            ? value as string
            : GetPlayerVersion(room?.GetPlayer(room.MasterClientId));
    }

    // null when the player doesn't report a build (another mod version, or no mod)
    internal static string GetPlayerVersion(Player player)
    {
        if (player == null)
        {
            return null;
        }

        if (player.IsLocal)
        {
            return BuildVersion;
        }

        return player.CustomProperties.TryGetValue(VersionKey, out var value) ? value as string : null;
    }

    internal static string DescribeVersion(string version)
    {
        return version ?? Gui.Localize("ModUi/&MultiplayerNoVersion");
    }

    internal static Player FindPlayer(int actorNumber, string nickname)
    {
        var room = PhotonNetwork.CurrentRoom;

        if (room == null)
        {
            return null;
        }

        return room.GetPlayer(actorNumber) ??
               room.Players.Values.FirstOrDefault(x => x.NickName == nickname);
    }

    // room list entries show the host's mod build; a different or missing one is highlighted
    internal static void DecorateRoomInfo(RoomInfo photonRoomInfo, ref NetworkingDefinitions.RoomInfo roomInfo)
    {
        string version = null;

        if (photonRoomInfo?.CustomProperties != null &&
            photonRoomInfo.CustomProperties.TryGetValue(VersionKey, out var value))
        {
            version = value as string;
        }

        var color = version == BuildVersion ? "#80C080" : "#E06060";

        roomInfo.campaignName = $"{roomInfo.campaignName} <color={color}>[{Main.ModAbbreviation} {DescribeVersion(version)}]</color>";
    }

    // player plates in the lobby and in game show each player's build when hovered; a different or missing build
    // turns the name red
    // every player shares its ping to the server every few seconds, only when it changed noticeably
    internal static void PublishPingIfNeeded()
    {
        if (!IsOnlineRoom || Time.unscaledTime < _nextPingPublishTime)
        {
            return;
        }

        _nextPingPublishTime = Time.unscaledTime + PingPublishInterval;

        var ping = PhotonNetwork.GetPing();

        if (_publishedPing >= 0 && Mathf.Abs(ping - _publishedPing) < PingPublishThresholdMs)
        {
            return;
        }

        _publishedPing = ping;
        PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { PingKey, ping } });
    }

    // the host's setting decides who sees pings, since clients play with the host's settings
    internal static bool CanSeePings =>
        IsOnlineRoom && (!Main.Settings.MultiplayerPingVisibleToHostOnly || PhotonNetwork.IsMasterClient);

    internal static int? GetPlayerPing(Player player)
    {
        if (player == null)
        {
            return null;
        }

        if (player.IsLocal)
        {
            return PhotonNetwork.GetPing();
        }

        return player.CustomProperties.TryGetValue(PingKey, out var value) && value is int ping ? ping : null;
    }

    internal static string DescribePing(Player player)
    {
        var ping = GetPlayerPing(player);

        return ping.HasValue
            ? Gui.Format("ModUi/&MultiplayerPing", ping.Value.ToString())
            : Gui.Localize("ModUi/&MultiplayerNoPing");
    }

    internal static void DecoratePlayerInfoGroup(PlayerInfoGroup group)
    {
        var slot = group?.playerSlot;

        if (!IsOnlineRoom || slot == null)
        {
            return;
        }

        var player = FindPlayer(slot.PlayerId, slot.PlayerNickname);
        var version = GetPlayerVersion(player);

        if (group.playerImageTooltip)
        {
            var content = $"{slot.PlayerNickname}\n{Main.ModAbbreviation} {DescribeVersion(version)}";

            if (CanSeePings)
            {
                content += $"\n{DescribePing(player)}";
            }

            if (MultiplayerFpsContext.CanSee(player))
            {
                content += $"\n{MultiplayerFpsContext.DescribeFps(player)}";
            }

            group.playerImageTooltip.Content = content;
        }

        if (version == BuildVersion)
        {
            group.ResetPlayerNameColor();
        }
        else
        {
            group.SetPlayerNameColor(new Color(0.88f, 0.38f, 0.38f));
        }
    }

    // shown when readying up in the lobby or when the host starts or loads the game: every build that differs from
    // this computer's or isn't reported. Only a warning, the action still goes ahead.
    internal static void WarnAboutVersionsIfNeeded()
    {
        if (!IsOnlineRoom)
        {
            return;
        }

        var lines = new List<string>();

        foreach (var player in PhotonNetwork.PlayerListOthers)
        {
            var version = GetPlayerVersion(player);

            if (version == BuildVersion)
            {
                continue;
            }

            var role = player.IsMasterClient ? Gui.Localize("ModUi/&MultiplayerHostRole") : string.Empty;

            lines.Add($"{player.NickName}{role}: {DescribeVersion(version)}");
        }

        if (lines.Count == 0)
        {
            return;
        }

        ShowMessage("Message/&MultiplayerVersionMismatchTitle",
            Gui.Format("Message/&MultiplayerVersionWarningDescription", BuildVersion, string.Join("\n", lines)));
    }

    #endregion

    #region Settings

    private static byte[] Serialize(Settings settings)
    {
        using var memory = new MemoryStream();

        using (var gzip = new GZipStream(memory, CompressionMode.Compress, true))
        {
            new XmlSerializer(typeof(Settings)).Serialize(gzip, settings);
        }

        return memory.ToArray();
    }

    private static Settings Deserialize(byte[] data)
    {
        using var memory = new MemoryStream(data);
        using var gzip = new GZipStream(memory, CompressionMode.Decompress);

        return (Settings)new XmlSerializer(typeof(Settings)).Deserialize(gzip);
    }

    private static string Hash(byte[] data)
    {
        using var sha = SHA256.Create();

        return BitConverter.ToString(sha.ComputeHash(data), 0, 8).Replace("-", string.Empty);
    }

    private static void PublishSettings()
    {
        var room = PhotonNetwork.CurrentRoom;

        if (room == null)
        {
            return;
        }

        try
        {
            var data = Serialize(Main.Settings);

            room.SetCustomProperties(new Hashtable { { SettingsKey, data }, { SettingsHashKey, Hash(data) } });
        }
        catch (Exception ex)
        {
            Main.Error($"could not share settings with the room: {ex.Message}");
        }
    }

    private static void ApplyHostSettingsIfChanged()
    {
        var properties = PhotonNetwork.CurrentRoom?.CustomProperties;

        if (properties == null ||
            !properties.TryGetValue(SettingsKey, out var value) ||
            value is not byte[] data)
        {
            return;
        }

        var hash = Hash(data);

        if (hash == _appliedSettingsHash)
        {
            return;
        }

        Settings hostSettings;

        try
        {
            hostSettings = Deserialize(data);
        }
        catch (Exception ex)
        {
            Main.Error($"could not read the host's settings: {ex.Message}");

            return;
        }

        var localSettings = _localSettings ?? Main.Settings;
        var restartOnlyDifferences = GetRestartOnlyDifferences(localSettings, hostSettings);

        SaveHostSettingsFile(hostSettings);

        _localSettings = localSettings;
        _appliedSettingsHash = hash;
        Main.UseSettings(hostSettings);
        Main.Info($"using the host's settings ({hash}) while in this room");

        if (restartOnlyDifferences.Count == 0 || IsUsingHostSettingsForThisLaunch)
        {
            return;
        }

        Gui.GuiService?.ShowMessage(MessageModal.Severity.Attention2,
            "Message/&MultiplayerHostSettingsRestartTitle",
            Gui.Format("Message/&MultiplayerHostSettingsRestartDescription",
                string.Join(", ", restartOnlyDifferences)),
            "Message/&MultiplayerHostSettingsRestartAccept", "Message/&MultiplayerHostSettingsRestartDecline",
            RequestHostSettingsOnNextLaunch, null);
    }

    private static void RestoreLocalSettings()
    {
        // settings that only apply at startup were built with the host's values this launch, so keep them
        if (_localSettings == null || IsUsingHostSettingsForThisLaunch)
        {
            return;
        }

        Main.UseSettings(_localSettings);
        _localSettings = null;
        _appliedSettingsHash = null;
        Main.Info("using this computer's own settings again");
    }

    // what to save when the mod menu closes: never the host's copy
    internal static Settings SettingsToSave => _localSettings ?? Main.Settings;

    private static void SaveHostSettingsFile(Settings hostSettings)
    {
        try
        {
            Main.EnsureFolderExists(Main.SettingsFolder);

            using var stream = File.Create(HostSettingsFile);

            new XmlSerializer(typeof(Settings)).Serialize(stream, hostSettings);
        }
        catch (Exception ex)
        {
            Main.Error($"could not save the host's settings: {ex.Message}");
        }
    }

    private static List<string> GetRestartOnlyDifferences(Settings local, Settings host)
    {
        _restartOnlySettings ??=
        [
            .. TranslatorContext.GetEnglishTermsContaining(RequiresRestartMarker)
                .Where(x => x.StartsWith("ModUi/&", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Substring("ModUi/&".Length))
        ];

        var localElements = ToElements(local);
        var hostElements = ToElements(host);

        return hostElements.Keys
            .Where(x => _restartOnlySettings.Contains(x) &&
                        !UiStatePrefixes.Any(x.StartsWith) &&
                        (!localElements.TryGetValue(x, out var localValue) || localValue != hostElements[x]))
            .OrderBy(x => x)
            .ToList();
    }

    private static Dictionary<string, string> ToElements(Settings settings)
    {
        using var memory = new MemoryStream();

        new XmlSerializer(typeof(Settings)).Serialize(memory, settings);
        memory.Position = 0;

        var root = XDocument.Load(memory).Root;
        var result = new Dictionary<string, string>();

        if (root == null)
        {
            return result;
        }

        foreach (var element in root.Elements())
        {
            result[element.Name.LocalName] = element.ToString(SaveOptions.DisableFormatting);
        }

        return result;
    }

    private static void RequestHostSettingsOnNextLaunch()
    {
        try
        {
            File.WriteAllText(UseHostSettingsFlagFile, DateTime.Now.ToString("O"));

            ShowMessage("Message/&MultiplayerHostSettingsRestartTitle",
                Gui.Localize("Message/&MultiplayerHostSettingsRestartScheduled"));
        }
        catch (Exception ex)
        {
            Main.Error($"could not schedule the host's settings for the next launch: {ex.Message}");
        }
    }

    // one launch only: load Settings/Host.xml instead of Settings.xml, which stays untouched
    internal static void UseHostSettingsForThisLaunchIfRequested()
    {
        if (!File.Exists(UseHostSettingsFlagFile))
        {
            return;
        }

        try
        {
            File.Delete(UseHostSettingsFlagFile);

            if (!File.Exists(HostSettingsFile))
            {
                return;
            }

            Settings hostSettings;

            using (var stream = File.OpenRead(HostSettingsFile))
            {
                hostSettings = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(stream);
            }

            _localSettings = Main.Settings;
            IsUsingHostSettingsForThisLaunch = true;
            Main.UseSettings(hostSettings);
            Main.Info("using the host's settings for this launch");
        }
        catch (Exception ex)
        {
            Main.Error($"could not load the host's settings for this launch: {ex.Message}");
        }
    }

    #endregion

    private static void ShowMessage(string title, string content)
    {
        Gui.GuiService?.ShowMessage(MessageModal.Severity.Attention2, title, content,
            "Message/&MessageOkTitle", null, null, null);
    }
}
