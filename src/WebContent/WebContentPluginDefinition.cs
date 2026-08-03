using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using VideoOS.Platform;
using VideoOS.Platform.Admin;
using VideoOS.Platform.Client;
using VideoOS.Platform.License;
using VideoOS.Platform.Messaging;
using WebContent.Admin;
using WebContent.Client;

namespace WebContent
{
    public class WebContentPluginDefinition : PluginDefinition
    {
        internal static List<Task> Tasks = new List<Task>();

        internal static Guid PluginId = new Guid(Resources.PluginId);

        internal static Guid WorkspaceKind = new Guid("ba41d989-23e5-4095-ab69-0ebade554aed");

        private readonly object _workspaceSync = new object();
        private readonly List<object> _workspaceMessageRegistrations = new List<object>();
        private readonly List<WorkSpacePlugin> _workSpacePlugins = new List<WorkSpacePlugin>();

        private DateTime _nextWorkspaceRetryUtc = DateTime.MinValue;
        private DateTime _lastWorkspaceFailureLogUtc = DateTime.MinValue;
        private int _workspaceLoadFailureCount;
        private int _emptyWorkspaceLoadCount;
        private bool _workspaceLoadSucceeded;
        private Timer _workspaceRetryTimer;

        public override Guid Id => PluginId;

        public override string Name => Resources.PluginName;

        public override Image Icon => ResourceLoader.CompanyLogo;

        public override string Manufacturer => Resources.PluginCompany;

        private string _versionString;
        public override string VersionString
        {
            get
            {
                if (_versionString == null)
                {
                    _versionString = FileVersionInfo.GetVersionInfo(GetType().Assembly.Location).ProductVersion;
                }

                return _versionString;
            }
        }

        public override AdminPlacementHint AdminPlacementHint => AdminPlacementHint.Default;

        public override List<ViewItemPlugin> ViewItemPlugins { get; } = new List<ViewItemPlugin>();

        public override List<WorkSpacePlugin> WorkSpacePlugins
        {
            get
            {
                EnsureWorkspacesLoaded(force: false, reason: "WorkSpacePlugins property");
                return _workSpacePlugins;
            }
        }

        public override Collection<LicenseInformation> PluginLicenseRequest => SiteLicenseHandler.GetLicenseRequest();

        public override bool IncludeInExport => true;

        public List<ItemNode> RootItemChildNodes { get; private set; } = new List<ItemNode>();

        public override ExportManager GenerateExportManager(ExportParameters exportParameters) => new WebContentExportManager(exportParameters);

        public override string SharedNodeName => Resources.PluginCompany;

        public override Guid SharedNodeId => new Guid(Resources.SharedNodeId);

        private List<ItemNode> _itemNodes;

        public override List<ItemNode> ItemNodes
        {
            get
            {
                if (_itemNodes != null) return _itemNodes;
                var securityActions = new List<SecurityAction>
                {
                    new SecurityAction("GENERIC_READ", "View Web Content Workspace"),
                    new SecurityAction("GENERIC_WRITE", "Manage Web Content Workspace")
                };
                var itemNode = new ItemNode(
                    WorkspaceKind,
                    Guid.Empty,
                    "Web Content Workspace", Icon,
                    "Web Content Workspaces", Icon,
                    Category.Text, true,
                    ItemsAllowed.Many,
                    new WorkspaceItemManager(WorkspaceKind),
                    null,
                    securityActions,
                    null,
                    null,
                    PlacementHint.Root);
                itemNode.SortKey = 500;
                _itemNodes = new List<ItemNode> { itemNode };
                return _itemNodes;
            }
        }

        public override void Init()
        {
            SiteLicenseHandler.Init();

            switch (EnvironmentManager.Instance.EnvironmentType)
            {
                case EnvironmentType.Administration:
                    break;
                case EnvironmentType.SmartClient:
                    ViewItemPlugins.Add(new WebContentViewItemPlugin());
                    RegisterWorkspaceRecoveryReceivers();
                    StartWorkspaceRetryTimer();
                    EnsureWorkspacesLoaded(force: false, reason: "Smart Client Init");
                    break;
                case EnvironmentType.Standalone:
                case EnvironmentType.Service:
                case EnvironmentType.ManagementServer:
                default:
                    break;
            }
        }

        public override async void Close()
        {
            SiteLicenseHandler.Close();
            StopWorkspaceRetryTimer();
            UnregisterWorkspaceRecoveryReceivers();

            ViewItemPlugins.Clear();
            lock (_workspaceSync)
            {
                _workSpacePlugins.Clear();
                _workspaceLoadSucceeded = false;
                _workspaceLoadFailureCount = 0;
                _emptyWorkspaceLoadCount = 0;
                _nextWorkspaceRetryUtc = DateTime.MinValue;
            }

            foreach (var task in Tasks)
            {
                try
                {
                    await task;
                }
                catch (Exception ex)
                {
                    EnvironmentManager.Instance.ExceptionHandler(nameof(Close), ex);
                }
            }
        }

