using System;
using System.Collections.Generic;
using System.Globalization;

namespace WebContent
{
    internal static class WebContentConfiguration
    {
        internal const double DefaultZoomLevel = 100.0;
        internal const double MinZoomLevel = 25.0;
        internal const double MaxZoomLevel = 500.0;

        internal static string GetString(Func<string, string> valueProvider, string defaultValue, params string[] keys)
        {
            if (valueProvider == null || keys == null)
            {
                return defaultValue;
            }

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var value = valueProvider(key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return defaultValue;
        }

        internal static string GetString(IDictionary<string, string> properties, string defaultValue, params string[] keys)
        {
            if (properties == null || keys == null)
            {
                return defaultValue;
            }

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var value = TryGetPropertyValue(properties, key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return defaultValue;
        }

        internal static bool GetBoolean(Func<string, string> valueProvider, bool defaultValue, params string[] keys)
        {
            var rawValue = GetString(valueProvider, null, keys);
            return ParseBoolean(rawValue, defaultValue);
        }

        internal static bool GetBoolean(IDictionary<string, string> properties, bool defaultValue, params string[] keys)
        {
            var rawValue = GetString(properties, null, keys);
            return ParseBoolean(rawValue, defaultValue);
        }

        internal static double GetZoomLevel(Func<string, string> valueProvider, double defaultValue, params string[] keys)
        {
            var rawValue = GetString(valueProvider, null, keys);
            return ParseZoomLevel(rawValue, defaultValue);
        }

        internal static double GetZoomLevel(IDictionary<string, string> properties, double defaultValue, params string[] keys)
        {
            var rawValue = GetString(properties, null, keys);
            return ParseZoomLevel(rawValue, defaultValue);
        }

        internal static Guid GetGuid(Func<string, string> valueProvider, Guid defaultValue, params string[] keys)
        {
            var rawValue = GetString(valueProvider, null, keys);
            return Guid.TryParse(rawValue, out var value) ? value : defaultValue;
        }

        internal static Guid GetGuid(IDictionary<string, string> properties, Guid defaultValue, params string[] keys)
        {
            var rawValue = GetString(properties, null, keys);
            return Guid.TryParse(rawValue, out var value) ? value : defaultValue;
        }

        internal static string NormalizeHomeAddress(string address)
        {
            return NormalizeHomeAddress(address, Strings.HomeAddress);
        }

        internal static string NormalizeHomeAddress(string address, string fallbackAddress)
        {
            return NormalizeAddressOrDefault(address, fallbackAddress);
        }

        internal static string NormalizeOptionalAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return string.Empty;
            }

            return TryCreateNavigationUri(address, out var uri) ? uri.ToString() : string.Empty;
        }

        internal static string NormalizeAddressOrDefault(string address, string fallbackAddress)
        {
            var uri = CreateNavigationUri(address, fallbackAddress);
            return uri.ToString();
        }

        internal static Uri CreateNavigationUri(string address, string fallbackAddress = null)
        {
            if (TryCreateNavigationUri(address, out var uri))
            {
                return uri;
            }

            if (TryCreateNavigationUri(fallbackAddress, out uri))
            {
                return uri;
            }

            return new Uri(Strings.HomeAddress);
        }

        internal static bool TryCreateNavigationUri(string address, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            var trimmed = address.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out uri))
            {
                return true;
            }

            return Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out uri);
        }

        internal static string FormatBoolean(bool value)
        {
            return value.ToString();
        }

        internal static string FormatZoomLevel(double value)
        {
            return ClampZoomLevel(value).ToString("F0", CultureInfo.InvariantCulture);
        }

        internal static double ClampZoomLevel(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return DefaultZoomLevel;
            }

            return Math.Max(MinZoomLevel, Math.Min(MaxZoomLevel, value));
        }

        internal static bool ParseBoolean(string value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "yes":
                case "y":
                case "on":
                case "enabled":
                    return true;
                case "0":
                case "no":
                case "n":
                case "off":
                case "disabled":
                    return false;
                default:
                    return bool.TryParse(value, out var parsedValue) ? parsedValue : defaultValue;
            }
        }

        internal static double ParseZoomLevel(string value, double defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ClampZoomLevel(defaultValue);
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue) &&
                !double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsedValue))
            {
                parsedValue = defaultValue;
            }

            return ClampZoomLevel(parsedValue);
        }

        private static string TryGetPropertyValue(IDictionary<string, string> properties, string key)
        {
            if (properties.TryGetValue(key, out var value))
            {
                return value;
            }

            foreach (var property in properties)
            {
                if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }
    }
}
