using System;

namespace WebContent.ServiceCtl
{
    /// <summary>
    /// Ticket #82: the auditable decision core of the MSI's Event Server service helper.
    /// The MSI schedules this exe as a deferred custom action (runs as SYSTEM) to stop the
    /// Event Server service BEFORE the plugin files are overwritten (a still-stopping service
    /// keeps the drop DLLs locked), and - when the admin's finish-page choice says so - to
    /// bring the service back after InstallFiles. Every wait here polls the service
    /// control manager for the real Stopped/Running transition; there is no fixed sleep
    /// anywhere, so a slow service stop is tolerated, not raced.
    /// Exit codes (the custom action is Return="check", so non-zero fails the install):
    ///   0 = done, including "service not installed" (benign no-op; the MSI condition already
    ///       gates on presence, a mid-session removal must not fail the install),
    ///   1 = usage error (never reaches the SCM),
    ///   2 = the service did not reach the requested state within its timeout.
    /// </summary>
    public static class ServiceCtlCore
    {
        public const int ExitDone = 0;
        public const int ExitUsage = 1;
        public const int ExitWaitTimeout = 2;

        // The Event Server's stop is genuinely slow (its plugins dispose on shutdown); the
        // psake deploy flow already allows a minute for the same transition, so the wait
        // budgets start above that.
        public static readonly TimeSpan StopWaitTimeout = TimeSpan.FromMinutes(3);
        public static readonly TimeSpan StartWaitTimeout = TimeSpan.FromMinutes(2);

        public const string Usage =
            "usage: WebContentServiceCtl <stop|restart> <service-name>  " +
            "(stop: stop a running service and wait for Stopped; " +
            "restart: stop-if-running, then start and wait for Running)";

        public static bool TryParseCommand(string arg, out ServiceCtlCommand command)
        {
            switch ((arg ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "stop":
                    command = ServiceCtlCommand.Stop;
                    return true;
                case "restart":
                    command = ServiceCtlCommand.Restart;
                    return true;
                default:
                    command = default(ServiceCtlCommand);
                    return false;
            }
        }

        /// <summary>
        /// Validates the full argument vector: exactly one command and one non-blank service
        /// name. Every refusal happens before the SCM is touched.
        /// </summary>
        public static bool TryParse(string[] args, out ServiceCtlCommand command, out string serviceName)
        {
            command = default(ServiceCtlCommand);
            serviceName = null;
            if (args == null || args.Length != 2)
            {
                return false;
            }

            if (!TryParseCommand(args[0], out command))
            {
                return false;
            }

            serviceName = (args[1] ?? string.Empty).Trim();
            return serviceName.Length > 0;
        }

        /// <summary>
        /// Executes the command against the injected service access. The wait semantics are
        /// the whole point of the ticket: Stop only returns success once the SCM reports
        /// Stopped, Restart only returns success once it reports Running, and a transitional
        /// state (Stop/Start pending) is waited out rather than assumed.
        /// </summary>
        public static int Run(ServiceCtlCommand command, string serviceName, IServiceAccess access, Action<string> log)
        {
            if (access == null)
            {
                throw new ArgumentNullException(nameof(access));
            }

            if (log == null)
            {
                log = _ => { };
            }

            if (!access.Exists(serviceName))
            {
                log("service '" + serviceName + "' is not installed - nothing to do");
                return ExitDone;
            }

            var state = access.GetState(serviceName);

            if (command == ServiceCtlCommand.Stop)
            {
                return StopAndEnsure(serviceName, state, access, log)
                    ? ExitDone
                    : ExitWaitTimeout;
            }

            // Restart: get to Stopped first (if not already there), then start and wait.
            if (state != ServiceCtlState.Stopped && !StopAndEnsure(serviceName, state, access, log))
            {
                return ExitWaitTimeout;
            }

            log("starting service '" + serviceName + "'");
            access.Start(serviceName);
            if (!access.WaitForState(serviceName, ServiceCtlState.Running, StartWaitTimeout))
            {
                log("service '" + serviceName + "' did not reach Running within " + StartWaitTimeout);
                return ExitWaitTimeout;
            }

            log("service '" + serviceName + "' is Running");
            return ExitDone;
        }

        // Drives the service to Stopped: issues Stop only when a stop is actually issuable
        // (Running/Paused), lets a pending transition run, and always waits for the real
        // Stopped verdict. Returns false only on wait timeout.
        private static bool StopAndEnsure(string serviceName, ServiceCtlState state, IServiceAccess access, Action<string> log)
        {
            switch (state)
            {
                case ServiceCtlState.Stopped:
                    log("service '" + serviceName + "' is already Stopped");
                    return true;
                case ServiceCtlState.StopPending:
                    log("service '" + serviceName + "' is StopPending - waiting for Stopped");
                    break;
                default:
                    log("stopping service '" + serviceName + "' (state " + state + ")");
                    access.Stop(serviceName);
                    break;
            }

            if (!access.WaitForState(serviceName, ServiceCtlState.Stopped, StopWaitTimeout))
            {
                log("service '" + serviceName + "' did not reach Stopped within " + StopWaitTimeout);
                return false;
            }

            log("service '" + serviceName + "' is Stopped");
            return true;
        }
    }

    public enum ServiceCtlCommand
    {
        Stop,
        Restart
    }

    /// <summary>
    /// The SCM states the core reasons about, mapped from ServiceControllerStatus by the
    /// entry point so the decision table stays unit-testable without a real service.
    /// </summary>
    public enum ServiceCtlState
    {
        Stopped,
        Running,
        Paused,
        StopPending,
        StartPending,
        Other
    }

    public interface IServiceAccess
    {
        bool Exists(string serviceName);

        ServiceCtlState GetState(string serviceName);

        void Stop(string serviceName);

        void Start(string serviceName);

        bool WaitForState(string serviceName, ServiceCtlState state, TimeSpan timeout);
    }
}
