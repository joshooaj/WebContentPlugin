using System;
using System.Collections.Generic;
using WebContent;
using WebContent.Client;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #16 (covering #11): persisted configuration values are untrusted input. Malformed,
    /// legacy, or culture-mangled values must fall back to safe defaults instead of throwing, in
    /// both the provider-based (Management Client admin) and dictionary-based (Smart Client
    /// runtime) consumption paths, and a corrupt workspace JSON blob must not take down the
    /// property pipeline.
    /// </summary>
    public class ConfigResilienceTests
    {
        private static readonly Guid FallbackWorkspaceId = new Guid("11111111-2222-3333-4444-555555555555");

        [Theory]
        [InlineData("1")]
        [InlineData("yes")]
        [InlineData("y")]
        [InlineData("on")]
        [InlineData("enabled")]
        [InlineData("TRUE")]
        [InlineData("True")]
        public void LegacyTruthyValuesParseAsTrue(string value)
        {
            Assert.True(WebContentConfiguration.ParseBoolean(value, false));
        }

        [Theory]
        [InlineData("0")]
        [InlineData("no")]
        [InlineData("n")]
        [InlineData("off")]
        [InlineData("disabled")]
        [InlineData("false")]
        public void LegacyFalsyValuesParseAsFalse(string value)
        {
            Assert.False(WebContentConfiguration.ParseBoolean(value, true));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("maybe")]
        [InlineData("2")]
        [InlineData("on-off")]
        public void UnparsableBooleanFallsBackToTheDefault(string value)
        {
            Assert.True(WebContentConfiguration.ParseBoolean(value, true));
            Assert.False(WebContentConfiguration.ParseBoolean(value, false));
        }

        [Theory]
        [InlineData("150", 150.0)]
        [InlineData("37.5", 37.5)]
        [InlineData("  75  ", 75.0)]
        public void ZoomLevelParsesInvariantNumbers(string value, double expected)
        {
            Assert.Equal(expected, WebContentConfiguration.ParseZoomLevel(value, WebContentConfiguration.DefaultZoomLevel));
        }

        [Theory]
        [InlineData("1", 25.0)]
        [InlineData("0", 25.0)]
        [InlineData("1500", 500.0)]
        [InlineData("600", 500.0)]
        public void ZoomLevelOutsideTheAllowedRangeIsClamped(string value, double expected)
        {
            Assert.Equal(expected, WebContentConfiguration.ParseZoomLevel(value, WebContentConfiguration.DefaultZoomLevel));
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("150x")]
        [InlineData("--")]
        public void UnparsableZoomLevelFallsBackToTheDefaultClamped(string value)
        {
            Assert.Equal(42.0, WebContentConfiguration.ParseZoomLevel(value, 42.0));
            Assert.Equal(WebContentConfiguration.MinZoomLevel, WebContentConfiguration.ParseZoomLevel(value, 10.0));
            Assert.Equal(WebContentConfiguration.MaxZoomLevel, WebContentConfiguration.ParseZoomLevel(value, 900.0));
        }

        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        public void NonFiniteZoomLevelIsReplacedWithTheNeutralDefault(string value)
        {
            Assert.Equal(WebContentConfiguration.DefaultZoomLevel, WebContentConfiguration.ParseZoomLevel(value, 42.0));
        }

        [Fact]
        public void EmptyZoomLevelFallsBackToTheDefaultClamped()
        {
            Assert.Equal(42.0, WebContentConfiguration.ParseZoomLevel("   ", 42.0));
            Assert.Equal(WebContentConfiguration.MinZoomLevel, WebContentConfiguration.ParseZoomLevel(null, 0.0));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void NonFiniteZoomIsNeutralizedByClamping(double value)
        {
            Assert.Equal(WebContentConfiguration.DefaultZoomLevel, WebContentConfiguration.ClampZoomLevel(value));
        }

        [Fact]
        public void ZoomLevelRoundTripsThroughFormatAndParseAtTheRangeLimits()
        {
            Assert.Equal("500", WebContentConfiguration.FormatZoomLevel(1500.0));
            Assert.Equal("25", WebContentConfiguration.FormatZoomLevel(0.0));
            Assert.Equal(500.0, WebContentConfiguration.ParseZoomLevel(WebContentConfiguration.FormatZoomLevel(1500.0), 0.0));
            Assert.Equal(25.0, WebContentConfiguration.ParseZoomLevel(WebContentConfiguration.FormatZoomLevel(0.0), 900.0));
        }

        [Fact]
        public void ProviderWithMissingOrBlankValuesReturnsTheDefault()
        {
            var values = new Dictionary<string, string>
            {
                { "blank", "   " },
                { "present", "hello" },
            };

            Assert.Equal("fallback", WebContentConfiguration.GetString(key => values.TryGetValue(key, out var v) ? v : null, "fallback", "missing", "blank"));
            Assert.Equal("hello", WebContentConfiguration.GetString(key => values.TryGetValue(key, out var v) ? v : null, "fallback", "blank", "present"));
        }

        [Fact]
        public void ProviderReceivesKeysInOrderAndSkipsBlankKeyEntries()
        {
            var visited = new List<string>();
            var result = WebContentConfiguration.GetString(
                key => { visited.Add(key); return null; },
                "fallback",
                "", "  ", "real-key");

            Assert.Equal("fallback", result);
            Assert.Equal(new[] { "real-key" }, visited);
        }

        [Fact]
        public void NullProviderOrNullDictionaryReturnsTheDefaultWithoutThrowing()
        {
            Assert.Equal("fallback", WebContentConfiguration.GetString((Func<string, string>)null, "fallback", "key"));
            Assert.Equal("fallback", WebContentConfiguration.GetString((IDictionary<string, string>)null, "fallback", "key"));
            Assert.True(WebContentConfiguration.GetBoolean((IDictionary<string, string>)null, true, "key"));
            Assert.Equal(75.0, WebContentConfiguration.GetZoomLevel((IDictionary<string, string>)null, 75.0, "key"));
            Assert.Equal(FallbackWorkspaceId, WebContentConfiguration.GetGuid((IDictionary<string, string>)null, FallbackWorkspaceId, "key"));
        }

        [Fact]
        public void DictionaryKeysAreMatchedCaseInsensitively()
        {
            var properties = new Dictionary<string, string> { { "hOmeAdDrEsS", "https://configured.example/" } };
            Assert.Equal("https://configured.example/", WebContentConfiguration.GetString(properties, "fallback", "HomeAddress"));
        }

        [Theory]
        [InlineData("not-a-guid")]
        [InlineData("")]
        [InlineData("   ")]
        public void UnparsableGuidFallsBackToTheDefault(string value)
        {
            var properties = new Dictionary<string, string> { { "InstanceId", value } };
            Assert.Equal(FallbackWorkspaceId, WebContentConfiguration.GetGuid(properties, FallbackWorkspaceId, "InstanceId"));
        }

        [Fact]
        public void CorruptWorkspaceConfigBlobIsIgnoredInsteadOfThrowing()
        {
            Assert.Null(WorkspaceConfiguration.Deserialize("{ this is not json"));
            Assert.Null(WorkspaceConfiguration.Deserialize(""));
            Assert.Null(WorkspaceConfiguration.Deserialize(null));
            Assert.Null(WorkspaceConfiguration.FromProperties(new Dictionary<string, string> { { WorkspaceConfiguration.WorkspaceConfigKey, "{{{" } }));
            Assert.Null(WorkspaceConfiguration.FromProperties(null));
        }

        [Fact]
        public void JsonThatParsesButCarriesNoItemsYieldsNoUsableSettings()
        {
            var config = WorkspaceConfiguration.Deserialize("[]");
            Assert.NotNull(config);
            Assert.Null(config.GetFirstWebContentSettings());
            Assert.Null(WorkspaceConfiguration.GetFirstWebContentSettings(
                new Dictionary<string, string> { { WorkspaceConfiguration.WorkspaceConfigKey, "[]" } }));
        }

        [Fact]
        public void CorruptWorkspaceConfigBlobFallsBackToLegacyProperties()
        {
            var properties = new Dictionary<string, string>
            {
                { WorkspaceConfiguration.WorkspaceConfigKey, "{ corrupt" },
                { "Address", "server:8443" },
            };

            var built = WorkspaceProperties.Build(FallbackWorkspaceId, "Workspace", properties, "https://fallback.example/");

            Assert.Equal("https://server:8443/", built["HomeAddress"]);
            Assert.Equal(FallbackWorkspaceId.ToString(), built["InstanceId"]);
        }

        [Fact]
        public void WorkspaceConfigBlobWithoutItemsFallsBackToLegacyProperties()
        {
            var properties = new Dictionary<string, string>
            {
                { WorkspaceConfiguration.WorkspaceConfigKey, "{\"version\":1,\"layout\":\"1x1\"}" },
                { "HomeAddress", "https://legacy.example/" },
            };

            var built = WorkspaceProperties.Build(FallbackWorkspaceId, "Workspace", properties, "https://fallback.example/");

            Assert.Equal("https://legacy.example/", built["HomeAddress"]);
        }

        [Fact]
        public void WebContentItemWithoutSettingsUsesNeutralDefaults()
        {
            var properties = new Dictionary<string, string>
            {
                { WorkspaceConfiguration.WorkspaceConfigKey, "{\"version\":1,\"items\":[{\"type\":\"WebContent\",\"position\":0}]}" },
            };

            var built = WorkspaceProperties.Build(FallbackWorkspaceId, "Workspace", properties, "https://fallback.example/");

            Assert.Equal("https://fallback.example/", built["HomeAddress"]);
            Assert.Equal("100", built["ZoomLevel"]);
            Assert.Equal("True", built["AllowBackgroundBrowser"]);
            Assert.Equal("True", built["AllowAudioNotifications"]);
            Assert.Equal("False", built["EnableDevTools"]);
        }

        [Fact]
        public void GarbageInLegacyPropertiesStillProducesCanonicalSafeValues()
        {
            var properties = new Dictionary<string, string>
            {
                { "HomeAddress", "javascript:alert(1)" },
                { "ZoomLevel", "999999" },
                { "HideToolbar", "not-a-bool" },
                { "InstanceId", "not-a-guid" },
            };

            var built = WorkspaceProperties.Build(FallbackWorkspaceId, "Workspace", properties, "https://fallback.example/");

            Assert.Equal("https://fallback.example/", built["HomeAddress"]);
            Assert.Equal("500", built["ZoomLevel"]);
            Assert.Equal("False", built["HideToolbar"]);
            Assert.Equal(FallbackWorkspaceId.ToString(), built["InstanceId"]);
        }
    }
}
