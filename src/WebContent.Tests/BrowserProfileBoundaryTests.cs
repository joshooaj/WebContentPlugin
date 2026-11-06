using System;
using System.IO;

using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the proxied-boundary keying of proxied browser profiles (ADR-0004, #92):
    /// the profile path is keyed to the FULL (Windows user, XProtect identity, VMS server)
    /// tuple, so same identity on two servers never shares a proxied profile or fetch
    /// channel, while every leg still maps to one sanitized, filesystem-safe segment.
    /// </summary>
    public class BrowserProfileBoundaryTests
    {
        private const string Root = @"C:\Users\someone\AppData\Roaming\SmartClient.WebContentData";

        private static readonly Guid BrowserId = Guid.Parse("2963db34-185a-4383-84c5-c9f7d780b425");

        // ---- server-key derivation -------------------------------------------------------

        [Fact]
        public void Server_Key_Folds_Host_And_Port()
        {
            Assert.Equal("vms.example.com-80", BrowserProfileKey.DeriveServerKey("vms.example.com", 80));
        }

        [Fact]
        public void Server_Key_Is_Case_Insensitive_On_Host_But_Port_Sensitive()
        {
            Assert.Equal(
                BrowserProfileKey.DeriveServerKey("VMS.Example.COM", 443),
                BrowserProfileKey.DeriveServerKey("vms.example.com", 443));
            Assert.NotEqual(
                BrowserProfileKey.DeriveServerKey("vms.example.com", 443),
                BrowserProfileKey.DeriveServerKey("vms.example.com", 8443));
        }

        [Fact]
        public void Server_Key_Sanitizes_Host_Graffiti()
        {
            // A DNS host never carries these; a hostile/foreign ServerId value must still
            // never escape its folder level.
            var key = BrowserProfileKey.DeriveServerKey(@"..\. \evil:443", 0);

            Assert.DoesNotContain('\\', key);
            Assert.DoesNotContain('/', key);
            Assert.DoesNotContain(':', key);
            Assert.DoesNotContain(' ', key);
        }

        [Fact]
        public void Server_Key_Empty_When_Host_Missing()
        {
            Assert.Equal(string.Empty, BrowserProfileKey.DeriveServerKey(null, 443));
            Assert.Equal(string.Empty, BrowserProfileKey.DeriveServerKey("  ", 443));
        }

        [Fact]
        public void Server_Key_Without_Port_When_Port_Unknown()
        {
            Assert.Equal("vms.example.com", BrowserProfileKey.DeriveServerKey("vms.example.com", -1));
            Assert.Equal("vms.example.com", BrowserProfileKey.DeriveServerKey("vms.example.com", 0));
        }

        // ---- path composition decision table ----------------------------------------------

        [Fact]
        public void Same_Tuple_Produces_Identical_Proxied_Path()
        {
            var first = BrowserProfileKey.ComposeProxiedProfilePath(Root, "D6BB5695", "vms.example.com-80", BrowserId);
            var second = BrowserProfileKey.ComposeProxiedProfilePath(Root, "D6BB5695", "vms.example.com-80", BrowserId);

            Assert.Equal(first, second);
        }

        [Fact]
        public void Same_Identity_Different_Server_Produces_Different_Proxied_Path()
        {
            // The #92 regression this ticket exists for: one Windows user, one XProtect
            // identity, TWO VMS servers - the paths must not share a folder.
            var serverA = BrowserProfileKey.ComposeProxiedProfilePath(Root, "D6BB5695", "vms-a.example.com-80", BrowserId);
            var serverB = BrowserProfileKey.ComposeProxiedProfilePath(Root, "D6BB5695", "vms-b.example.com-80", BrowserId);

            Assert.NotEqual(serverA, serverB);
        }

        [Fact]
        public void Same_People_Different_Browser_Id_Produce_Different_Proxied_Path()
        {
            var one = BrowserProfileKey.ComposeProxiedProfilePath(Root, "D6BB5695", "vms.example.com-80", BrowserId);
            var other = BrowserProfileKey.ComposeProxiedProfilePath(Root, "D6BB5695", "vms.example.com-80", Guid.NewGuid());

            Assert.NotEqual(one, other);
        }

        [Fact]
        public void Path_Shape_Is_Root_User_Server_Proxied()
        {
            var path = BrowserProfileKey.ComposeProxiedProfilePath(Root, "D6BB5695", "vms.example.com-80", BrowserId);

            Assert.Equal(
                Path.Combine(Root, "D6BB5695", "server-vms.example.com-80", "proxied-2963db34-185a-4383-84c5-c9f7d780b425"),
                path);
        }

        [Fact]
        public void Proxied_Leaf_Keeps_Lowercase_D_Guid_Format()
        {
            // The folder name must match the browser id the plugin logs (and #94's sweep
            // matches on this prefix).
            var path = BrowserProfileKey.ComposeProxiedProfilePath(Root, "User1", "vms.example.com-80", BrowserId);

            Assert.EndsWith("proxied-" + BrowserId.ToString("D"), path, StringComparison.Ordinal);
            Assert.Contains("proxied-2963db34-185a-4383-84c5-c9f7d780b425", path, StringComparison.Ordinal);
        }

        [Fact]
        public void Identity_Fallbacks_Still_Name_The_User_Segment()
        {
            // The user leg arrives through DeriveUserKey's fallback chain; whatever leg won,
            // the composition keeps it as its own segment.
            var viaIdentity = BrowserProfileKey.DeriveUserKey("D6BB5695-05C9", "WORKGROUP\\jhadmin", "jhadmin");
            var viaFqn = BrowserProfileKey.DeriveUserKey(null, "WORKGROUP\\jhadmin", "jhadmin");
            var viaName = BrowserProfileKey.DeriveUserKey(null, null, "jhadmin");

            Assert.Equal(Path.Combine(Root, "D6BB5695-05C9", "server-vms.example.com-80", "proxied-" + BrowserId.ToString("D")),
                BrowserProfileKey.ComposeProxiedProfilePath(Root, viaIdentity, "vms.example.com-80", BrowserId));
            Assert.Equal(Path.Combine(Root, "WORKGROUP_jhadmin", "server-vms.example.com-80", "proxied-" + BrowserId.ToString("D")),
                BrowserProfileKey.ComposeProxiedProfilePath(Root, viaFqn, "vms.example.com-80", BrowserId));
            Assert.Equal(Path.Combine(Root, "jhadmin", "server-vms.example.com-80", "proxied-" + BrowserId.ToString("D")),
                BrowserProfileKey.ComposeProxiedProfilePath(Root, viaName, "vms.example.com-80", BrowserId));
        }

        [Fact]
        public void Missing_User_Key_Falls_Back_To_Root_Level_But_Keeps_Server_Separation()
        {
            // Pre-login/design-time identity loss keeps the existing shared-root fallback
            // shape, and the server leg STILL fences the boundary at that level.
            var noUser = BrowserProfileKey.ComposeProxiedProfilePath(Root, string.Empty, "vms-a.example.com-80", BrowserId);

            Assert.Equal(Path.Combine(Root, "server-vms-a.example.com-80", "proxied-" + BrowserId.ToString("D")), noUser);
            Assert.NotEqual(noUser, BrowserProfileKey.ComposeProxiedProfilePath(Root, string.Empty, "vms-b.example.com-80", BrowserId));
        }

        [Fact]
        public void Missing_Server_Key_Uses_Placeholder_And_Never_Matches_A_Resolved_Server()
        {
            var unknown = BrowserProfileKey.ComposeProxiedProfilePath(Root, "User1", string.Empty, BrowserId);
            var resolved = BrowserProfileKey.ComposeProxiedProfilePath(Root, "User1", "vms.example.com-80", BrowserId);

            Assert.Equal(Path.Combine(Root, "User1", "server-unknown", "proxied-" + BrowserId.ToString("D")), unknown);
            Assert.NotEqual(unknown, resolved);

            // Processes that all fail to resolve agree on the one placeholder.
            Assert.Equal(unknown, BrowserProfileKey.ComposeProxiedProfilePath(Root, "User1", null, BrowserId));
        }

        [Fact]
        public void Hostile_Legs_Never_Escape_Their_Folder_Level()
        {
            var path = BrowserProfileKey.ComposeProxiedProfilePath(
                Root,
                @"..\..\..\evil",
                @"..\..\..\evil:443\",
                BrowserId);

            // Everything under the root stays exactly three segment levels down: no traversal.
            Assert.StartsWith(Root + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            var tail = path.Substring(Root.Length + 1);
            Assert.Equal(3, tail.Split(Path.DirectorySeparatorChar).Length);

            // And the composition re-sanitizes, so even a caller that skipped its own
            // sanitize step cannot break out.
            Assert.DoesNotContain(@"..\..\..\", path, StringComparison.Ordinal);
        }

        [Fact]
        public void Server_Segment_Prefix_Guarantees_It_Is_Never_Dot_Or_DotDot()
        {
            // The prefix is what makes the placeholder discussion moot: the segment can never
            // BE a traversal token regardless of what the key sanitizes to.
            var path = BrowserProfileKey.ComposeProxiedProfilePath(Root, "User1", "..", BrowserId);

            Assert.Equal(Path.Combine(Root, "User1", "server-..", "proxied-" + BrowserId.ToString("D")), path);
        }
    }
}
