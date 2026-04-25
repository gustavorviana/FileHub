using System;
using System.Net;
using FileHub;
#if !NET8_0_OR_GREATER
using System.Runtime.Serialization;
#endif

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
#if !NET8_0_OR_GREATER
    [Serializable]
#endif
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

#if !NET8_0_OR_GREATER
        private S3DriverException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            var status = info.GetInt32(nameof(StatusCode));
            StatusCode = status < 0 ? null : (HttpStatusCode?)status;
            ErrorCode = info.GetString(nameof(ErrorCode));
            RequestId = info.GetString(nameof(RequestId));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            base.GetObjectData(info, context);
            info.AddValue(nameof(StatusCode), StatusCode.HasValue ? (int)StatusCode.Value : -1);
            info.AddValue(nameof(ErrorCode), ErrorCode);
            info.AddValue(nameof(RequestId), RequestId);
        }
#endif
    }
}
