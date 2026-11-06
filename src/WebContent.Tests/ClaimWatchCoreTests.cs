using System;

using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #96: the pure decision table of the boundary-claim watch. Every row is what one
    /// watch tick ASKS about one proxied browser - does its recorded claim posture still describe
    /// the live boundary, and if not, which existing route heals it? The heart of the ticket,
    /// "the owner died while a joined client sat open", is the dead-owner row: a joined posture
    /// facing a dead-pid claim must Rewire (re-claim through the #93 protocol, then force the
    /// view re-creation through the #87 route). Liveness here is an injected seam; the production
    /// probes are the #93 store's own, pinned by wiring guards.
    /// </summary>
    public class ClaimWatchCoreTests
    {
        private const int OwnPid = 1000;
        private const int ForeignPid = 2000;
        private const int OwnPort = 40000;
        private const int ForeignPort = 41000;
        private const long Now = 1_700_000_000_000L;

        private static string ClaimContent(int ownerPid, int port, long epoch = 1)
        {
            return new ListenerClaim(Guid.NewGuid(), port, ownerPid, "fp", Now, epoch).Serialize();
        }

        /// <summary>A joined browser riding the foreign owner, view up - the steady state.</summary>
        private static ClaimWatchFacts Joined(Action<ClaimWatchFacts> tweak = null)
        {
            var facts = new ClaimWatchFacts
            {
                OwnPid = OwnPid,
                OwnsClaimSlot = false,
                OwnListenerPort = 0,
                RecordedOwnerPid = ForeignPid,
                RecordedPort = ForeignPort,
                RecordedEpoch = 1,
                ViewLive = true,
                InitFailurePending = false,
                NowUnixMs = Now,
                LastInitAttemptUnixMs = 0,
                HandoffAttempts = 0,
            };
            tweak?.Invoke(facts);
            return facts;
        }

        /// <summary>THIS process owning the slot on its own served port, view up - the steady state.</summary>
        private static ClaimWatchFacts Owning(Action<ClaimWatchFacts> tweak = null)
        {
            var facts = new ClaimWatchFacts
            {
                OwnPid = OwnPid,
                OwnsClaimSlot = true,
                OwnListenerPort = OwnPort,
                RecordedOwnerPid = OwnPid,
                RecordedPort = OwnPort,
                RecordedEpoch = 1,
                ViewLive = true,
                InitFailurePending = false,
                NowUnixMs = Now,
                LastInitAttemptUnixMs = 0,
                HandoffAttempts = 0,
            };
            tweak?.Invoke(facts);
            return facts;
        }

        private static ClaimWatchVerdict Decide(ListenerClaimFileRead file, ClaimWatchFacts facts, bool foreignAlive = true, bool portConnectable = true)
        {
            return ClaimWatchCore.Decide(
                file,
                facts,
                pid => pid == OwnPid || (pid == ForeignPid && foreignAlive),
                port => portConnectable);
        }

        [Fact]
        public void NullInputsDecideNothing()
        {
            Assert.Equal(ClaimWatchAction.None, Decide(null, Joined()).Action);
            Assert.Equal(ClaimWatchAction.None, Decide(ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort)), null).Action);
        }

        [Fact]
        public void ATransientReadErrorIsNeverTreatedAsStaleContent()
        {
            // Deleting or re-claiming on a momentary read refusal would evict a LIVE owner's
            // claim - the release discipline refuses this on the write side too.
            var verdict = Decide(ListenerClaimFileRead.Error("reading failed: sharing violation"), Joined());
            Assert.Equal(ClaimWatchAction.None, verdict.Action);
            Assert.Null(verdict.Audit);
        }

        [Fact]
        public void AVanishedSlotRepublishesForAnOwnerButRewiresForAJoiner()
        {
            var owner = Decide(ListenerClaimFileRead.NotFound(), Owning());
            Assert.Equal(ClaimWatchAction.Republish, owner.Action);
            Assert.Contains("vanished while this process was its owner", owner.Audit);

            // #96's acceptance shape (clean exit released the slot): the joiner takes the handoff.
            var joined = Decide(ListenerClaimFileRead.NotFound(), Joined());
            Assert.Equal(ClaimWatchAction.Rewire, joined.Action);
            Assert.Contains("the owner left the boundary", joined.Audit);
        }

        [Fact]
        public void ASlotThatNoLongerParsesToThisBuildRewiresThroughTheProtocol()
        {
            // Foreign schema (incl. pre-epoch v1) and corrupt content ride the #93 stale-with-
            // audit rule, not a watch-side special case.
            foreach (var content in new[] { "complete garbage", "webcontent-listener-claim/3\nx=1\n" })
            {
                var verdict = Decide(ListenerClaimFileRead.Found(content), Joined());
                Assert.Equal(ClaimWatchAction.Rewire, verdict.Action);
                Assert.Contains("no longer parses to this build's schema", verdict.Audit);
            }
        }

        [Fact]
        public void AForeignPidClaimingOurSlotAdoptsTheOwnershipPosture()
        {
            // The slot names US although this browser thinks it joined someone (a won race or a
            // rewire that promoted us): re-consult to adopt the posture honestly.
            var verdict = Decide(
                ListenerClaimFileRead.Found(ClaimContent(OwnPid, OwnPort)),
                Joined(f => f.RecordedOwnerPid = ForeignPid));
            Assert.Equal(ClaimWatchAction.Rewire, verdict.Action);
            Assert.Contains("now names this process", verdict.Audit);
        }

        [Fact]
        public void AnOwnSlotNamingAnotherPortRepublishesForThePortActuallyServed()
        {
            // The listener never stopped serving; only the RECORD is wrong (swept and rewritten
            // against a stale read). Joiners keep their argument; no view has to move.
            var verdict = Decide(
                ListenerClaimFileRead.Found(ClaimContent(OwnPid, ForeignPort)),
                Owning());
            Assert.Equal(ClaimWatchAction.Republish, verdict.Action);
            Assert.Contains("re-publishing the claim for the port actually served", verdict.Audit);
        }

        [Fact]
        public void TheOwnerFacingItsOwnHealthyMatchActsOnTheClaimNotAtAll()
        {
            var verdict = Decide(
                ListenerClaimFileRead.Found(ClaimContent(OwnPid, OwnPort)),
                Owning());
            Assert.Equal(ClaimWatchAction.None, verdict.Action);
            Assert.Null(verdict.Audit);
        }

        [Fact]
        public void TheDeadOwnerIsTheHandoffTrigger()
        {
            // THE row of the ticket: joined posture, live-looking slot, owner process GONE. The
            // survivor rewires - re-claims via the protocol (epoch bumps) and the rewire forces
            // the view re-creation with the new port baked in.
            var verdict = Decide(
                ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort)),
                Joined(),
                foreignAlive: false);
            Assert.Equal(ClaimWatchAction.Rewire, verdict.Action);
            Assert.Contains("no longer running", verdict.Audit);
            Assert.Contains("handoff", verdict.Audit);
        }

        [Fact]
        public void ARefusingOwnerPortIsStaleByTheSameRuleEvenWhileThePidLives()
        {
            var verdict = Decide(
                ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort)),
                Joined(),
                portConnectable: false);
            Assert.Equal(ClaimWatchAction.Rewire, verdict.Action);
            Assert.Contains("refused a connect probe", verdict.Audit);
        }

        [Fact]
        public void AnOwnedPostureFacingALiveForeignOwnerStepsDownToJoin()
        {
            // Another process's staleness verdict took the slot first: no fight, no side-door -
            // the next consult joins it.
            var verdict = Decide(
                ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort)),
                Owning());
            Assert.Equal(ClaimWatchAction.Rewire, verdict.Action);
            Assert.Contains("stepping down to join it", verdict.Audit);
        }

        [Fact]
        public void AChangedGenerationUnderAJoinerRewiresEvenWhenTheOwnerIsAlive()
        {
            // The epoch/port/owner-pid agreement check: any of the three drifting from what this
            // browser RECORDS means its baked --proxy-server argument describes a dead generation.
            foreach (var facts in new[]
            {
                Joined(), // epoch drift: slot is at 2, we joined under 1
                Joined(f => f.RecordedPort = ForeignPort + 1),
                Joined(f => f.RecordedOwnerPid = ForeignPid + 1),
            })
            {
                var verdict = Decide(
                    ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort, epoch: 2)),
                    facts);
                Assert.Equal(ClaimWatchAction.Rewire, verdict.Action);
                Assert.Contains("changed hands while this browser was joined", verdict.Audit);
            }
        }

        [Fact]
        public void TheRecycledBrowserWindowRetriesWithinBudgetInsteadOfReWiring()
        {
            // The claim STILL describes exactly what the browser carries, but the WebView never
            // attached (browser process recycling after the collection exit, ~13 s measured) -
            // that is the retry budget's business, not the claim's.
            var verdict = Decide(
                ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort)),
                Joined(f =>
                {
                    f.ViewLive = false;
                    f.InitFailurePending = true;
                    f.LastInitAttemptUnixMs = Now - ClaimWatchCore.ReCreationGraceMs - 1;
                    f.HandoffAttempts = 1;
                }));
            Assert.Equal(ClaimWatchAction.Retry, verdict.Action);
            Assert.Contains("attempt 2 of " + ClaimWatchCore.MaxHandoffRetries, verdict.Audit);
        }

        [Fact]
        public void TheGraceWindowRefusesToPileUpAttempts()
        {
            var verdict = Decide(
                ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort)),
                Joined(f =>
                {
                    f.ViewLive = false;
                    f.InitFailurePending = true;
                    f.LastInitAttemptUnixMs = Now - 1000; // attempt barely started
                }));
            Assert.Equal(ClaimWatchAction.None, verdict.Action);
            Assert.Null(verdict.Audit);
        }

        [Fact]
        public void AClosedViewIsNeverResurrected()
        {
            // Not-init-failure dead view = the user closed it. The watch heals boundaries, not
            // deliberate closures.
            var verdict = Decide(
                ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort)),
                Joined(f => f.ViewLive = false));
            Assert.Equal(ClaimWatchAction.None, verdict.Action);
            Assert.Null(verdict.Audit);
        }

        [Fact]
        public void TheRetryBudgetTerminatesTheCycleWithOneGiveUpAudit()
        {
            ListenerClaimFileRead file = ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort));
            ClaimWatchFacts FailedInFlight(int attempts) => Joined(f =>
            {
                f.ViewLive = false;
                f.InitFailurePending = true;
                f.LastInitAttemptUnixMs = Now - ClaimWatchCore.ReCreationGraceMs - 1;
                f.HandoffAttempts = attempts;
            });

            for (int attempts = 0; attempts < ClaimWatchCore.MaxHandoffRetries - 1; attempts++)
            {
                Assert.Equal(ClaimWatchAction.Retry, Decide(file, FailedInFlight(attempts)).Action);
            }

            var giveUp = Decide(file, FailedInFlight(ClaimWatchCore.MaxHandoffRetries));
            Assert.Equal(ClaimWatchAction.GiveUp, giveUp.Action);
            Assert.Contains("leaving the in-view notice", giveUp.Audit);

            // Later ticks stay quiet: no second give-up, no endless retries - the cycle ends.
            var quiet = Decide(file, FailedInFlight(ClaimWatchCore.MaxHandoffRetries + 3));
            Assert.Equal(ClaimWatchAction.None, quiet.Action);
            Assert.Null(quiet.Audit);
        }

        [Fact]
        public void OwnerDeathMidHandoffRestartsABoundedCycleNotAnEndlessOne()
        {
            // Survivor A rewired (epoch 2), then DIED itself while B still watches: B faces a
            // dead-pid epoch-2 claim -> the same Rewire row (its Acquire will bump to 3). The
            // retry budget belongs to each generation's re-creation, so termination holds.
            var firstDeath = Decide(
                ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort, epoch: 2)),
                Joined(f => { f.RecordedEpoch = 2; }),
                foreignAlive: false);
            Assert.Equal(ClaimWatchAction.Rewire, firstDeath.Action);

            // And B's own view failing under the settled generation walks the SAME bounded budget.
            var retry = Decide(
                ListenerClaimFileRead.Found(ClaimContent(ForeignPid, ForeignPort, epoch: 3)),
                Joined(f =>
                {
                    f.RecordedEpoch = 3;
                    f.ViewLive = false;
                    f.InitFailurePending = true;
                    f.LastInitAttemptUnixMs = Now - ClaimWatchCore.ReCreationGraceMs - 1;
                    f.HandoffAttempts = ClaimWatchCore.MaxHandoffRetries - 1;
                }));
            Assert.Equal(ClaimWatchAction.Retry, retry.Action);
        }
    }
}
