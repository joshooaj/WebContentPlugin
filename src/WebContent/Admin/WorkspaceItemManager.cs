using System;
using System.Collections.Generic;
using System.Windows.Forms;
using VideoOS.Platform;
using VideoOS.Platform.Admin;
using VideoOS.Platform.Messaging;
using WebContent;
using Message = VideoOS.Platform.Messaging.Message;

namespace WebContent.Admin
{
    public class WorkspaceItemManager : ItemManager
    {
        private readonly Guid _kind;
        private WorkspaceUserControl _userControl;
        private object _messageRegistrationObject;

        public WorkspaceItemManager(Guid kind) : base()
        {
            _kind = kind;
        }

        public override void Init()
        {
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), "Initializing WorkspaceItemManager.");
            EnvironmentManager.Instance.EnableConfigurationChangedService = true;
        }

        public override void Close()
        {
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), "Closing WorkspaceItemManager.");
            if (_messageRegistrationObject != null)
            {
                EnvironmentManager.Instance.UnRegisterReceiver(_messageRegistrationObject);
                _messageRegistrationObject = null;
            }
        }

        private void RefreshTreeView()
        {
            EnvironmentManager.Instance.SendMessage(
                new Message(MessageId.System.ApplicationRefreshTreeViewCommand) { Data = _kind });
        }

        public static List<Item> GetWorkspaces()
        {
            return TryGetWorkspaces(out var workspaces, out _) ? workspaces : new List<Item>();
        }

        public static bool TryGetWorkspaces(out List<Item> workspaces, out Exception error)
        {
            workspaces = new List<Item>();
            error = null;

            try
            {
                var configuration = Configuration.Instance;
                if (configuration == null)
                {
                    error = new InvalidOperationException("Configuration service is not available.");
                    return false;
                }

                var result = configuration.GetItemConfigurations(WebContentPluginDefinition.PluginId, null, WebContentPluginDefinition.WorkspaceKind);
                if (result == null)
                {
                    error = new InvalidOperationException("Workspace configuration is temporarily unavailable.");
                    return false;
                }

                workspaces = result;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        public static Item GetWorkspace(Guid workspaceId)
        {
            return Configuration.Instance.GetItemConfiguration(WebContentPluginDefinition.PluginId, WebContentPluginDefinition.WorkspaceKind, workspaceId);
        }

        public override UserControl GenerateDetailUserControl()
        {
            _userControl = new WorkspaceUserControl();
            _userControl.ConfigurationChangedByUser += ConfigurationChangedByUserHandler;
            return _userControl;
        }

        public override ItemNodeUserControl GenerateOverviewUserControl()
        {
            return new WorkspaceOverviewUserControl();
        }

        public override void ClearUserControl()
        {
            _userControl?.ClearUserControl();
        }

        public override void ReleaseUserControl()
        {
            _userControl = null;
        }

        public override void FillUserControl(Item item)
        {
            if (_userControl == null) return;
            
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Filling UserControl for workspace item: {item.Name} (FQID: {item.FQID}).");
            CurrentItem = item;
            var properties = item?.Properties;
            var configError = WorkspaceConfiguration.DetectError(properties);

            _userControl.WorkspaceName = item.Name;

            var workspaceConfig = WorkspaceConfiguration.FromProperties(properties);
            if (workspaceConfig != null)
            {
                var settings = workspaceConfig.GetFirstWebContentSettings();
                if (settings != null)
                {
                    _userControl.HomeAddress = WebContentConfiguration.NormalizeHomeAddress(settings.HomeAddress);
                    _userControl.RememberLastAddress = settings.RememberLastAddress;
                    _userControl.HideToolbar = settings.HideToolbar;
                    _userControl.HideAddress = settings.HideToolbar ? false : settings.HideAddress;
                    _userControl.ZoomLevel = WebContentConfiguration.ClampZoomLevel(settings.ZoomLevel);
                    _userControl.ClearBrowsingDataOnExit = settings.ClearBrowsingDataOnExit;
                    _userControl.AllowBackgroundBrowser = settings.AllowBackgroundBrowser;
                    _userControl.AllowAudioNotifications = settings.AllowAudioNotifications;
                    _userControl.MuteAudio = settings.MuteAudio;
                    _userControl.IsolateBrowserInstances = settings.IsolateBrowserInstances;
                    _userControl.EnableDevTools = settings.EnableDevTools;
                    _userControl.ProxyEnabled = settings.ProxyEnabled;
                    _userControl.ProxyTargetsText = DescribeProxyTargets(settings.ProxyTargets);
                    _userControl.FillContent();
                    if (configError != WorkspaceConfigError.None)
                    {
                        SignalBrokenConfiguration(item, properties, configError);
                    }
                    else
                    {
                        _userControl.ClearConfigurationError();
                    }
                    return;
                }
            }

            _userControl.HomeAddress = WebContentConfiguration.NormalizeHomeAddress(
                WebContentConfiguration.GetString(properties, Strings.HomeAddress, "Address", "HomeAddress"));
            _userControl.RememberLastAddress = WebContentConfiguration.GetBoolean(properties, false, "RememberLastAddress");
            _userControl.HideToolbar = WebContentConfiguration.GetBoolean(properties, false, "HideToolbar");
            _userControl.HideAddress = WebContentConfiguration.GetBoolean(properties, false, "HideAddress") && !_userControl.HideToolbar;
            _userControl.ZoomLevel = WebContentConfiguration.GetZoomLevel(properties, WebContentConfiguration.DefaultZoomLevel, "ZoomLevel");
            _userControl.ClearBrowsingDataOnExit = WebContentConfiguration.GetBoolean(properties, false, "ClearBrowsingDataOnExit");
            _userControl.AllowBackgroundBrowser = WebContentConfiguration.GetBoolean(properties, true, "AllowBackgroundBrowser");
            _userControl.AllowAudioNotifications = WebContentConfiguration.GetBoolean(properties, true, "AllowAudioNotifications");
            _userControl.MuteAudio = WebContentConfiguration.GetBoolean(properties, false, "MuteAudio");
            _userControl.IsolateBrowserInstances = WebContentConfiguration.GetBoolean(properties, false, "IsolateBrowserInstances");
            _userControl.EnableDevTools = WebContentConfiguration.GetBoolean(properties, false, "EnableDevTools");
            _userControl.ProxyEnabled = false;
            _userControl.ProxyTargetsText = string.Empty;
            _userControl.FillContent();

            if (configError != WorkspaceConfigError.None)
            {
                SignalBrokenConfiguration(item, properties, configError);
            }
            else
            {
                _userControl.ClearConfigurationError();
            }
        }

        /// <summary>
        /// Ticket #27: a definition that failed detection loads with fallback values that look
        /// healthy, so the admin page must say so. The banner names the defect and the page is
        /// marked dirty after the fill completes (the shell resets dirty state synchronously),
        /// enabling Save to rewrite a clean definition from what the admin sees.
        /// One state is not a defect: the placeholder item left behind by <see cref="CreateItem"/>
        /// never has a home address yet, so it loads with the banner suppressed — still dirty, so
        /// Save stays enabled while the user completes the new workspace.
        /// </summary>
        private void SignalBrokenConfiguration(Item item, IDictionary<string, string> properties, WorkspaceConfigError configError)
        {
            if (WorkspaceConfiguration.IsUnfinishedNewWorkspace(item?.Name, properties))
            {
                _userControl?.SignalUnfinishedNewItem();
                EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Workspace '{item?.Name}' (FQID: {item?.FQID}) is a new placeholder nobody has saved yet; no configuration-error banner is shown, but the page stays dirty so Save can complete the definition.");
                return;
            }

            _userControl?.SignalLoadDefect(string.Format(Strings.WorkspaceConfigErrorAdminBanner, WebContent.Client.WorkspaceConfigErrorText.Describe(configError)));
            EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Workspace '{item?.Name}' loaded with a configuration error ({configError}); the page shows a warning and marks itself dirty so Save can rewrite a clean definition.");
        }

        public override string GetItemName()
        {
            return _userControl?.WorkspaceName ?? string.Empty;
        }

        public override void SetItemName(string name)
        {
            if (_userControl != null)
            {
                _userControl.WorkspaceName = name;
            }
        }

        public override bool ValidateAndSaveUserControl()
        {
            if (CurrentItem == null) return false;
            
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Saving workspace configuration for item: {CurrentItem.Name} (FQID: {CurrentItem.FQID}).");

            var name = _userControl?.WorkspaceName;
            if (WorkspaceNameValidation.IsEmpty(name))
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace (FQID: {CurrentItem.FQID}): the workspace name is empty.");
                MessageBox.Show(Strings.MissingNameMessage, Strings.MissingNameTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            CurrentItem.Name = name;

            var rawHomeAddress = _userControl?.HomeAddress;
            if (string.IsNullOrWhiteSpace(rawHomeAddress))
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): home address is empty.");
                MessageBox.Show(Strings.MissingHomeAddressMessage, Strings.InvalidAddressTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (!WebContentConfiguration.TryCreateNavigationUri(rawHomeAddress, out var homeAddressUri))
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): invalid home address '{rawHomeAddress}'.");
                MessageBox.Show(string.Format(Strings.InvalidAddressMessage, rawHomeAddress), Strings.InvalidAddressTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var homeAddress = homeAddressUri.AbsoluteUri;
            var hideToolbar = _userControl?.HideToolbar ?? false;
            var hideAddress = !hideToolbar && (_userControl?.HideAddress ?? false);

            var proxyEnabled = _userControl?.ProxyEnabled ?? false;
            List<ProxyTargetConfiguration> proxyTargets = null;
            if (proxyEnabled)
            {
                if (!TryBuildProxyTargets(_userControl.ProxyTargetsText, out proxyTargets))
                {
                    return false;
                }
            }

            SetOrRemoveProperty(CurrentItem, "Address", homeAddress);
            SetOrRemoveProperty(CurrentItem, "HomeAddress", homeAddress);
            SetOrRemoveProperty(CurrentItem, "RememberLastAddress", WebContentConfiguration.FormatBoolean(_userControl?.RememberLastAddress ?? false));
            SetOrRemoveProperty(CurrentItem, "HideToolbar", WebContentConfiguration.FormatBoolean(hideToolbar));
            SetOrRemoveProperty(CurrentItem, "HideAddress", WebContentConfiguration.FormatBoolean(hideAddress));
            SetOrRemoveProperty(CurrentItem, "ZoomLevel", WebContentConfiguration.FormatZoomLevel(_userControl?.ZoomLevel ?? WebContentConfiguration.DefaultZoomLevel));
            SetOrRemoveProperty(CurrentItem, "ClearBrowsingDataOnExit", WebContentConfiguration.FormatBoolean(_userControl?.ClearBrowsingDataOnExit ?? false));
            SetOrRemoveProperty(CurrentItem, "AllowBackgroundBrowser", WebContentConfiguration.FormatBoolean(_userControl?.AllowBackgroundBrowser ?? true));
            SetOrRemoveProperty(CurrentItem, "AllowAudioNotifications", WebContentConfiguration.FormatBoolean(_userControl?.AllowAudioNotifications ?? true));
            SetOrRemoveProperty(CurrentItem, "MuteAudio", WebContentConfiguration.FormatBoolean(_userControl?.MuteAudio ?? false));
            SetOrRemoveProperty(CurrentItem, "IsolateBrowserInstances", WebContentConfiguration.FormatBoolean(_userControl?.IsolateBrowserInstances ?? false));
            SetOrRemoveProperty(CurrentItem, "EnableDevTools", WebContentConfiguration.FormatBoolean(_userControl?.EnableDevTools ?? false));

            var config = WorkspaceConfiguration.CreateDefault(homeAddress);
            if (config.Items != null && config.Items.Count > 0)
            {
                var settings = config.Items[0].Settings;
                settings.HideToolbar = hideToolbar;
                settings.HideAddress = hideAddress;
                settings.ZoomLevel = WebContentConfiguration.ClampZoomLevel(_userControl?.ZoomLevel ?? WebContentConfiguration.DefaultZoomLevel);
                settings.RememberLastAddress = _userControl?.RememberLastAddress ?? false;
                settings.ClearBrowsingDataOnExit = _userControl?.ClearBrowsingDataOnExit ?? false;
                settings.AllowBackgroundBrowser = _userControl?.AllowBackgroundBrowser ?? true;
                settings.AllowAudioNotifications = _userControl?.AllowAudioNotifications ?? true;
                settings.MuteAudio = _userControl?.MuteAudio ?? false;
                settings.IsolateBrowserInstances = _userControl?.IsolateBrowserInstances ?? false;
                settings.EnableDevTools = _userControl?.EnableDevTools ?? false;
                settings.ProxyEnabled = proxyEnabled;
                settings.ProxyTargets = proxyTargets;
            }

            SetOrRemoveProperty(CurrentItem, WorkspaceConfiguration.WorkspaceConfigKey, config.Serialize());

            Configuration.Instance.SaveItemConfiguration(WebContentPluginDefinition.PluginId, CurrentItem);
            RefreshTreeView();
            return true;
        }

        private void SetOrRemoveProperty(Item item, string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (item.Properties.ContainsKey(key))
                    item.Properties.Remove(key);
            }
            else
            {
                item.Properties[key] = value;
            }
        }

        /// <summary>
        /// Ticket #52: parses the admin's one-origin-per-line proxy targets into canonical
        /// scheme/host/port records. An invalid line rejects the save (same posture as the home
        /// address), duplicates and other overlapping entries collapse onto one record, and an
        /// enabled proxy with no targets at all is rejected so the toggle can never mean
        /// "allow nothing" by accident.
        /// </summary>
        private bool TryBuildProxyTargets(string targetsText, out List<ProxyTargetConfiguration> targets)
        {
            targets = new List<ProxyTargetConfiguration>();
            var seenOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in (targetsText ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (!ProxyPolicy.TryNormalizeTarget(line, out var origin))
                {
                    EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): invalid proxy target '{line}'.");
                    MessageBox.Show(string.Format(Strings.InvalidProxyTargetMessage, line), Strings.InvalidAddressTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                if (!seenOrigins.Add(origin))
                {
                    continue;
                }

                var originUri = new Uri(origin);
                targets.Add(new ProxyTargetConfiguration
                {
                    Scheme = originUri.Scheme,
                    Host = originUri.Host,
                    Port = originUri.Port
                });
            }

            if (targets.Count == 0)
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): proxy enabled but no target allowlisted.");
                MessageBox.Show(Strings.MissingProxyTargetsMessage, Strings.InvalidAddressTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Renders stored proxy target records back as canonical one-per-line text for the admin
        /// page; invalid stored records are dropped rather than shown.
        /// </summary>
        private static string DescribeProxyTargets(List<ProxyTargetConfiguration> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                return string.Empty;
            }

            var lines = new List<string>();
            foreach (var target in targets)
            {
                if (target != null && ProxyPolicy.TryNormalizeTargetParts(target.Scheme, target.Host, target.Port, out var origin))
                {
                    lines.Add(origin);
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        public override Item CreateItem(Item parentItem, FQID suggestedFQID)
        {
            var fqid = new FQID(Configuration.Instance.ServerFQID.ServerId)
            {
                ObjectId = Guid.NewGuid(),
                Kind = WebContentPluginDefinition.WorkspaceKind
            };

            CurrentItem = new Item(fqid, WorkspaceConfiguration.NewWorkspacePlaceholderName);
            var defaultConfig = WorkspaceConfiguration.CreateDefault(string.Empty);
            SetOrRemoveProperty(CurrentItem, WorkspaceConfiguration.WorkspaceConfigKey, defaultConfig.Serialize());
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Creating new workspace item. FQID: {fqid}.");
            Configuration.Instance.SaveItemConfiguration(WebContentPluginDefinition.PluginId, CurrentItem);
            RefreshTreeView();

            return CurrentItem;
        }

        public override void DeleteItem(Item item)
        {
            if (item == null) return;
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Deleting workspace item: {item.Name} (FQID: {item.FQID}).");
            Configuration.Instance.DeleteItemConfiguration(WebContentPluginDefinition.PluginId, item);
            RefreshTreeView();
        }

        public override List<Item> GetItems()
        {
            return Configuration.Instance.GetItemConfigurations(WebContentPluginDefinition.PluginId, null, WebContentPluginDefinition.WorkspaceKind);
        }

        public override List<Item> GetItems(Item parentItem)
        {
            return Configuration.Instance.GetItemConfigurations(WebContentPluginDefinition.PluginId, parentItem, WebContentPluginDefinition.WorkspaceKind);
        }

        public override Item GetItem(FQID fqid)
        {
            return Configuration.Instance.GetItemConfiguration(WebContentPluginDefinition.PluginId, _kind, fqid.ObjectId);
        }
    }
}
