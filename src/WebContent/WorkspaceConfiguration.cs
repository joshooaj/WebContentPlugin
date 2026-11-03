using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace WebContent
{
    /// <summary>
    /// Why an admin-defined workspace has no usable browser configuration (ticket #9). Surfaced
    /// to the operator as a WorkSpaceNotification on the workspace tab instead of letting the
    /// tab silently disappear.
    /// </summary>
    internal enum WorkspaceConfigError
    {
        None,

        /// <summary>The WorkspaceConfig JSON blob exists but cannot be parsed (corrupt).</summary>
        MalformedJson,

        /// <summary>No browser view item with a home address could be resolved from the definition.</summary>
        MissingBrowserConfig,
    }

    [DataContract]
    internal class WorkspaceConfiguration
    {
        internal const string WorkspaceConfigKey = "WorkspaceConfig";

        /// <summary>
        /// The placeholder name the admin <see cref="Admin.WorkspaceItemManager"/> gives a freshly
        /// created workspace item before the user completes it. The initial save carries an empty
        /// home address, so <see cref="DetectError"/> sees MissingBrowserConfig; that is not a
        /// broken saved definition, it is a new workspace nobody has saved yet.
        /// </summary>
        internal const string NewWorkspacePlaceholderName = "Enter a name";

        [DataMember(Name = "version")]
        public int Version { get; set; }

        [DataMember(Name = "layout")]
        public string Layout { get; set; }

        [DataMember(Name = "items")]
        public List<WorkspaceItemConfiguration> Items { get; set; }

        public static WorkspaceConfiguration CreateDefault(string homeAddress)
        {
            return new WorkspaceConfiguration
            {
                Version = 1,
                Layout = "1x1",
                Items = new List<WorkspaceItemConfiguration>
                {
                    new WorkspaceItemConfiguration
                    {
                        Type = "WebContent",
                        Position = 0,
                        Settings = new WorkspaceItemSettings
                        {
                            HomeAddress = homeAddress,
                            HideToolbar = false,
                            HideAddress = false,
                            ZoomLevel = WebContentConfiguration.DefaultZoomLevel,
                            RememberLastAddress = false,
                            ClearBrowsingDataOnExit = false,
                            AllowBackgroundBrowser = true,
                            AllowAudioNotifications = true,
                            MuteAudio = false,
                            IsolateBrowserInstances = false,
                            EnableDevTools = false
                        }
                    }
                }
            };
        }

        public static WorkspaceConfiguration FromProperties(IDictionary<string, string> properties)
        {
            if (properties == null)
            {
                return null;
            }

            if (!properties.TryGetValue(WorkspaceConfigKey, out var json) ||
                string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return Deserialize(json);
        }

        public static WorkspaceConfiguration Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(WorkspaceConfiguration));
                    return serializer.ReadObject(stream) as WorkspaceConfiguration;
                }
            }
            catch
            {
                return null;
            }
        }

        public string Serialize()
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(WorkspaceConfiguration));
                serializer.WriteObject(stream, this);
                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public WorkspaceItemSettings GetFirstWebContentSettings()
        {
            if (Items == null || Items.Count == 0)
            {
                return null;
            }

            foreach (var item in Items)
            {
                if (string.Equals(item.Type, "WebContent", StringComparison.OrdinalIgnoreCase))
                {
                    return item.Settings ?? new WorkspaceItemSettings();
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves the raw property dictionary to the first WebContent item's settings, or null
        /// when the workspace carries no usable JSON blob.
        /// </summary>
        internal static WorkspaceItemSettings GetFirstWebContentSettings(IDictionary<string, string> properties)
        {
            var config = FromProperties(properties);
            return config != null ? config.GetFirstWebContentSettings() : null;
        }

        /// <summary>
        /// Classifies why the raw workspace properties do not yield a usable browser home address
        /// (ticket #9). When the JSON blob is present it is authoritative: a corrupt blob is
        /// reported even if legacy properties could rescue rendering, and a blob without a
        /// home-addressed WebContent item is a missing browser config. Only a definition without
        /// any blob falls back to the legacy HomeAddress/Address properties.
        /// </summary>
        internal static WorkspaceConfigError DetectError(IDictionary<string, string> properties)
        {
            if (properties == null)
            {
                return WorkspaceConfigError.MissingBrowserConfig;
            }

            var json = WebContentConfiguration.GetString(properties, null, WorkspaceConfigKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var config = Deserialize(json);
                if (config == null)
                {
                    return WorkspaceConfigError.MalformedJson;
                }

                var settings = config.GetFirstWebContentSettings();
                if (settings == null || string.IsNullOrWhiteSpace(settings.HomeAddress))
                {
                    return WorkspaceConfigError.MissingBrowserConfig;
                }

                return WorkspaceConfigError.None;
            }

            var legacyHome = WebContentConfiguration.GetString(properties, string.Empty, "HomeAddress", "Address");
            return string.IsNullOrWhiteSpace(legacyHome) ? WorkspaceConfigError.MissingBrowserConfig : WorkspaceConfigError.None;
        }

        /// <summary>
        /// True when the workspace is a freshly created, never-completed placeholder: it still
        /// carries the <see cref="NewWorkspacePlaceholderName"/> default name and its definition
        /// has no usable home address. Admin pages must not present this state as a broken saved
        /// configuration, because the user never had a chance to save the values first.
        /// </summary>
        internal static bool IsUnfinishedNewWorkspace(string itemName, IDictionary<string, string> properties)
        {
            return string.Equals(itemName, NewWorkspacePlaceholderName, StringComparison.Ordinal)
                   && DetectError(properties) != WorkspaceConfigError.None;
        }
    }

    [DataContract]
    internal class WorkspaceItemConfiguration
    {
        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "position")]
        public int Position { get; set; }

        [DataMember(Name = "settings")]
        public WorkspaceItemSettings Settings { get; set; }
    }

    [DataContract]
    internal class WorkspaceItemSettings
    {
        [DataMember(Name = "homeAddress")]
        public string HomeAddress { get; set; }
        [DataMember(Name = "hideToolbar")]
        public bool HideToolbar { get; set; }

        [DataMember(Name = "hideAddress")]
        public bool HideAddress { get; set; }

        [DataMember(Name = "zoomLevel")]
        public double ZoomLevel { get; set; } = WebContentConfiguration.DefaultZoomLevel;

        [DataMember(Name = "rememberLastAddress")]
        public bool RememberLastAddress { get; set; }

        [DataMember(Name = "clearBrowsingDataOnExit")]
        public bool ClearBrowsingDataOnExit { get; set; }

        [DataMember(Name = "allowBackgroundBrowser")]
        public bool AllowBackgroundBrowser { get; set; } = true;

        [DataMember(Name = "allowAudioNotifications")]
        public bool AllowAudioNotifications { get; set; } = true;

        [DataMember(Name = "muteAudio")]
        public bool MuteAudio { get; set; }

        [DataMember(Name = "isolateBrowserInstances")]
        public bool IsolateBrowserInstances { get; set; }

        [DataMember(Name = "enableDevTools")]
        public bool EnableDevTools { get; set; }

        /// <summary>
        /// Ticket #52: server-side proxy toggle for this workspace. Absent from definitions saved
        /// before the proxy existed, which deserializes to false - proxy off.
        /// </summary>
        [DataMember(Name = "proxyEnabled")]
        public bool ProxyEnabled { get; set; }

        /// <summary>
        /// Ticket #52: targets the proxy is permitted to reach (scheme, host, port). Null for
        /// definitions that predate the proxy or where the admin added none.
        /// </summary>
        [DataMember(Name = "proxyTargets")]
        public List<ProxyTargetConfiguration> ProxyTargets { get; set; }

        /// <summary>
        /// Ticket #65: admin-defined rule-trigger rows for this workspace. Null for definitions
        /// saved before #65 (readers are tolerant of the section's absence) or where the admin
        /// added none — no rows means the workspace's proxy traffic never raises events.
        /// </summary>
        [DataMember(Name = "proxyTriggers", EmitDefaultValue = false)]
        public List<ProxyTriggerConfiguration> ProxyTriggers { get; set; }

        /// <summary>
        /// Ticket #85: the workspace's maximum proxied fetch-mode response size in bytes. Absent
        /// from every definition saved before #85 and from saves that kept the default, which
        /// deserialize to 0 — the Event Server then applies
        /// <see cref="ProxyFetchLimits.DefaultMaxResponseBytes"/>. Whatever the stored value
        /// claims, readers clamp it to <see cref="ProxyFetchLimits.MaxAllowedResponseBytes"/>;
        /// this raw member must never decide a cap on its own.
        /// </summary>
        [DataMember(Name = "proxyMaxResponseBytes", EmitDefaultValue = false)]
        public int ProxyMaxResponseBytes { get; set; }
    }

    [DataContract]
    internal class ProxyTargetConfiguration
    {
        [DataMember(Name = "scheme")]
        public string Scheme { get; set; }

        /// <summary>
        /// Legacy (#52) exact-host member. Since #73 writers emit <see cref="Pattern"/> instead,
        /// but readers accept this shape forever - stored workspaces are never migrated
        /// write-back, and mixed versions must keep loading.
        /// </summary>
        [DataMember(Name = "host", EmitDefaultValue = false)]
        public string Host { get; set; }

        /// <summary>
        /// #73: the structured host pattern - an exact lower-case host, a leftmost-label wildcard
        /// ("*.foo.bar.com"), or the lone full-host hatch ("*"). The new stored shape; readers
        /// prefer it over <see cref="Host"/> when both are somehow present.
        /// </summary>
        [DataMember(Name = "pattern", EmitDefaultValue = false)]
        public string Pattern { get; set; }

        [DataMember(Name = "port")]
        public int Port { get; set; }

        /// <summary>
        /// Ticket #56: the admin's per-target "accept untrusted certificate" opt-in. Absent from
        /// definitions saved before #56, which deserializes to false - the Event Server fetcher
        /// validates that target's TLS chain normally.
        /// </summary>
        [DataMember(Name = "acceptUntrusted")]
        public bool AcceptUntrusted { get; set; }

        /// <summary>
        /// The host part to canonicalize: the #73 pattern when the record carries one, else the
        /// legacy exact host. This is the one reader seam every gate and seeder goes through,
        /// which is what keeps old and new stored shapes equivalent.
        /// </summary>
        internal string EffectivePattern => !string.IsNullOrWhiteSpace(Pattern) ? Pattern : Host;
    }

    /// <summary>
    /// Ticket #65: one admin-defined proxy rule-trigger row stored in the workspace's
    /// <c>proxyTriggers</c> section. Raw, unvalidated text on the wire — the Event Server's
    /// <see cref="ProxyTriggerPolicy"/> is the single grammar authority on every read, and the
    /// #78 MC grid must validate against the same policy before saving. A reader must never
    /// decide match semantics from this shape alone.
    /// </summary>
    [DataContract]
    internal class ProxyTriggerConfiguration
    {
        /// <summary>Full-URL glob: scheme://host[:port][/path-glob], wildcards per grammar.</summary>
        [DataMember(Name = "url")]
        public string Url { get; set; }

        /// <summary>Single uppercase verb, or absent/"*" for any method.</summary>
        [DataMember(Name = "method", EmitDefaultValue = false)]
        public string Method { get; set; }

        /// <summary>"1xx".."5xx" class or an exact code, or absent for any response status.</summary>
        [DataMember(Name = "status", EmitDefaultValue = false)]
        public string Status { get; set; }
    }
}
