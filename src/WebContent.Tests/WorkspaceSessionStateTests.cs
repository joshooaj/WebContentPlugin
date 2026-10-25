using System;
using WebContent.Client;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #9: each workspace tab remembers its last visited URL for the lifetime of the
    /// Smart Client session. The store is keyed by the view item's InstanceId and guarded by
    /// the admin-definition fingerprint: a definition edit makes the recorded URL unreadable
    /// (restore falls back to the new home), and a client restart empties the static store.
    /// Standalone (manually placed) view items carry no fingerprint and are never session-tracked.
    /// </summary>
    public class WorkspaceSessionStateTests
    {
        private const string Fingerprint = "fp-definition-v1";
        private const string UpdatedFingerprint = "fp-definition-v2";

        [Fact]
        public void RecordedUrl_Is_Returned_For_The_Same_Fingerprint()
        {
            var key = Guid.NewGuid();
            WorkspaceSessionState.Record(key, Fingerprint, "https://site.example/page");

            Assert.Equal("https://site.example/page", WorkspaceSessionState.TryGetRecordedUrl(key, Fingerprint));
        }

        [Fact]
        public void Fingerprint_Change_Makes_The_Recorded_Url_Unreadable()
        {
            var key = Guid.NewGuid();
            WorkspaceSessionState.Record(key, Fingerprint, "https://site.example/page");

            Assert.Null(WorkspaceSessionState.TryGetRecordedUrl(key, UpdatedFingerprint));
        }

        [Fact]
        public void ReRecording_Replaces_The_Last_Visited_Url()
        {
            var key = Guid.NewGuid();
            WorkspaceSessionState.Record(key, Fingerprint, "https://site.example/one");
            WorkspaceSessionState.Record(key, Fingerprint, "https://site.example/two");

            Assert.Equal("https://site.example/two", WorkspaceSessionState.TryGetRecordedUrl(key, Fingerprint));
        }

        [Fact]
        public void Unknown_Key_Has_No_Recorded_Url()
        {
            Assert.Null(WorkspaceSessionState.TryGetRecordedUrl(Guid.NewGuid(), Fingerprint));
        }

        [Fact]
        public void Blank_Fingerprint_Records_Nothing_And_Restores_Nothing()
        {
            var key = Guid.NewGuid();
            WorkspaceSessionState.Record(key, string.Empty, "https://site.example/page");
            WorkspaceSessionState.Record(key, null, "https://site.example/page");

            Assert.Null(WorkspaceSessionState.TryGetRecordedUrl(key, string.Empty));
            Assert.Null(WorkspaceSessionState.TryGetRecordedUrl(key, null));
            Assert.Null(WorkspaceSessionState.TryGetRecordedUrl(key, Fingerprint));
        }

        [Fact]
        public void Empty_Instance_Key_Is_Ignored()
        {
            WorkspaceSessionState.Record(Guid.Empty, Fingerprint, "https://site.example/page");

            Assert.Null(WorkspaceSessionState.TryGetRecordedUrl(Guid.Empty, Fingerprint));
        }

        [Fact]
        public void Blank_Url_Is_Not_Recorded()
        {
            var key = Guid.NewGuid();
            WorkspaceSessionState.Record(key, Fingerprint, "https://site.example/page");
            WorkspaceSessionState.Record(key, Fingerprint, "   ");

            Assert.Equal("https://site.example/page", WorkspaceSessionState.TryGetRecordedUrl(key, Fingerprint));
        }

        [Fact]
        public void Reset_Clears_The_Recorded_Url()
        {
            var key = Guid.NewGuid();
            WorkspaceSessionState.Record(key, Fingerprint, "https://site.example/page");
            WorkspaceSessionState.Reset(key);

            Assert.Null(WorkspaceSessionState.TryGetRecordedUrl(key, Fingerprint));
        }

        [Fact]
        public void SessionKeys_Are_Independent()
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            WorkspaceSessionState.Record(first, Fingerprint, "https://first.example/");
            WorkspaceSessionState.Record(second, Fingerprint, "https://second.example/");

            Assert.Equal("https://first.example/", WorkspaceSessionState.TryGetRecordedUrl(first, Fingerprint));
            Assert.Equal("https://second.example/", WorkspaceSessionState.TryGetRecordedUrl(second, Fingerprint));
        }

        [Fact]
        public void Recorded_Session_Url_Wins_Over_Persisted_Home_And_Last_Address()
        {
            var resolved = WorkspaceSessionState.ResolveRestoreAddress(
                "https://session.example/page", "https://persisted.example/", true, "https://home.example/");

            Assert.Equal("https://session.example/page", resolved);
        }

        [Fact]
        public void Persisted_Last_Address_Used_When_Nothing_Was_Recorded_This_Session()
        {
            var resolved = WorkspaceSessionState.ResolveRestoreAddress(
                null, "https://persisted.example/", true, "https://home.example/");

            Assert.Equal("https://persisted.example/", resolved);
        }

        [Fact]
        public void Persisted_Last_Address_Ignored_When_RememberIsDisabled()
        {
            var resolved = WorkspaceSessionState.ResolveRestoreAddress(
                null, "https://persisted.example/", false, "https://home.example/");

            Assert.Equal("https://home.example/", resolved);
        }

        [Fact]
        public void Home_Is_The_Final_Fallback()
        {
            Assert.Equal("https://home.example/", WorkspaceSessionState.ResolveRestoreAddress(null, null, false, "https://home.example/"));
            Assert.Equal("https://home.example/", WorkspaceSessionState.ResolveRestoreAddress("   ", string.Empty, true, "https://home.example/"));
        }

        [Theory]
        [InlineData("fp-v1", "fp-v2", true)]
        [InlineData("fp-v1", "fp-v1", false)]
        [InlineData("", "fp-v2", true)]
        [InlineData(null, "fp-v2", true)]
        [InlineData("fp-v1", "", false)]
        [InlineData("fp-v1", null, false)]
        public void DefinitionUpdate_Is_Detected_From_Fingerprints(string previous, string updated, bool expected)
        {
            Assert.Equal(expected, WorkspaceSessionState.ShouldResetOnDefinitionUpdate(previous, updated));
        }
    }
}
