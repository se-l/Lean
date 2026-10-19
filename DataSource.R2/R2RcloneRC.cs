/*
 * The rclone remote-control client, used ONLY on the stale-view recovery path (see R2StaleView):
 * the mount runs --rc --rc-no-auth, so the plugin can invalidate the VFS/dir cache itself and
 * self-heal without waiting for the r2-events service (whose vfs/refresh on object events is
 * the FIRST line of defence; this is the second, active only once a read already failed).
 *
 * Deliberately fire-and-forget: a refresh that cannot be issued (RC down, socket hiccup) is
 * logged at Trace and swallowed — correctness comes from re-resolving the head from the REST
 * catalog and the bounded attempt budget in R2LocalCatalog, not from this call. The recovery
 * sequence must never fail because its first optimisation step did.
 */

using System;
using System.Collections.Generic;
using System.Net.Http;
using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Posts rclone RC commands to the mount's control socket. Every call is idempotent and
    /// failure-tolerant: success or a Trace line, never an exception.
    /// </summary>
    internal static class R2RcloneRC
    {
        /// <summary>Config key for the RC endpoint; the default is rclone's --rc-port default.</summary>
        private const string UrlSetting = "cloudflare-r2.rc-url";
        private const string DefaultUrl = "http://localhost:5572";

        private static readonly Lazy<HttpClient> Client =
            new Lazy<HttpClient>(() => new HttpClient { Timeout = TimeSpan.FromSeconds(2) });

        /// <summary>
        /// Re-reads the directory tree under <paramref name="dir"/> (mount-relative, e.g.
        /// <c>__r2_data_catalog/&lt;uuid&gt;</c>), dropping entries the bucket no longer has.
        /// </summary>
        public static void RefreshDirectory(string dir)
        {
            Post("vfs/refresh", new Dictionary<string, string>
            {
                { "dir", dir },
                { "recursive", "true" }
            });
        }

        /// <summary>Drops one exact mount-relative path from the VFS cache.</summary>
        public static void Forget(string path)
        {
            Post("vfs/forget", new Dictionary<string, string>
            {
                { "path", path }
            });
        }

        private static void Post(string command, Dictionary<string, string> arguments)
        {
            try
            {
                var url = Url().TrimEnd('/') + "/" + command;
                using var content = new FormUrlEncodedContent(arguments);
                using var response = Client.Value.PostAsync(url, content).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    Log.Trace($"R2RcloneRC: {command} answered {(int)response.StatusCode} " +
                              $"{response.ReasonPhrase}.");
                }
            }
            catch (Exception error)
            {
                Log.Trace($"R2RcloneRC: {command} unavailable, continuing without it: {error.Message}");
            }
        }

        private static string Url()
        {
            var url = Config.Get(UrlSetting, DefaultUrl);
            return string.IsNullOrEmpty(url) ? DefaultUrl : url.Trim();
        }
    }
}
