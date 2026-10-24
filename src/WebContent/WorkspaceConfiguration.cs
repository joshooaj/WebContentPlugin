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
    }
}
