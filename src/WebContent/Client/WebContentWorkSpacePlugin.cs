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
            LoadProperties(true);

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
            if (IsWebContentViewItemPresent())
            {
                return;
            }

            PopulateViewItem();
        }

        private bool IsWebContentViewItemPresent()
        {
            try
            {
                return ViewAndLayoutItem.ViewItemId(0) == WebContentViewItemPluginId;
            }
            catch
            {
                return false;
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
                EnvironmentManager.Instance.ExceptionDialog("WebContentWorkSpace:PopulateViewItem", ex);
            }
        }
    }
}
