using System;
using System.Collections.Generic;
using System.Linq;

namespace WebContent.Client
{
    /// <summary>
    /// Pure builder that turns an admin-defined workspace into the effective property dictionary
    /// passed to the WebContent view item. Reads the JSON blob first (see
    /// <see cref="WorkspaceConfiguration"/>) and falls back to the legacy Name + Address
    /// properties. Home address normalization, zoom clamping, and the hide-toolbar-forces-
    /// hide-address rule are applied so the Smart Client receives canonical values.
    /// </summary>
    internal static class WorkspaceProperties
    {
        public static Dictionary<string, string> Build(
            Guid workspaceId,
            string workspaceName,
            IDictionary<string, string> sourceProperties,
            string fallbackHomeAddress)
        {
            var properties = new Dictionary<string, string>
            {
                { WorkspaceViewItemSync.WorkspaceIdKey, workspaceId.ToString() },
                { "WorkspaceName", workspaceName },
                { WorkspaceViewItemSync.FingerprintKey, WorkspaceDefinitionFingerprint.Compute(workspaceName, sourceProperties) }
            };

            var settings = WorkspaceConfiguration.GetFirstWebContentSettings(sourceProperties);
            if (settings != null)
            {
                properties["InstanceId"] = workspaceId.ToString();
                properties["HomeAddress"] = WebContentConfiguration.NormalizeHomeAddress(settings.HomeAddress, fallbackHomeAddress);
                properties["HideToolbar"] = WebContentConfiguration.FormatBoolean(settings.HideToolbar);

                var effectiveHideAddress = settings.HideToolbar ? false : settings.HideAddress;
                properties["HideAddress"] = WebContentConfiguration.FormatBoolean(effectiveHideAddress);
                properties["ZoomLevel"] = WebContentConfiguration.FormatZoomLevel(settings.ZoomLevel);
                properties["RememberLastAddress"] = WebContentConfiguration.FormatBoolean(settings.RememberLastAddress);
                properties["ClearBrowsingDataOnExit"] = WebContentConfiguration.FormatBoolean(settings.ClearBrowsingDataOnExit);
                properties["AllowBackgroundBrowser"] = WebContentConfiguration.FormatBoolean(settings.AllowBackgroundBrowser);
                properties["AllowAudioNotifications"] = WebContentConfiguration.FormatBoolean(settings.AllowAudioNotifications);
                properties["MuteAudio"] = WebContentConfiguration.FormatBoolean(settings.MuteAudio);
                properties["IsolateBrowserInstances"] = WebContentConfiguration.FormatBoolean(settings.IsolateBrowserInstances);
                properties["EnableDevTools"] = WebContentConfiguration.FormatBoolean(settings.EnableDevTools);
                properties["ProxyEnabled"] = WebContentConfiguration.FormatBoolean(settings.ProxyEnabled);
                properties["ProxyTargets"] = BuildProxyTargetsProperty(settings.ProxyTargets);

                return properties;
            }

            var homeAddress = WebContentConfiguration.NormalizeHomeAddress(
                WebContentConfiguration.GetString(sourceProperties, string.Empty, "HomeAddress", "Address"),
                fallbackHomeAddress);

            properties["InstanceId"] = WebContentConfiguration.GetGuid(sourceProperties, workspaceId, "InstanceId").ToString();
            properties["HomeAddress"] = homeAddress;
            properties["HideToolbar"] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, "HideToolbar"));

            var hideAddress = WebContentConfiguration.GetBoolean(sourceProperties, false, "HideAddress");
            if (WebContentConfiguration.GetBoolean(sourceProperties, false, "HideToolbar"))
            {
                hideAddress = false;
            }

            properties["HideAddress"] = WebContentConfiguration.FormatBoolean(hideAddress);
            properties["ZoomLevel"] = WebContentConfiguration.FormatZoomLevel(
                WebContentConfiguration.GetZoomLevel(sourceProperties, WebContentConfiguration.DefaultZoomLevel, "ZoomLevel"));
            properties["RememberLastAddress"] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, "RememberLastAddress"));
            properties["ClearBrowsingDataOnExit"] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, "ClearBrowsingDataOnExit"));
            properties["AllowBackgroundBrowser"] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, true, "AllowBackgroundBrowser"));
            properties["AllowAudioNotifications"] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, true, "AllowAudioNotifications"));
            properties["MuteAudio"] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, "MuteAudio"));
            properties["IsolateBrowserInstances"] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, "IsolateBrowserInstances"));
            properties["EnableDevTools"] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, "EnableDevTools"));

            properties["ProxyEnabled"] = WebContentConfiguration.FormatBoolean(false);
            properties["ProxyTargets"] = string.Empty;

            return properties;
        }

        /// <summary>
        /// Ticket #52: flattens the admin proxy allowlist to the canonical, comma-separated
        /// origin list the view item consumes. Invalid entries (bad scheme/host/port) are
        /// dropped; duplicates collapse.
        /// </summary>
        private static string BuildProxyTargetsProperty(List<ProxyTargetConfiguration> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                return string.Empty;
            }

            var canonical = new List<string>();
            foreach (var target in targets)
            {
                if (target == null)
                {
                    continue;
                }

                if (ProxyPolicy.TryNormalizeTargetParts(target.Scheme, target.Host, target.Port, out var origin) &&
                    !canonical.Contains(origin, StringComparer.OrdinalIgnoreCase))
                {
                    canonical.Add(origin);
                }
            }

            return string.Join(",", canonical);
        }
    }
}