        private void RegisterWorkspaceRecoveryReceivers()
        {
            _workspaceMessageRegistrations.Add(EnvironmentManager.Instance.RegisterReceiver(
                WorkspaceConfigurationMessageReceiver,
                new MessageIdFilter(MessageId.Server.ConfigurationChangedIndication)));

            _workspaceMessageRegistrations.Add(EnvironmentManager.Instance.RegisterReceiver(
                WorkspaceConfigurationMessageReceiver,
                new MessageIdFilter(MessageId.System.SystemConfigurationChangedIndication)));

            _workspaceMessageRegistrations.Add(EnvironmentManager.Instance.RegisterReceiver(
                WorkspaceConfigurationMessageReceiver,
                new MessageIdFilter(MessageId.SmartClient.ShownWorkSpaceChangedIndication)));
        }

        private void UnregisterWorkspaceRecoveryReceivers()
        {
            foreach (var registration in _workspaceMessageRegistrations)
            {
                try
                {
                    EnvironmentManager.Instance.UnRegisterReceiver(registration);
                }
                catch (Exception ex)
                {
                    EnvironmentManager.Instance.ExceptionHandler(nameof(UnregisterWorkspaceRecoveryReceivers), ex);
                }
            }

            _workspaceMessageRegistrations.Clear();
        }

        private object WorkspaceConfigurationMessageReceiver(Message message, FQID destination, FQID sender)
        {
            EnsureWorkspacesLoaded(force: false, reason: $"message:{message?.MessageId}");
            return null;
        }

