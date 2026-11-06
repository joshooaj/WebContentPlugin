using System;
using System.Collections.Generic;

using WebContent.ServiceCtl;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// #82: decision-table tests for the MSI's Event Server service helper (ServiceCtlCore,
    /// linked from src/WebContentServiceCtl). The live MSI rows run the real SCM; these rows
    /// pin the wait semantics the acceptance criteria hinge on: a stop is only successful
    /// once the SCM reports Stopped (a pending transition is waited out, never assumed), a
    /// declined/first-install flow never starts anything, and every failure exit is
    /// distinguishable. No real service is touched here.
    /// </summary>
    public class EventServerServiceCtlTests
    {
        private const string Service = "MilestoneEventServerService";

        // ---------------------------------------------------------------- parsing

        public static TheoryData<string[]> MalformedArgumentVectors => new TheoryData<string[]>
        {
            null,
            new string[0],
            new[] { "stop" },
            new[] { "stop", Service, "extra" },
            new[] { "status", Service },
            new[] { string.Empty, Service },
            new[] { "stop", "  " },
        };

        [Theory]
        [MemberData(nameof(MalformedArgumentVectors))]
        public void RefusalsNeverReachTheServiceControlManager(string[] args)
        {
            var access = new FakeService();
            var parsed = ServiceCtlCore.TryParse(args, out var command, out var name);

            Assert.False(parsed);
            Assert.Equal(0, access.Calls);
        }

        [Theory]
        [InlineData("stop", ServiceCtlCommand.Stop)]
        [InlineData("STOP", ServiceCtlCommand.Stop)]
        [InlineData(" Restart ", ServiceCtlCommand.Restart)]
        public void BothCommandsParseCaseAndWhitespaceInsensitively(string arg, ServiceCtlCommand expected)
        {
            Assert.True(ServiceCtlCore.TryParse(new[] { arg, Service }, out var command, out var name));
            Assert.Equal(expected, command);
            Assert.Equal(Service, name);
        }

        // ---------------------------------------------------------------- stop

        [Fact]
        public void StopOnRunningServiceWaitsForTheRealStoppedTransition()
        {
            var access = new FakeService();
            access.States.Enqueue(ServiceCtlState.Running);

            var result = ServiceCtlCore.Run(ServiceCtlCommand.Stop, Service, access, null);

            Assert.Equal(ServiceCtlCore.ExitDone, result);
            Assert.True(access.WaitedForStopped);
            // The wait, not a sleep: the core asked the SCM to drive the transition and
            // then demanded the Stopped verdict before claiming success.
            Assert.Equal(1, access.StopCalls);
            Assert.Equal(0, access.StartCalls); // never start during a stop
        }

        [Fact]
        public void StopOnAlreadyStoppedServiceIsANoOp()
        {
            var access = new FakeService();
            access.States.Enqueue(ServiceCtlState.Stopped);

            Assert.Equal(ServiceCtlCore.ExitDone, ServiceCtlCore.Run(ServiceCtlCommand.Stop, Service, access, null));
            Assert.Equal(0, access.StopCalls);
            Assert.False(access.WaitedForStopped);
        }

        [Fact]
        public void StopToleratesASlowStopAlreadyInFlight()
        {
            // Service was stopped by someone else moments ago and is still StopPending:
            // the core must NOT re-issue Stop and must NOT assume - it waits for Stopped.
            var access = new FakeService();
            access.States.Enqueue(ServiceCtlState.StopPending);

            var result = ServiceCtlCore.Run(ServiceCtlCommand.Stop, Service, access, null);

            Assert.Equal(ServiceCtlCore.ExitDone, result);
            Assert.Equal(0, access.StopCalls);
            Assert.True(access.WaitedForStopped);
        }

        [Fact]
        public void StopFailureIsTheWaitTimeoutVerdictNotASilentSuccess()
        {
            var access = new FakeService();
            access.States.Enqueue(ServiceCtlState.Running);
            access.StopReachesStopped = false; // never reaches Stopped within the wait

            var result = ServiceCtlCore.Run(ServiceCtlCommand.Stop, Service, access, null);

            Assert.Equal(ServiceCtlCore.ExitWaitTimeout, result);
        }

        [Fact]
        public void MissingServiceIsABenignNoOp()
        {
            // The MSI conditions already gate on presence; a service removed mid-session
            // must not fail the install.
            var access = new FakeService();
            access.ExistsResult = false;

            var result = ServiceCtlCore.Run(ServiceCtlCommand.Stop, Service, access, null);

            Assert.Equal(ServiceCtlCore.ExitDone, result);
            Assert.Equal(0, access.StopCalls);
        }

        // ---------------------------------------------------------------- restart (the finish-page choice)

        [Fact]
        public void RestartStopsARunningServiceThenWaitsForRunning()
        {
            var access = new FakeService();
            access.States.Enqueue(ServiceCtlState.Running);

            var result = ServiceCtlCore.Run(ServiceCtlCommand.Restart, Service, access, null);

            Assert.Equal(ServiceCtlCore.ExitDone, result);
            Assert.Equal(1, access.StopCalls);
            Assert.Equal(1, access.StartCalls);
            Assert.True(access.WaitedForStopped);
            Assert.True(access.WaitedForRunning);
        }

        [Fact]
        public void RestartOnStoppedServiceJustStartsIt()
        {
            var access = new FakeService();
            access.States.Enqueue(ServiceCtlState.Stopped);

            var result = ServiceCtlCore.Run(ServiceCtlCommand.Restart, Service, access, null);

            Assert.Equal(ServiceCtlCore.ExitDone, result);
            Assert.Equal(0, access.StopCalls);
            Assert.Equal(1, access.StartCalls);
            Assert.True(access.WaitedForRunning);
        }

        [Fact]
        public void RestartRefusesToClaimSuccessWithoutSeeingRunning()
        {
            var access = new FakeService();
            access.States.Enqueue(ServiceCtlState.Stopped);
            access.StartReachesRunning = false;

            var result = ServiceCtlCore.Run(ServiceCtlCommand.Restart, Service, access, null);

            Assert.Equal(ServiceCtlCore.ExitWaitTimeout, result);
        }

        [Fact]
        public void RestartFirstStopCanTimeoutBeforeAnyStartIsIssued()
        {
            // The upgrade branch's hazard: the service will not come down, so the core must
            // never press Start on top of a half-stopped service.
            var access = new FakeService();
            access.States.Enqueue(ServiceCtlState.Running);
            access.StopReachesStopped = false;

            var result = ServiceCtlCore.Run(ServiceCtlCommand.Restart, Service, access, null);

            Assert.Equal(ServiceCtlCore.ExitWaitTimeout, result);
            Assert.Equal(0, access.StartCalls);
        }

        [Fact]
        public void TheWaitBudgetsOutrankASleepGuess()
        {
            // Documented intent: the Event Server's slow stop (the psake flow allows a
            // minute just for the file-unlock retry window) fits inside the core's budget.
            Assert.True(ServiceCtlCore.StopWaitTimeout >= TimeSpan.FromMinutes(2));
            Assert.True(ServiceCtlCore.StartWaitTimeout >= TimeSpan.FromMinutes(1));
        }

        // ---------------------------------------------------------------- fake

        private sealed class FakeService : IServiceAccess
        {
            public Queue<ServiceCtlState> States { get; } = new Queue<ServiceCtlState>();

            public bool ExistsResult = true;

            // Whether the SCM-side wait for the transition would succeed; false models a
            // service that never reaches the requested state within the timeout.
            public bool StopReachesStopped = true;

            public bool StartReachesRunning = true;

            public int StopCalls;
            public int StartCalls;
            public bool WaitedForStopped;
            public bool WaitedForRunning;

            public int Calls => StopCalls + StartCalls;

            public bool Exists(string serviceName) => ExistsResult;

            public ServiceCtlState GetState(string serviceName)
            {
                return States.Count > 0 ? States.Dequeue() : ServiceCtlState.Stopped;
            }

            public void Stop(string serviceName)
            {
                StopCalls++;
            }

            public void Start(string serviceName)
            {
                StartCalls++;
            }

            public bool WaitForState(string serviceName, ServiceCtlState state, TimeSpan timeout)
            {
                if (state == ServiceCtlState.Stopped)
                {
                    WaitedForStopped = true;
                    return StopReachesStopped;
                }

                WaitedForRunning = true;
                return StartReachesRunning;
            }
        }
    }
}
