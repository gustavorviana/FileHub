using System;
using System.Net;
using FileHub;

namespace FileHub.OracleObjectStorage
{
    /// <summary>
    /// Thrown when an OCI Object Storage request fails with an error that does
    /// not map to a BCL-friendly exception. Carries the HTTP status, service
    /// code and opc-request-id for diagnostics; the raw SDK exception is
    /// available through <see cref="Exception.InnerException"/> but its type
    /// is not part of the public contract.
    /// </summary>
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
    }
}
