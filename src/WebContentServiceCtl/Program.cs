using System;
using System.ServiceProcess;

namespace WebContent.ServiceCtl
{
    /// <summary>
    /// Ticket #82 entry point: parse, then run ServiceCtlCore against the real service
    /// control manager. The MSI streams this exe as a deferred custom action, so it already
    /// runs as SYSTEM with service-control privilege; it never elevates or self-checks.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            ServiceCtlCommand command;
            string serviceName;
            if (!ServiceCtlCore.TryParse(args, out command, out serviceName))
            {
                Console.Error.WriteLine(ServiceCtlCore.Usage);
                return ServiceCtlCore.ExitUsage;
            }

            try
            {
                return ServiceCtlCore.Run(command, serviceName, new WindowsServiceAccess(), Console.WriteLine);
            }
            catch (Exception ex)
            {
                // Same posture as the missing-service case: an SCM hiccup must not silently
                // look like success, and the MSI's Return="check" turns non-zero into a
                // rollback with the message above in the (manual-run) console.
                Console.Error.WriteLine("service control failed: " + ex.Message);
                return ServiceCtlCore.ExitWaitTimeout;
            }
        }

        private sealed class WindowsServiceAccess : IServiceAccess
        {
            public bool Exists(string serviceName)
            {
                try
                {
                    using (var controller = new ServiceController(serviceName))
                    {
                        var ignored = controller.Status;
                    }

                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }

            public ServiceCtlState GetState(string serviceName)
            {
                using (var controller = new ServiceController(serviceName))
                {
                    return Map(controller.Status);
                }
            }

            public void Stop(string serviceName)
            {
                using (var controller = new ServiceController(serviceName))
                {
                    if (controller.Status == ServiceControllerStatus.Running ||
                        controller.Status == ServiceControllerStatus.Paused)
                    {
                        controller.Stop();
                    }
                }
            }

            public void Start(string serviceName)
            {
                using (var controller = new ServiceController(serviceName))
                {
                    if (controller.Status == ServiceControllerStatus.Stopped)
                    {
                        controller.Start();
                    }
                }
            }

            public bool WaitForState(string serviceName, ServiceCtlState state, TimeSpan timeout)
            {
                using (var controller = new ServiceController(serviceName))
                {
                    try
                    {
                        controller.WaitForStatus(MapTarget(state), timeout);
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }
            }

            private static ServiceControllerStatus MapTarget(ServiceCtlState state)
            {
                switch (state)
                {
                    case ServiceCtlState.Stopped:
                        return ServiceControllerStatus.Stopped;
                    default:
                        return ServiceControllerStatus.Running;
                }
            }

            private static ServiceCtlState Map(ServiceControllerStatus status)
            {
                switch (status)
                {
                    case ServiceControllerStatus.Stopped:
                        return ServiceCtlState.Stopped;
                    case ServiceControllerStatus.Running:
                        return ServiceCtlState.Running;
                    case ServiceControllerStatus.Paused:
                        return ServiceCtlState.Paused;
                    case ServiceControllerStatus.StopPending:
                        return ServiceCtlState.StopPending;
                    case ServiceControllerStatus.StartPending:
                        return ServiceCtlState.StartPending;
                    default:
                        return ServiceCtlState.Other;
                }
            }
        }
    }
}
