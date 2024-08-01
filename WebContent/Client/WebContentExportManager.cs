using VideoOS.Platform.Client;

namespace WebContent.Client
{
    internal class WebContentExportManager : ExportManager
    {
        public WebContentExportManager(ExportParameters exportParameters) : base(exportParameters)
        {
        }

        public override int Progress => 100;

        public override string LastErrorMessage => string.Empty;

        public override void ExportCancelled()
        {
            // No-op
        }

        public override void ExportComplete()
        {
            // No-op
        }

        public override void ExportFailed()
        {
            // No-op
        }

        public override void ExportStarting()
        {
            // No-op
        }
    }
}