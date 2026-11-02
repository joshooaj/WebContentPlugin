using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Win32;

using VideoOS.Platform;

namespace WebContent.Admin
{
    /// <summary>
    /// Help page shown on the Web Content Workspaces overview: static onboarding content plus
    /// the ticket #75 deployment CA export. The export asks the Event Server over MIP messaging
    /// (ProxyFetchClient.Active, provisioned during Init) for the CA's PUBLIC certificate and
    /// offers a Save As *.cer (DER); every decision lives in the pure
    /// <see cref="ProxyCaExportViewBuilder"/>, so the page itself stays thin.
    /// </summary>
    public partial class WorkspaceOverviewPage : UserControl
    {
        /// <summary>#75: one export at a time; the messaging exchange has its own deadline.</summary>
        private bool _caExportRunning;

        public WorkspaceOverviewPage()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Ticket #75: pull the deployment CA certificate from the Event Server and Save As it.
        /// No ConfigureAwait(false) anywhere: this is a WPF event handler, and every statement
        /// after the await - including the finally block - must stay on the UI thread. A failed
        /// export never opens a save dialog, so no empty file is ever written.
        /// </summary>
        private async void OnProxyCaExport(object sender, RoutedEventArgs e)
        {
            if (_caExportRunning)
            {
                return;
            }

            _caExportRunning = true;
            btnProxyCaExport.IsEnabled = false;
            proxyCaExportError.Visibility = Visibility.Collapsed;
            txtProxyCaExportError.Text = string.Empty;
            txtProxyCaExportResult.Text = Strings.ProxyCaExportRunning;
            txtProxyCaExportResult.Visibility = Visibility.Visible;

            try
            {
                var client = ProxyFetchClient.Active;
                if (client == null || !client.IsReady)
                {
                    RenderCaExportFailure(Strings.ProxyCaExportNotConnected);
                    txtProxyCaExportResult.Visibility = Visibility.Collapsed;
                    return;
                }

                var response = await client.RequestProxyCertificateAsync();
                var view = ProxyCaExportViewBuilder.Build(response);
                if (!view.ExportAvailable)
                {
                    EnvironmentManager.Instance.Log(
                        true,
                        nameof(WorkspaceOverviewPage),
                        $"Proxy authority certificate export failed (code={view.FailureCode}).");
                    RenderCaExportFailure(CaExportFailureText(view.FailureCode, response));
                    txtProxyCaExportResult.Visibility = Visibility.Collapsed;
                    return;
                }

                EnvironmentManager.Instance.Log(
                    false,
                    nameof(WorkspaceOverviewPage),
                    $"Proxy authority certificate answered: thumbprint {view.Thumbprint}, valid to {view.NotAfterUtc:u}, {view.DerBytes.Length} DER bytes.");

                var dialog = new SaveFileDialog
                {
                    Title = Strings.ProxyCaExportSaveTitle,
                    Filter = Strings.ProxyCaExportSaveFilter,
                    FileName = "WebContent-Proxy-Authority.cer",
                    DefaultExt = "cer",
                    AddExtension = true,
                };
                if (dialog.ShowDialog() != true)
                {
                    txtProxyCaExportResult.Text = Strings.ProxyCaExportCancelled;
                    return;
                }

                File.WriteAllBytes(dialog.FileName, view.DerBytes);
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(WorkspaceOverviewPage),
                    $"Proxy authority certificate exported to '{dialog.FileName}' (thumbprint {view.Thumbprint}).");
                txtProxyCaExportResult.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    Strings.ProxyCaExportSavedFormat,
                    dialog.FileName,
                    view.Thumbprint);
            }
            catch (Exception exception)
            {
                // The button must never strand the page: an unexpected fault reads as a failure
                // sentence, not as a crash dialog.
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(WorkspaceOverviewPage),
                    $"Proxy authority certificate export threw: {exception.Message}");
                RenderCaExportFailure(string.Format(
                    CultureInfo.InvariantCulture,
                    Strings.ProxyCaExportFailedFormat,
                    ProxyFetchErrorCodes.Error,
                    exception.Message));
                txtProxyCaExportResult.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _caExportRunning = false;
                if (btnProxyCaExport != null)
                {
                    btnProxyCaExport.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// The Event Server's failure codes the admin can act on get their own sentence: a
        /// machine without a CA yet reads differently from an Event Server that never answered.
        /// </summary>
        private static string CaExportFailureText(string code, ProxyCaExportResponsePayload response)
        {
            switch (code)
            {
                case ProxyFetchErrorCodes.NoAuthority:
                    return Strings.ProxyCaExportMissing;
                case ProxyFetchErrorCodes.ServerUnavailable:
                    return Strings.ProxyCaExportNotConnected;
                default:
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        Strings.ProxyCaExportFailedFormat,
                        code,
                        response == null ? "no response" : response.ErrorMessage);
            }
        }

        private void RenderCaExportFailure(string text)
        {
            txtProxyCaExportError.Text = text;
            proxyCaExportError.Visibility = Visibility.Visible;
        }
    }
}