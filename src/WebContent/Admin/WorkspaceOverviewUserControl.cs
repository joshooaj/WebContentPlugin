using System.Windows.Forms;
using System.Windows.Forms.Integration;
using VideoOS.Platform;
using VideoOS.Platform.Admin;

namespace WebContent.Admin
{
    /// <summary>
    /// Thin WinForms ItemNodeUserControl wrapper required by
    /// <c>GenerateOverviewUserControl</c>. The help content itself is the WPF
    /// <see cref="WorkspaceOverviewPage"/> hosted through an ElementHost.
    /// </summary>
    public class WorkspaceOverviewUserControl : ItemNodeUserControl
    {
        private readonly ElementHost _host;

        public WorkspaceOverviewUserControl()
        {
            // Dock.Fill in the constructor is required: Management Client keeps
            // the overview control at its 150x150 design size unless it stretches.
            Dock = DockStyle.Fill;

            _host = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = new WorkspaceOverviewPage(),
                TabStop = false, // ElementHost honors WinForms TabStop, not WPF IsTabStop.
            };
            Controls.Add(_host);
        }

        public override void Init(Item item)
        {
            // The overview is static content; nothing to initialize.
        }

        public override void Close()
        {
            // Nothing to tear down.
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _host?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
