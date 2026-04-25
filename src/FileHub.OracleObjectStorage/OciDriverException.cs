using System;
using System.Net;
using FileHub;
#if !NET8_0_OR_GREATER
using System.Runtime.Serialization;
#endif

namespace FileHub.OracleObjectStorage
{
    /// <summary>
    /// Thrown when an OCI Object Storage request fails with an error that does
    /// not map to a BCL-friendly exception. Carries the HTTP status, service
    /// code and opc-request-id for diagnostics; the raw SDK exception is
    /// available through <see cref="Exception.InnerException"/> but its type
    /// is not part of the public contract.
    /// </summary>
#if !NET8_0_OR_GREATER
    [Serializable]
#endif
    public sealed class OciDriverException : FileHubException
    {
        public HttpStatusCode? StatusCode { get; }
        public string ServiceCode { get; }
        public string OpcRequestId { get; }

        public OciDriverException(string message, HttpStatusCode? statusCode, string serviceCode, string opcRequestId, Exception innerException)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            ServiceCode = serviceCode;
            OpcRequestId = opcRequestId;
        }

#if !NET8_0_OR_GREATER
        private OciDriverException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            var status = info.GetInt32(nameof(StatusCode));
            StatusCode = status < 0 ? null : (HttpStatusCode?)status;
            ServiceCode = info.GetString(nameof(ServiceCode));
            OpcRequestId = info.GetString(nameof(OpcRequestId));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            base.GetObjectData(info, context);
            info.AddValue(nameof(StatusCode), StatusCode.HasValue ? (int)StatusCode.Value : -1);
            info.AddValue(nameof(ServiceCode), ServiceCode);
            info.AddValue(nameof(OpcRequestId), OpcRequestId);
        }
#endif
    }
}
