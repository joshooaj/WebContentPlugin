using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WebContent
{
    /// <summary>
    /// Wire codec for proxy-fetch payloads (#54 live-host fix). The platform's
    /// ExternalMessage.Data rehydrates the payload via
    /// "new DataContractSerializer(Type.GetType(DataClass))"; when the plugin assembly is not
    /// visible to the HOST process's type resolution (Smart Client loads plugins differently
    /// from Management Client), Type.GetType returns null, the platform swallows the resulting
    /// ArgumentNullException ("Value cannot be null"), and hands the receiver Data == null - a
    /// silent black hole that looks exactly like a lost response. Putting a plain string on the
    /// wire removes the type-binding requirement from every host: payloads travel as JSON
    /// strings, encoded/decoded only inside this plugin.
    /// </summary>
    internal static class ProxyFetchWireCodec
    {
        // DataContractJsonSerializer instances are documented as thread-safe for concurrent
        // ReadObject/WriteObject when the type is fixed at construction (as here): the fetcher
        // runs up to its concurrency cap on the thread pool and the client receives on the
        // messaging thread, all against these same instances.
        private static readonly DataContractJsonSerializer RequestSerializer =
            new DataContractJsonSerializer(typeof(ProxyFetchRequestPayload));

        private static readonly DataContractJsonSerializer FrameSerializer =
            new DataContractJsonSerializer(typeof(ProxyFetchResponsePayload));

        private static readonly DataContractJsonSerializer LeafRequestSerializer =
            new DataContractJsonSerializer(typeof(ProxyLeafRequestPayload));

        private static readonly DataContractJsonSerializer LeafResponseSerializer =
            new DataContractJsonSerializer(typeof(ProxyLeafResponsePayload));

        private static readonly DataContractJsonSerializer TunnelSerializer =
            new DataContractJsonSerializer(typeof(ProxyTunnelPayload));

        // Ticket #69: diagnostics payloads — same JSON-string-on-the-wire discipline.
        private static readonly DataContractJsonSerializer BlockReportSerializer =
            new DataContractJsonSerializer(typeof(ProxyBlockReportPayload));

        private static readonly DataContractJsonSerializer SessionEventSerializer =
            new DataContractJsonSerializer(typeof(ProxySessionEventPayload));

        private static readonly DataContractJsonSerializer StatusRequestSerializer =
            new DataContractJsonSerializer(typeof(ProxyStatusRequestPayload));

        private static readonly DataContractJsonSerializer StatusResponseSerializer =
            new DataContractJsonSerializer(typeof(ProxyStatusResponsePayload));

        // Ticket #75: deployment CA public-certificate export payloads.
        private static readonly DataContractJsonSerializer CaExportRequestSerializer =
            new DataContractJsonSerializer(typeof(ProxyCaExportRequestPayload));

        private static readonly DataContractJsonSerializer CaExportResponseSerializer =
            new DataContractJsonSerializer(typeof(ProxyCaExportResponsePayload));

        /// <summary>Encode a request payload for Message.Data.</summary>
        internal static string ToWire(ProxyFetchRequestPayload payload)
        {
            return Serialize(payload, RequestSerializer);
        }

        /// <summary>Encode one response frame for Message.Data.</summary>
        internal static string ToWire(ProxyFetchResponsePayload frame)
        {
            return Serialize(frame, FrameSerializer);
        }

        /// <summary>
        /// Decode a request payload from a received Message.Data. Null for anything that is not
        /// a decodable request JSON string (foreign/host values, garbage, version skew). Members
        /// added by later plugin versions are optional on read (explicit DataContract), so an
        /// older Smart Client's frame still decodes; a frame that decodes but names no url was
        /// never a fetch request, and stays an unusable frame.
        /// </summary>
        internal static ProxyFetchRequestPayload RequestFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            var payload = Deserialize<ProxyFetchRequestPayload>(json, RequestSerializer);
            if (payload == null || string.IsNullOrEmpty(payload.Url))
            {
                return null;
            }

            return payload;
        }

        /// <summary>Decode one response frame from a received Message.Data; null when unusable.</summary>
        internal static ProxyFetchResponsePayload FrameFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxyFetchResponsePayload>(json, FrameSerializer);
        }

        /// <summary>Encode a leaf-mint request for Message.Data (ADR-0003).</summary>
        internal static string ToWire(ProxyLeafRequestPayload request)
        {
            return Serialize(request, LeafRequestSerializer);
        }

        /// <summary>Encode a leaf-mint response for Message.Data (ADR-0003).</summary>
        internal static string ToWire(ProxyLeafResponsePayload response)
        {
            return Serialize(response, LeafResponseSerializer);
        }

        internal static ProxyLeafRequestPayload LeafRequestFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxyLeafRequestPayload>(json, LeafRequestSerializer);
        }

        internal static ProxyLeafResponsePayload LeafResponseFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxyLeafResponsePayload>(json, LeafResponseSerializer);
        }

        /// <summary>Encode one websocket-tunnel message (#57) for Message.Data.</summary>
        internal static string ToWire(ProxyTunnelPayload payload)
        {
            return Serialize(payload, TunnelSerializer);
        }

        /// <summary>Decode one websocket-tunnel message from Message.Data; null when unusable.</summary>
        internal static ProxyTunnelPayload TunnelFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxyTunnelPayload>(json, TunnelSerializer);
        }

        /// <summary>Encode a block report (#69) for Message.Data.</summary>
        internal static string ToWire(ProxyBlockReportPayload report)
        {
            return Serialize(report, BlockReportSerializer);
        }

        internal static ProxyBlockReportPayload BlockReportFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxyBlockReportPayload>(json, BlockReportSerializer);
        }

        /// <summary>Encode a session-lifecycle event (#69) for Message.Data.</summary>
        internal static string ToWire(ProxySessionEventPayload sessionEvent)
        {
            return Serialize(sessionEvent, SessionEventSerializer);
        }

        internal static ProxySessionEventPayload SessionEventFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxySessionEventPayload>(json, SessionEventSerializer);
        }

        /// <summary>Encode a workspace status query (#69) for Message.Data.</summary>
        internal static string ToWire(ProxyStatusRequestPayload request)
        {
            return Serialize(request, StatusRequestSerializer);
        }

        internal static ProxyStatusRequestPayload StatusRequestFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxyStatusRequestPayload>(json, StatusRequestSerializer);
        }

        /// <summary>Encode a workspace status snapshot (#69) for Message.Data.</summary>
        internal static string ToWire(ProxyStatusResponsePayload response)
        {
            return Serialize(response, StatusResponseSerializer);
        }

        internal static ProxyStatusResponsePayload StatusResponseFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxyStatusResponsePayload>(json, StatusResponseSerializer);
        }

        /// <summary>Encode a deployment CA export request (#75) for Message.Data.</summary>
        internal static string ToWire(ProxyCaExportRequestPayload request)
        {
            return Serialize(request, CaExportRequestSerializer);
        }

        internal static ProxyCaExportRequestPayload CaExportRequestFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxyCaExportRequestPayload>(json, CaExportRequestSerializer);
        }

        /// <summary>Encode a deployment CA export answer (#75) for Message.Data.</summary>
        internal static string ToWire(ProxyCaExportResponsePayload response)
        {
            return Serialize(response, CaExportResponseSerializer);
        }

        internal static ProxyCaExportResponsePayload CaExportResponseFromWire(object data)
        {
            var json = data as string;
            if (json == null)
            {
                return null;
            }

            return Deserialize<ProxyCaExportResponsePayload>(json, CaExportResponseSerializer);
        }

        private static string Serialize(object payload, DataContractJsonSerializer serializer)
        {
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, payload);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string json, DataContractJsonSerializer serializer) where T : class
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return serializer.ReadObject(stream) as T;
                }
            }
            catch (Exception)
            {
                // Malformed or foreign content: an unusable frame, not an exception on the
                // message-receiver thread.
                return null;
            }
        }
    }
}