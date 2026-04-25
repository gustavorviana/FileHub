using System;
using System.Net;
using FileHub;

namespace FileHub.AmazonS3
{
    /// <summary>
    /// Thrown when an S3 request fails with an error that does not map to a
    /// BCL-friendly exception (<see cref="System.IO.FileNotFoundException"/>,
    /// <see cref="UnauthorizedAccessException"/>, etc.). Carries the HTTP
    /// status, S3 error code and request id for diagnostics; the raw SDK
    /// exception is available through <see cref="Exception.InnerException"/>
    /// but its type is not part of the public contract.
    /// </summary>
    public sealed class S3DriverException : FileHubException
    {
        public HttpStatusCode? StatusCode { get; }
        public string ErrorCode { get; }
        public string RequestId { get; }

        public S3DriverException(string message, HttpStatusCode? statusCode, string errorCode, string requestId, Exception innerException)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
            RequestId = requestId;
        }
    }
}
