using System;
using System.Linq;
using System.Reflection;

using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #87 interim contract (ADR-0004 point 5): a failed PROXIED CoreWebView2
    /// initialization must produce an honest in-view profile-ownership presentation plus ONE
    /// explanatory log line naming the cause and the browser id - and nothing at all for a
    /// non-proxied failure (its error surface is unchanged). The whole classifier runs without
    /// a live WebView2, filesystem, or process.
    /// </summary>
    public class BrowserInitFailureTests
    {
        private static readonly Guid BrowserId = Guid.Parse("12345678-1234-4234-8234-9876543210ab");

        [Fact]
        public void NonProxiedFailure_PresentsNothingAndKeepsTheLogOnlySurface()
        {
            BrowserInitFailure.Presentation presentation;
            Assert.False(BrowserInitFailure.TryPresent(proxied: false, BrowserId, "whatever the wrapper threw", out presentation));
            Assert.Null(presentation);
        }

        [Fact]
        public void ProxiedFailure_PresentsTheProfileOwnershipCause()
        {
            BrowserInitFailure.Presentation presentation;
            Assert.True(BrowserInitFailure.TryPresent(proxied: true, BrowserId, "The WebView2 environment could not be created because another process is using the user data folder with different options.", out presentation));

            // The distinct cause category from the shared error-page wording layer - never one
            // of the per-request proxy refusal categories, so it reads as profile ownership.
            Assert.Equal(ProxyErrorPage.CategoryProfileOwned, presentation.Category);
            Assert.DoesNotContain("gateway", presentation.Explanation, StringComparison.OrdinalIgnoreCase);

            // The user-facing wording names the other session ON THIS MACHINE and the profile,
            // and points at the honest remedy (close the other session / reload after it exits).
            Assert.False(string.IsNullOrWhiteSpace(presentation.CauseLabel));
            Assert.Contains("Another Smart Client session", presentation.Headline, StringComparison.Ordinal);
            Assert.Contains("profile", presentation.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("this computer", presentation.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Smart Client session", presentation.Explanation, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheOneLogLineNamesTheOwnershipCauseTheBrowserIdAndTheTechnicalMessage()
        {
            const string technical = "Initialization failed: options mismatch on the shared user data folder.";

            BrowserInitFailure.Presentation presentation;
            Assert.True(BrowserInitFailure.TryPresent(proxied: true, BrowserId, "  " + technical + "  ", out presentation));

            // One line (no embedded newlines) naming cause + browser id + verbatim message.
            Assert.DoesNotContain(Environment.NewLine, presentation.LogLine);
            Assert.DoesNotContain("\n", presentation.LogLine);
            Assert.Contains("proxied browser " + BrowserId, presentation.LogLine, StringComparison.Ordinal);
            Assert.Contains("profile", presentation.LogLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("another Smart Client session on this machine", presentation.LogLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(technical, presentation.LogLine, StringComparison.Ordinal);
        }

        [Fact]
        public void MissingExceptionMessage_StillPresentsAndNamesBrowserId()
        {
            BrowserInitFailure.Presentation presentation;
            Assert.True(BrowserInitFailure.TryPresent(proxied: true, BrowserId, null, out presentation));
            Assert.Contains("proxied browser " + BrowserId, presentation.LogLine, StringComparison.Ordinal);
            Assert.Contains("no initialization exception was reported", presentation.LogLine, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(presentation.Detail));
        }

        [Fact]
        public void TheOwnershipCategoryIsNeverMappedFromAPerRequestWireCode()
        {
            // The category belongs to the browser-level contract, not the refusal pipeline:
            // no ProxyFetchErrorCodes value may map onto it, and it maps onto nothing itself.
            var codes = typeof(ProxyFetchErrorCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(string))
                .Select(f => (string)f.GetValue(null))
                .ToList();

            foreach (var code in codes)
            {
                string category;
                if (ProxyErrorPage.TryCategoryForErrorCode(code, out category))
                {
                    Assert.NotEqual(ProxyErrorPage.CategoryProfileOwned, category);
                }
            }

            string unmapped;
            Assert.False(ProxyErrorPage.TryCategoryForErrorCode(ProxyErrorPage.CategoryProfileOwned, out unmapped));
        }
    }
}
