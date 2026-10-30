using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using VideoOS.Platform;
using VideoOS.Platform.Admin;
using VideoOS.Platform.Background;
using VideoOS.Platform.Client;
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
        private readonly WorkspacePluginFactory _workspacePluginFactory = new WorkspacePluginFactory();
        private readonly List<BackgroundPlugin> _backgroundPlugins = new List<BackgroundPlugin>();

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

        public override AdminPlacementHint AdminPlacementHint => AdminPlacementHint.Root;

        public override List<ViewItemPlugin> ViewItemPlugins { get; } = new List<ViewItemPlugin>();

        /// <summary>
        /// Server-side proxy fetcher (#53). The host instantiates background plugins for the
        /// environments declared in TargetEnvironments, so this reaches the Event Server only;
        /// Smart Client and Management Client hosts never run it.
        /// </summary>
        public override List<BackgroundPlugin> BackgroundPlugins => _backgroundPlugins;

        /// <summary>
        /// The classic MIP messaging ids the proxy-fetch exchange uses (#53), registered in the
        /// message id vocabulary so tooling sees them as plugin-owned traffic.
        /// </summary>
        public override List<string> PluginDefinedMessageIds { get; } = new List<string>
        {
            ProxyMessageIds.FetchRequestId,
            ProxyMessageIds.FetchResponseId,
        };

        public override List<WorkSpacePlugin> WorkSpacePlugins
        {
            get
            {
                EnsureWorkspacesLoaded(force: false, reason: "WorkSpacePlugins property");
                return _workSpacePlugins;
            }
        }

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
                    new SecurityAction(WorkspacePermission.View, "View Web Content Workspace"),
                    new SecurityAction(WorkspacePermission.Manage, "Manage Web Content Workspace")
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
                    PlacementHint.Default);
                itemNode.SortKey = 500;
                _itemNodes = new List<ItemNode> { itemNode };
                return _itemNodes;
            }
        }

        public override void Init()
        {
            switch (EnvironmentManager.Instance.EnvironmentType)
            {
                case EnvironmentType.Administration:
                    // Management Client harness for the proxy-fetch exchange (#53): lets the admin
                    // config page fire a test fetch from the MC across to the Event Server.
                    ProxyFetchClient.Active = new ProxyFetchClient();
                    ProxyFetchClient.Active.Setup(EnvironmentManager.Instance.MasterSite == null ? null : EnvironmentManager.Instance.MasterSite.ServerId);
                    break;
                case EnvironmentType.SmartClient:
                    ViewItemPlugins.Add(new WebContentViewItemPlugin());
                    RegisterWorkspaceRecoveryReceivers();
                    StartWorkspaceRetryTimer();
                    EnsureWorkspacesLoaded(force: false, reason: "Smart Client Init");
                    break;
                case EnvironmentType.Standalone:
                case EnvironmentType.ManagementServer:
                    break;
                case EnvironmentType.Service:
                    // Event Server: host the proxy-fetch background plugin. Nothing Smart Client-
                    // or view-specific may run here - the WebView2 view stack is never loaded.
                    _backgroundPlugins.Add(new ProxyFetchBackgroundPlugin());
                    break;
                default:
                    break;
            }
        }

        public override async void Close()
        {
            StopWorkspaceRetryTimer();
            UnregisterWorkspaceRecoveryReceivers();

            var proxyHarness = ProxyFetchClient.Active;
            if (proxyHarness != null)
            {
                ProxyFetchClient.Active = null;
                proxyHarness.Close();
            }

            ViewItemPlugins.Clear();
            _backgroundPlugins.Clear();
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

                _workSpacePlugins.Clear();
                _workSpacePlugins.AddRange(_workspacePluginFactory.CreateWorkSpacePlugins(workspaces));

                if (_workSpacePlugins.Count == 0 && _emptyWorkspaceLoadCount < 3)
                {
                    _emptyWorkspaceLoadCount++;
                    _nextWorkspaceRetryUtc = now.Add(TimeSpan.FromSeconds(15));
                    if (_emptyWorkspaceLoadCount == 1)
                    {
                        EnvironmentManager.Instance.Log(false, nameof(WebContentPluginDefinition), "No workspace tabs were provisioned during startup (none defined, none viewable, or permission lookup unavailable); scheduling retry.");
                    }

                    return;
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
    }
}
