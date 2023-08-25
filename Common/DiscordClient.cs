using System;
using System.Net.Http;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json;
using QuantConnect.Configuration;
using System.Linq;

namespace QuantConnect
{
    /// <summary>
    /// Discord channels
    /// </summary>
    public enum DiscordChannel
    {
        General,
        Trades,
        Emergencies,
        Status,
        Paper
    }
    /// <summary>
    /// Send messages to Discord via WebHooks. WebHook URLs are stored in config.json
    /// </summary>
    public static class DiscordClient
    {
        private readonly static HttpClient httpClient = new();

        private readonly static Dictionary<DiscordChannel, Uri> Webhooks = Enum.GetValues(typeof(DiscordChannel)).Cast<DiscordChannel>().ToDictionary(
            channel => channel,
            channel => GetChannel(channel)
        );

        /// <summary>
        /// 
        /// </summary>
        public static async void Send(string message, DiscordChannel channel, bool LiveMode = false)
        {
            try
            {
                if (!LiveMode)
                {
                    return;
                }
                Uri? webhookUrl = Config.Get("ib-trading-mode") == "paper" ? Webhooks[DiscordChannel.Paper] : Webhooks[channel];
                if (webhookUrl == null) {
                    Console.Write($"Could not find a webhook for channel {channel}. Have you included this in the config.json?");
                    return;
                }

                var msg = new
                {
                    content = message
                };
                var payload = JsonConvert.SerializeObject(msg);
                using StringContent httpContent = new(payload, Encoding.UTF8, "application/json");

                using var response = await httpClient.PostAsync(webhookUrl, httpContent);
                if (!response.IsSuccessStatusCode)
                {
                    Console.Write($"Failed to send message to Discord server. HTTP status code: {response.StatusCode}. Reason: {response.ReasonPhrase}");
                }
            }
            catch (Exception e)
            {
                Console.Write($"DiscordClient.Send - {e}");
            }

        }

        public static Uri? GetChannel(DiscordChannel channel)
        {
            var channelJson = Config.Get("discord-channels", null);
            if (channelJson == null) { 
                return null; 
            }
            else
            {
                var channels = JsonConvert.DeserializeObject<Dictionary<string, string>>(channelJson);
                channels.TryGetValue(channel.ToString(), out string? channelUrl);
                if (channelUrl == null) { 
                    return null; 
                }
                else { 
                    return new Uri(channelUrl); 
                }
            }
        }
    }
}