        private void StartWorkspaceRetryTimer()
        {
            if (_workspaceRetryTimer != null)
            {
                return;
            }

            _workspaceRetryTimer = new Timer(WorkspaceRetryTimerCallback, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        private void StopWorkspaceRetryTimer()
        {
            var timer = _workspaceRetryTimer;
            if (timer == null)
            {
                return;
            }

            _workspaceRetryTimer = null;
            timer.Dispose();
        }

        private void WorkspaceRetryTimerCallback(object state)
        {
            EnsureWorkspacesLoaded(force: false, reason: "retry-timer");
        }

        private void EnsureWorkspacesLoaded(bool force, string reason)
        {
            if (EnvironmentManager.Instance.EnvironmentType != EnvironmentType.SmartClient)
            {
                return;
            }

            lock (_workspaceSync)
            {
                if (_workspaceLoadSucceeded)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                if (!force && now < _nextWorkspaceRetryUtc)
                {
                    return;
                }

                if (!WorkspaceItemManager.TryGetWorkspaces(out var workspaces, out var error))
                {
                    _workspaceLoadFailureCount++;
                    var retryDelay = GetRetryDelay(_workspaceLoadFailureCount);
                    _nextWorkspaceRetryUtc = now.Add(retryDelay);
                    LogWorkspaceLoadFailure(reason, error, retryDelay);
                    return;
                }

                if (workspaces.Count == 0 && !_workspaceLoadSucceeded && _emptyWorkspaceLoadCount < 3)
                {
                    _emptyWorkspaceLoadCount++;
                    _nextWorkspaceRetryUtc = now.Add(TimeSpan.FromSeconds(15));
                    if (_emptyWorkspaceLoadCount == 1)
                    {
                        EnvironmentManager.Instance.Log(false, nameof(WebContentPluginDefinition), "Workspace configuration returned zero entries during startup; scheduling retry.");
                    }

                    return;
                }

                _workSpacePlugins.Clear();
                foreach (var workspace in workspaces)
                {
                    var properties = BuildWorkspaceProperties(workspace);
                    _workSpacePlugins.Add(new WebContentWorkSpacePlugin(
                        workspace.FQID.ObjectId,
                        workspace.Name,
                        properties));
                }

                if (_workspaceLoadFailureCount > 0)
                {
                    EnvironmentManager.Instance.Log(false, nameof(WebContentPluginDefinition), $"Recovered workspace provisioning and loaded {_workSpacePlugins.Count} workspaces.");
                }

                _workspaceLoadFailureCount = 0;
                _emptyWorkspaceLoadCount = 0;
                _workspaceLoadSucceeded = true;
                _nextWorkspaceRetryUtc = DateTime.MinValue;
            }
        }

        private void LogWorkspaceLoadFailure(string reason, Exception error, TimeSpan retryDelay)
        {
            var now = DateTime.UtcNow;
            var shouldLog = _workspaceLoadFailureCount == 1 || now - _lastWorkspaceFailureLogUtc >= TimeSpan.FromMinutes(1);
            if (!shouldLog)
            {
                return;
            }

            var errorText = error == null ? "Unknown error." : error.Message;
            EnvironmentManager.Instance.Log(
                true,
                nameof(WebContentPluginDefinition),
                $"Workspace provisioning attempt failed (reason={reason}, attempts={_workspaceLoadFailureCount}, retry={retryDelay.TotalSeconds:F0}s). {errorText}");
            _lastWorkspaceFailureLogUtc = now;
        }

        private static TimeSpan GetRetryDelay(int failureCount)
        {
            var cappedFailures = Math.Min(Math.Max(failureCount, 1), 6);
            return TimeSpan.FromSeconds(Math.Pow(2, cappedFailures));
        }

        private static Dictionary<string, string> BuildWorkspaceProperties(Item workspace)
        {
            var properties = new Dictionary<string, string>
            {
                { "WorkspaceId", workspace.FQID.ObjectId.ToString() },
                { "WorkspaceName", workspace.Name }
            };

            var sourceProperties = workspace.Properties;

            var workspaceConfig = WorkspaceConfiguration.FromProperties(sourceProperties);
            if (workspaceConfig != null)
            {
                var settings = workspaceConfig.GetFirstWebContentSettings();
                if (settings != null)
                {
                    properties[nameof(WebContentSettings.InstanceId)] = workspace.FQID.ObjectId.ToString();
                    properties[nameof(WebContentSettings.HomeAddress)] = WebContentConfiguration.NormalizeHomeAddress(settings.HomeAddress);
                    properties[nameof(WebContentSettings.HideToolbar)] = WebContentConfiguration.FormatBoolean(settings.HideToolbar);

                    var effectiveHideAddress = settings.HideToolbar ? false : settings.HideAddress;
                    properties[nameof(WebContentSettings.HideAddress)] = WebContentConfiguration.FormatBoolean(effectiveHideAddress);
                    properties[nameof(WebContentSettings.ZoomLevel)] = WebContentConfiguration.FormatZoomLevel(settings.ZoomLevel);
                    properties[nameof(WebContentSettings.RememberLastAddress)] = WebContentConfiguration.FormatBoolean(settings.RememberLastAddress);
                    properties[nameof(WebContentSettings.ClearBrowsingDataOnExit)] = WebContentConfiguration.FormatBoolean(settings.ClearBrowsingDataOnExit);
                    properties[nameof(WebContentSettings.AllowBackgroundBrowser)] = WebContentConfiguration.FormatBoolean(settings.AllowBackgroundBrowser);
                    properties[nameof(WebContentSettings.AllowAudioNotifications)] = WebContentConfiguration.FormatBoolean(settings.AllowAudioNotifications);
                    properties[nameof(WebContentSettings.MuteAudio)] = WebContentConfiguration.FormatBoolean(settings.MuteAudio);
                    properties[nameof(WebContentSettings.IsolateBrowserInstances)] = WebContentConfiguration.FormatBoolean(settings.IsolateBrowserInstances);
                    properties[nameof(WebContentSettings.EnableDevTools)] = WebContentConfiguration.FormatBoolean(settings.EnableDevTools);

                    return properties;
                }
            }

            var homeAddress = WebContentConfiguration.NormalizeHomeAddress(
                WebContentConfiguration.GetString(sourceProperties, Strings.HomeAddress, "HomeAddress", "Address"));

            properties[nameof(WebContentSettings.InstanceId)] = WebContentConfiguration.GetGuid(sourceProperties, workspace.FQID.ObjectId, nameof(WebContentSettings.InstanceId)).ToString();
            properties[nameof(WebContentSettings.HomeAddress)] = homeAddress;
            properties[nameof(WebContentSettings.HideToolbar)] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, nameof(WebContentSettings.HideToolbar)));

            var hideAddress = WebContentConfiguration.GetBoolean(sourceProperties, false, nameof(WebContentSettings.HideAddress));
            if (WebContentConfiguration.GetBoolean(sourceProperties, false, nameof(WebContentSettings.HideToolbar)))
            {
                hideAddress = false;
            }

            properties[nameof(WebContentSettings.HideAddress)] = WebContentConfiguration.FormatBoolean(hideAddress);
            properties[nameof(WebContentSettings.ZoomLevel)] = WebContentConfiguration.FormatZoomLevel(
                WebContentConfiguration.GetZoomLevel(sourceProperties, WebContentConfiguration.DefaultZoomLevel, nameof(WebContentSettings.ZoomLevel)));
            properties[nameof(WebContentSettings.RememberLastAddress)] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, nameof(WebContentSettings.RememberLastAddress)));
            properties[nameof(WebContentSettings.ClearBrowsingDataOnExit)] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, nameof(WebContentSettings.ClearBrowsingDataOnExit)));
            properties[nameof(WebContentSettings.AllowBackgroundBrowser)] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, true, nameof(WebContentSettings.AllowBackgroundBrowser)));
            properties[nameof(WebContentSettings.AllowAudioNotifications)] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, true, nameof(WebContentSettings.AllowAudioNotifications)));
            properties[nameof(WebContentSettings.MuteAudio)] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, nameof(WebContentSettings.MuteAudio)));
            properties[nameof(WebContentSettings.IsolateBrowserInstances)] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, nameof(WebContentSettings.IsolateBrowserInstances)));
            properties[nameof(WebContentSettings.EnableDevTools)] = WebContentConfiguration.FormatBoolean(
                WebContentConfiguration.GetBoolean(sourceProperties, false, nameof(WebContentSettings.EnableDevTools)));

            return properties;
        }
    }
}
