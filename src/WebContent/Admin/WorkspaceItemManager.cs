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
            if (_userControl == null) return;
            _userControl.ClearUserControl();
            // #74/#84: no item context after a clear, so the Status/Diagnostics role gate cannot
            // grant anything.
            _userControl.SetStatusTabVisible(false);
            _userControl.SetDiagnosticsTabVisible(false);
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

            // #74 role gate: the Status tab renders only when this user holds the workspace's
            // Manage security action; every denial or failed lookup hides the whole tab.
            var managePermission = WorkspaceManagePermissionEvaluator.Check(item);
            _userControl.SetStatusTabVisible(WorkspaceTabPolicy.ShowStatusTab(managePermission));

            // #84: the Diagnostics tab rides the same gate - status-derived data plus the one
            // allowlist-writing affordance, both for edit-rights holders only.
            _userControl.SetDiagnosticsTabVisible(WorkspaceTabPolicy.ShowDiagnosticsTab(managePermission));

            // #76: the Status tab queries the Event Server for this item's live sessions, and the
            // id has to travel in the same form the Smart Client reports blocks under.
            _userControl.SetWorkspaceItemId(item?.FQID != null ? item.FQID.ObjectId.ToString() : null);

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
                    // #85: the cap field fills from the stored wire value (0 = never stored),
                    // displayed in whole MB inside the admin range; the server-side clamp keeps
                    // even a hand-edited huge value honest.
                    _userControl.ProxyResponseCapText =
                        WebContentConfiguration.ResolveProxyResponseCapMegabytes(settings.ProxyMaxResponseBytes)
                            .ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                    // #81: the structured grid renders stored entries from either shape (legacy
                    // host, structured pattern) forever; records too corrupt to render are
                    // dropped here exactly as the old text-box fill dropped them - with a log line.
                    var droppedStoredTargets = _userControl.LoadProxyTargets(settings.ProxyTargets);
                    if (droppedStoredTargets > 0)
                    {
                        EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Workspace '{item.Name}' (FQID: {item.FQID}) has {droppedStoredTargets} stored proxy target record(s) too malformed to render; they are not shown and a Save will rewrite the definition without them.");
                    }
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
            _userControl.ProxyResponseCapText = WebContentConfiguration.DefaultProxyResponseCapMegabytes.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            _userControl.LoadProxyTargets(null);
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
            // #81: the save reads the structured grid rows, not free text. An invalid row
            // refuses the save by name (the grid already highlights it); duplicates collapse
            // onto one canonical record with any flagged duplicate flagging the entry; and an
            // enabled proxy with no targets at all stays refused, exactly as the old text-box
            // path had it. A disabled proxy keeps its targets parsed and stored anyway -
            // turning the proxy off must not erase the allowlist (MC-9 in the verification matrix).
            if (!ProxyAllowlistEditor.TryBuildStoredTargets(
                    _userControl?.ProxyTargetRows ?? new List<ProxyTargetEditorRow>(),
                    out var proxyTargets,
                    out var rejectedTargetRows))
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): invalid proxy target row(s): {string.Join(" | ", rejectedTargetRows)}.");
                MessageBox.Show(string.Format(Strings.InvalidProxyTargetMessage, string.Join("; ", rejectedTargetRows)), Strings.InvalidAddressTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (proxyEnabled && proxyTargets.Count == 0)
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): proxy enabled but no target allowlisted.");
                MessageBox.Show(Strings.MissingProxyTargetsMessage, Strings.InvalidAddressTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // #85: the per-workspace response-size cap validated from the Proxy tab's whole-MB
            // field. An out-of-range or non-numeric entry refuses the save by name - the page
            // never silently rewrites what the admin typed. The Event Server clamps stored
            // values server-side regardless, so this UI is convenience, not the enforcement.
            if (!WebContentConfiguration.TryParseProxyResponseCapMegabytes(_userControl?.ProxyResponseCapText, out var capMegabytes, out var rejectedCapText))
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): invalid proxy response cap '{rejectedCapText}'.");
                MessageBox.Show(string.Format(Strings.InvalidProxyCapMessage, rejectedCapText), Strings.InvalidAddressTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // What the grid actually stored, in the admin's own entry shape. The MC MIP.log is the
            // storage-side view of the allowlist editor that does not depend on how the WPF grid
            // surfaces through accessibility, so it is what the #81 probe compares against.
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Stored proxy targets for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): routing {(proxyEnabled ? "on" : "off")}; {ProxyAllowlistEditor.DescribeStoredTargets(proxyTargets)}.");

            // The stored cap, again the MC's own storage-side statement for the #85 probe to
            // compare against a reloaded-pane readback.
            var capBytes = WebContentConfiguration.ProxyResponseCapBytes(capMegabytes);
            EnvironmentManager.Instance.Log(
                false,
                nameof(WorkspaceItemManager),
                $"Stored proxy response cap for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): {WebContentConfiguration.ResolveProxyResponseCapMegabytes(capBytes)} MB{(capBytes == 0 ? " (default)" : string.Empty)}.");

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
                // #85: the wire cap in bytes; the default stores 0 so the optional member stays
                // omitted and pre-#85 stored shapes never grow a redundant member.
                settings.ProxyMaxResponseBytes = capBytes;
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
