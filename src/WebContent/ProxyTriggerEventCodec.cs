using System;
using System.IO;
using System.Runtime.Serialization.Json;

using VideoOS.Platform.Data;

namespace WebContent
{
    /// <summary>
    /// Ticket #65: wire codec for the rule-trigger event raise. The event travels as ONE JSON
    /// string on the system <c>Server.NewEventCommand</c> message - the same codec discipline as
    /// every proxy payload: the platform binds message payloads via Type.GetType, and a receiver
    /// that cannot resolve the payload type silently gets null. AnalyticsEvent is a platform type
    /// (so a typed object would survive on matching builds), but the JSON string keeps the raise
    /// working identically across Event Server versions and makes the NewEventIndication echo
    /// readable by this plugin's own receiver.
    /// </summary>
    internal static class ProxyTriggerEventCodec
    {
        // DataContractJsonSerializer instances are documented as thread-safe for concurrent
        // ReadObject/WriteObject calls, so one static instance is enough for the fetch path.
        private static readonly DataContractJsonSerializer EventSerializer =
            new DataContractJsonSerializer(typeof(AnalyticsEvent));

        internal static string ToWire(AnalyticsEvent analyticsEvent)
        {
            if (analyticsEvent == null)
            {
                throw new ArgumentNullException(nameof(analyticsEvent));
            }

            using (var stream = new MemoryStream())
            {
                EventSerializer.WriteObject(stream, analyticsEvent);
                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>Null for anything that is not a readable AnalyticsEvent JSON string.</summary>
        internal static AnalyticsEvent FromWire(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                {
                    return EventSerializer.ReadObject(stream) as AnalyticsEvent;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
