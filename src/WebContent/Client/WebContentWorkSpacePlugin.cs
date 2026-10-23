using System;
using System.Collections.Generic;
using System.Drawing;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.Messaging;
using WebContent.Admin;
using Message = VideoOS.Platform.Messaging.Message;

namespace WebContent.Client
{
    public class WebContentWorkSpacePlugin : WorkSpacePlugin
    {
        private static readonly Guid WebContentViewItemPluginId = new Guid(Resources.ViewItemPluginId);

        private readonly Guid _workspaceId;
        private readonly string _workspaceName;
        private readonly Dictionary<string, string> _workspaceProperties;
        private readonly List<object> _messageRegistrations = new List<object>();
        private bool _isCurrentWorkspace;

        public WebContentWorkSpacePlugin(Guid workspaceId, string workspaceName, Dictionary<string, string> properties)
        {
            _workspaceId = workspaceId;
            _workspaceName = workspaceName;
            _workspaceProperties = properties ?? new Dictionary<string, string>();

            if (!_workspaceProperties.TryGetValue(nameof(WebContentSettings.InstanceId), out var instanceId) ||
                !Guid.TryParse(instanceId, out _))
            {
                _workspaceProperties[nameof(WebContentSettings.InstanceId)] = _workspaceId.ToString();
            }
        }

        public override Guid Id => _workspaceId;

        public override string Name => _workspaceName;

        public override bool IsSetupStateSupported => true;

        public override void Init()
        {
            try
            {
                LoadProperties(true);
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, nameof(WebContentWorkSpacePlugin), $"Failed to load workspace properties for {_workspaceName} ({_workspaceId}): {ex.Message}");
            }

            ViewAndLayoutItem.Name = _workspaceName;

            var rectangles = new List<Rectangle>
            {
                new Rectangle(0, 0, 1000, 1000)
            };
            ViewAndLayoutItem.Layout = rectangles.ToArray();

            _messageRegistrations.Add(EnvironmentManager.Instance.RegisterReceiver(
                ShownWorkSpaceChangedReceiver,
                new MessageIdFilter(MessageId.SmartClient.ShownWorkSpaceChangedIndication)));

            _messageRegistrations.Add(EnvironmentManager.Instance.RegisterReceiver(
                WorkSpaceStateChangedReceiver,
                new MessageIdFilter(MessageId.SmartClient.WorkSpaceStateChangedIndication)));

            EnsureViewItemPresent();
        }

        public override void Close()
        {
            foreach (var registration in _messageRegistrations)
            {
                EnvironmentManager.Instance.UnRegisterReceiver(registration);
            }

            _messageRegistrations.Clear();
        }

        private object ShownWorkSpaceChangedReceiver(Message message, FQID sender, FQID related)
        {
            _isCurrentWorkspace = message.Data is Item item && item.FQID.ObjectId == _workspaceId;
            if (_isCurrentWorkspace)
            {
                Notification = null;
                EnsureViewItemPresent();
            }

            return null;
        }

        private object WorkSpaceStateChangedReceiver(Message message, FQID sender, FQID related)
        {
            if (!_isCurrentWorkspace)
            {
                return null;
            }

            if (message.Data is WorkSpaceState state && state == WorkSpaceState.Normal)
            {
                EnsureViewItemPresent();
            }

            return null;
        }

        private void EnsureViewItemPresent()
        {
            if (IsWebContentViewItemCurrent())
            {
                return;
            }

            RemoveWebContentViewItem();
            PopulateViewItem();
        }

        private bool IsWebContentViewItemCurrent()
        {
            try
            {
                if (ViewAndLayoutItem.ViewItemId(0) != WebContentViewItemPluginId)
                {
                    return false;
                }

                // Properties without a fingerprint (e.g. hand-built test doubles) keep the old
                // presence-only behavior so re-provisioning never churns on missing metadata.
                if (!_workspaceProperties.TryGetValue(WorkspaceViewItemSync.FingerprintKey, out var fingerprint) ||
                    string.IsNullOrEmpty(fingerprint))
                {
                    return true;
                }

                var existing = ViewAndLayoutItem.GetViewItem(0);
                if (WorkspaceViewItemSync.IsCurrent(existing?.Properties, _workspaceId, fingerprint))
                {
                    return true;
                }

                EnvironmentManager.Instance.Log(false, nameof(WebContentWorkSpacePlugin), $"Persisted view item for workspace {_workspaceName} ({_workspaceId}) is stale or foreign; re-provisioning it from the current definition.");
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void RemoveWebContentViewItem()
        {
            try
            {
                if (ViewAndLayoutItem.ViewItemId(0) != WebContentViewItemPluginId)
                {
                    return;
                }

                var existing = ViewAndLayoutItem.GetViewItem(0);
                if (existing != null)
                {
                    ViewAndLayoutItem.RemoveChild(existing);
                }
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, nameof(WebContentWorkSpacePlugin), $"Failed to remove stale view item from workspace {_workspaceName} ({_workspaceId}): {ex.Message}");
            }
        }

        private void PopulateViewItem()
        {
            try
            {
                ViewAndLayoutItem.InsertViewItemPlugin(0, new WebContentViewItemPlugin(), _workspaceProperties);
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, nameof(WebContentWorkSpacePlugin), $"Failed to populate workspace {_workspaceName} ({_workspaceId}): {ex.Message}");
            }
        }
    }
}
