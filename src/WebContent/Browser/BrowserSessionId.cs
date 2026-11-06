using System.Globalization;

namespace WebContent.Browser
{
    /// <summary>
    /// A browser session id: a strictly positive integer that identifies one <em>set of browser state</em> —
    /// its own browsing data, cookies, storage and cache, surviving view closes and reopens — within a plugin
    /// module instance. It is not a view: any number of views may run against the same session.
    ///
    /// The type is internal; it becomes visible to integrators only through the <see cref="WebContentPluginDescriptor"/>
    /// metadata (where a null id means "the default view session") and the trigger message fields (where the
    /// caller selects a session for the navigation).
    /// </summary>
    internal static class BrowserSessionId
    {
        /// <summary>The reserved id (0) naming the implicit default session in which views without an explicit
        /// browser session id run. Each view gets its own instance of it.</summary>
        public const int DefaultViewSessionId = 0;

        public static bool IsValid(int? id)
        {
            return id.HasValue && id.Value > 0;
        }

        public static bool IsDefaultViewSession(int? id)
        {
            return id.HasValue && id.Value == DefaultViewSessionId;
        }

        public static bool IsNewSession(int? id)
        {
            return !id.HasValue;
        }

        public static string Format(int? id)
        {
            return id.HasValue
                ? id.Value.ToString(CultureInfo.InvariantCulture)
                : "none";
        }

        /// <summary>Parses a browser session id from a JSON message field: the field must be present, and be a
        /// positive integer (or a decimal-integer string). A field explicitly set to null is not a browser session
        /// selection at all. Anything else fails with an actionable message.</summary>
        public static bool TryParseField(object raw, out int id, out string error)
        {
            id = 0;
            error = null;
            switch (raw)
            {
                case int i:
                    if (i > 0)
                    {
                        id = i;
                        return true;
                    }
                    error = string.Format(CultureInfo.InvariantCulture, "browser_session_id must be a positive integer, but was {0}.", i);
                    return false;
                case long l when l > 0 && l <= int.MaxValue:
                    id = (int)l;
                    return true;
                case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0:
                    id = parsed;
                    return true;
                case null:
                    error = "browser_session_id must be present and a positive integer, but was null.";
                    return false;
                default:
                    error = string.Format(CultureInfo.InvariantCulture, "browser_session_id must be a positive integer, but was \"{0}\".", raw);
                    return false;
            }
        }
    }
}
