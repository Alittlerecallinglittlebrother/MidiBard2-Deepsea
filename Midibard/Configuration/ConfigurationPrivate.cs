using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Utility;

using Newtonsoft.Json;

using static Dalamud.api;

namespace MidiBard;

public class ConfigurationPrivate : IPluginConfiguration
{
    public static ConfigurationPrivate config;

    public int Version { get; set; }

    public bool[] EnabledTracks = Enumerable.Repeat(false, 100).ToArray();

    public static void Init()
    {
        Task.Run(() =>
        {
            bool loggedIn = false;
            while (!loggedIn)
            {
                try
                {
                    if (api.Player != null && api.ClientState.IsLoggedIn)
                    {
                        if (api.Player.HomeWorld.ValueNullable == null)
                        {
                            Thread.Sleep(500);
                            continue;
                        }

                        Load();
                        loggedIn = true;
                        return;
                    }
                }
                catch (Exception e)
                {
                    PluginLog.Error(e, "Error");
                    return;
                }

                Thread.Sleep(500);
            }
        });
    }

    public void Save()
    {
        try
        {
            if (api.Player != null && api.ClientState.IsLoggedIn)
            {
                if (api.Player.HomeWorld.ValueNullable != null)
                {
                    var playerName = api.Player.CharacterName;
                    var playerWorld = api.Player.HomeWorld.ValueNullable?.Name.ToDalamudString().TextValue;
                    var contentId = api.Player.ContentId;

                    var configFileInfo = GetConfigFileInfo(playerName, playerWorld, contentId);
                    var serializedContents = JsonConvert.SerializeObject(this, Formatting.Indented);

                    File.WriteAllText(configFileInfo.FullName, serializedContents);
                    PluginLog.Warning($"Saving {DateTime.Now} - {playerName}_{playerWorld}_{contentId}.json Saved");
                }
            }
        }
        catch (Exception e)
        {
            PluginLog.Error(e, "Error when saving private config");
            ImGuiUtil.AddNotification(NotificationType.Error, "Error when saving private config");
        }
    }

    public static void Load()
    {
        if (api.Player != null && api.ClientState.IsLoggedIn)
        {

            var contentId = api.Player.ContentId;
            if (api.Player.HomeWorld.ValueNullable != null)
            {
                var playerName = api.Player.CharacterName;
                var playerWorld = api.Player.HomeWorld.ValueNullable?.Name.ToDalamudString().TextValue;

                var configFileInfo = GetConfigFileInfo(playerName, playerWorld, contentId);
                if (configFileInfo.Exists)
                {
                    var fileText = File.ReadAllText(configFileInfo.FullName);

                    var loadedCharacterConfiguration = JsonConvert.DeserializeObject<ConfigurationPrivate>(fileText);
                    if (loadedCharacterConfiguration == null)
                    {
                        config = new ConfigurationPrivate();
                    }
                    else
                    {
                        config = loadedCharacterConfiguration;
                    }
                }
                else
                {
                    config = new ConfigurationPrivate();
                    config.EnabledTracks[0] = true; // always enable the 1st track for new user
                }
                return;
            }
        }

        config = new ConfigurationPrivate(); // to prevent unexpected exception when character isn't logged in.
    }

    static FileInfo GetConfigFileInfo(string charName, string world, ulong contentId)
    {
        var pluginConfigDirectory = api.PluginInterface.ConfigDirectory;

        return new FileInfo(pluginConfigDirectory.FullName + $@"\{charName}_{world}_{contentId}.json");
    }
}
