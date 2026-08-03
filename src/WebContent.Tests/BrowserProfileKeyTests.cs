using System;
using System.Linq;
using Xunit;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the pure per-XProtect-user browser profile key derivation. These verify the
    /// security behaviour on ticket #17: distinct XProtect identities must map to distinct,
    /// stable, filesystem-safe profile keys so browsers are not shared across users.
    /// </summary>
    public class BrowserProfileKeyTests
    {
        [Fact]
        public void Distinct_UserIdentities_Produce_Distinct_Keys()
        {
            var user1 = BrowserProfileKey.DeriveUserKey("11111111-2222-3333-4444-555555555555", "Guard", "User1");
            var user2 = BrowserProfileKey.DeriveUserKey("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Guard", "User2");

            Assert.NotNull(user1);
            Assert.NotNull(user2);
            Assert.NotEqual(user1, user2);
        }

        [Fact]
        public void Distinct_Users_Same_WindowsAccount_Do_Not_Share_Profile_Key()
        {
            // Different XProtect users behind a shared Windows account name.
            var guardUser1 = BrowserProfileKey.DeriveUserKey("11111111-2222-3333-4444-555555555555", "WORKGROUP\\Guard", "User1");
            var guardUser2 = BrowserProfileKey.DeriveUserKey("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "WORKGROUP\\Guard", "User2");

            Assert.NotEqual(guardUser1, guardUser2);
        }

        [Theory]
        [InlineData("same-user", null, null)]
        [InlineData("GUARD|User1", "SameId", "X")]
        [InlineData(null, null, "same-user")]
        public void Same_User_Produces_Stable_Key(string id1, string id2, string name)
        {
            var first = BrowserProfileKey.DeriveUserKey(id1, id2, name);
            var second = BrowserProfileKey.DeriveUserKey(id1, id2, name);

            Assert.Equal(first, second);
            Assert.False(string.IsNullOrEmpty(first));
        }

        [Fact]
        public void Falls_Back_To_FullyQualifiedUserName_When_Identity_Missing()
        {
            var key = BrowserProfileKey.DeriveUserKey(null, "DOMAIN\\User1", "User1");
            Assert.Equal("DOMAIN_User1", key);
        }

        [Fact]
        public void Basic_Users_With_Same_WindowsAccount_Are_Distinguished_By_FullyQualifiedUserName()
        {
            // SDK: FullyQualifiedUserName for basic users is "[BASIC]\accountname".
            var user1 = BrowserProfileKey.DeriveUserKey(null, "[BASIC]\\User1", "User1");
            var user2 = BrowserProfileKey.DeriveUserKey(null, "[BASIC]\\User2", "User2");

            Assert.NotEqual(user1, user2);
            Assert.Equal("_BASIC__User1", user1);
            Assert.Equal("_BASIC__User2", user2);
        }

        [Fact]
        public void Falls_Back_To_UserName_When_Only_Name_Available()
        {
            var key = BrowserProfileKey.DeriveUserKey(null, null, "User1");
            Assert.Equal("User1", key);
        }

        [Fact]
        public void Empty_Identity_Produces_Empty_Key()
        {
            Assert.Equal(string.Empty, BrowserProfileKey.DeriveUserKey(null, null, null));
            Assert.Equal(string.Empty, BrowserProfileKey.DeriveUserKey(" ", "", null));
        }

        [Fact]
        public void Sanitize_Removes_Path_Separators_And_Special_Chars()
        {
            var key = BrowserProfileKey.Sanitize(@"C:\Users\Guard\AppData");

            Assert.DoesNotContain('\\', key);
            Assert.DoesNotContain(':', key);
            Assert.DoesNotContain('/', key);
            Assert.DoesNotContain(' ', key);
        }

        [Fact]
        public void Sanitize_Preserves_Alphanumerics_Dot_Dash_Underscore()
        {
            var key = BrowserProfileKey.Sanitize("User-1.Name_x.9");

            Assert.Equal("User-1.Name_x.9", key);
        }

        [Fact]
        public void Sanitize_Neutralizes_Braces_From_Guid()
        {
            var key = BrowserProfileKey.Sanitize("{aBcD1234-0000-0000-0000-000000000000}");

            Assert.DoesNotContain('{', key);
            Assert.DoesNotContain('}', key);
            Assert.Equal("_aBcD1234-0000-0000-0000-000000000000_", key);
        }

        [Fact]
        public void Sanitize_Returns_Empty_When_Only_Special_Chars()
        {
            var key = BrowserProfileKey.Sanitize("\\ / : * ? \" < > |");

            Assert.True(string.IsNullOrEmpty(key) || key.StartsWith("_", StringComparison.Ordinal));
        }

        [Fact]
        public void Sanitize_Handles_Unicode_Identities()
        {
            var key = BrowserProfileKey.Sanitize("Åke_Öberg");

            Assert.False(string.IsNullOrEmpty(key));
        }
    }
}