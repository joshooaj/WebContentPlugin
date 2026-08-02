using VideoOS.Platform;
using VideoOS.Platform.Client;

namespace WebContent.Client
{
    internal class WebContentExportManager : ExportManager
    {
        public WebContentExportManager(ExportParameters exportParameters) : base(exportParameters)
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentExportManager), "ExportManager created.");
        }

        public override int Progress => 100;

        public override string LastErrorMessage => string.Empty;

        public override void ExportCancelled()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentExportManager), "Export cancelled.");
        }

        public override void ExportComplete()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentExportManager), "Export complete.");
        }

        public override void ExportFailed()
        {
            EnvironmentManager.Instance.Log(true, nameof(WebContentExportManager), $"Export failed. Error: {LastErrorMessage}");
        }

        public override void ExportStarting()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentExportManager), "Export starting.");
        }
    }
}