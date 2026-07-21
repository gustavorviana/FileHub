using Oci.Common.Model;
using System;
using System.IO;
using System.Net;

namespace FileHub.OracleObjectStorage.Internal;

/// <summary>
/// Translates exceptions produced by the OCI SDK into exceptions exposed
/// by FileHub.
/// </summary>
internal static class OciExceptionTranslator
{
    /// <summary>
    /// Determines whether the exception should be translated.
    /// Cancellation, invalid arguments, disposed objects and exceptions
    /// already translated by FileHub are preserved.
    /// </summary>
    public static bool ShouldTranslate(Exception exception)
    {
        if (exception == null)
            throw new ArgumentNullException(nameof(exception));

        return exception is not OperationCanceledException
            && exception is not ArgumentException
            && exception is not OciDriverException;
    }

    /// <summary>
    /// Translates an exception raised while operating on an object.
    /// </summary>
    public static Exception TranslateObject(Exception raw, string objectName, string bucket, string @namespace)
    {
        if (raw == null)
            throw new ArgumentNullException(nameof(raw));

        var ociException = raw as OciException;
        var statusCode = ociException?.StatusCode;
        var serviceCode = ociException?.ServiceCode;
        var opcRequestId = ociException?.OpcRequestId;

        if (IsObjectNotFound(raw, statusCode, serviceCode))
            return new FileNotFoundException(
                $"Object \"{objectName}\" was not found in bucket " +
                $"\"{bucket}\" (namespace \"{@namespace}\").",
                raw);

        if (IsUnauthorized(statusCode, serviceCode))
            return new UnauthorizedAccessException(
                $"Access denied for object \"{objectName}\" in bucket " +
                $"\"{bucket}\": {raw.Message}",
                raw);

        return CreateDriverException(
            raw,
            $"OCI request failed for object \"{objectName}\" " +
            $"in bucket \"{bucket}\"",
            statusCode,
            serviceCode,
            opcRequestId);
    }

    /// <summary>
    /// Translates an exception raised while inspecting or controlling an
    /// OCI work request.
    /// </summary>
    public static Exception TranslateWorkRequest(Exception raw, string workRequestId, string @namespace, string bucket, string region)
    {
        if (raw == null)
            throw new ArgumentNullException(nameof(raw));

        var ociException = raw as OciException;
        var statusCode = ociException?.StatusCode;
        var serviceCode = ociException?.ServiceCode;
        var opcRequestId = ociException?.OpcRequestId;

        var description =
            $"OCI work request \"{workRequestId}\" associated with " +
            $"bucket \"{bucket}\" in namespace \"{@namespace}\" " +
            $"and region \"{region}\"";

        if (statusCode == HttpStatusCode.NotFound)
            return CreateDriverException(
                raw,
                $"{description} was not found",
                statusCode,
                serviceCode,
                opcRequestId);

        if (IsUnauthorized(statusCode, serviceCode))
            return new UnauthorizedAccessException($"Access denied for {description}: {raw.Message}", raw);

        return CreateDriverException(
            raw,
            $"Request failed for {description}",
            statusCode,
            serviceCode,
            opcRequestId);
    }

    private static OciDriverException CreateDriverException(Exception raw, string message, HttpStatusCode? statusCode, string serviceCode, string opcRequestId)
    {
        var codePrefix = string.IsNullOrEmpty(serviceCode)
            ? string.Empty
            : $"[{serviceCode}] ";

        var requestIdSuffix = string.IsNullOrEmpty(opcRequestId)
            ? string.Empty
            : $" (opc-request-id={opcRequestId})";

        return new OciDriverException(
            $"{message}: {codePrefix}{raw.Message}{requestIdSuffix}",
            statusCode,
            serviceCode,
            opcRequestId,
            raw);
    }

    private static bool IsObjectNotFound(Exception raw, HttpStatusCode? statusCode, string serviceCode)
    {
        return statusCode == HttpStatusCode.NotFound
            || string.Equals(serviceCode, "BucketNotFound", StringComparison.Ordinal)
            || string.Equals(serviceCode, "NamespaceNotFound", StringComparison.Ordinal)
            || string.Equals(serviceCode, "ObjectNotFound", StringComparison.Ordinal)
            || MessageIndicatesNotFound(raw.Message);
    }

    private static bool IsUnauthorized(HttpStatusCode? statusCode, string serviceCode)
    {
        return statusCode == HttpStatusCode.Unauthorized
            || statusCode == HttpStatusCode.Forbidden
            || string.Equals(serviceCode, "NotAuthenticated", StringComparison.Ordinal)
            || string.Equals(serviceCode, "NotAuthorized", StringComparison.Ordinal)
            || string.Equals(serviceCode, "NotAuthorizedOrNotFound", StringComparison.Ordinal)
            || string.Equals(serviceCode, "SignatureDoesNotMatch", StringComparison.Ordinal);
    }

    private static bool MessageIndicatesNotFound(string message)
    {
        if (string.IsNullOrEmpty(message))
            return false;

        return message.Contains("was not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not exist in the namespace", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Not Found", StringComparison.OrdinalIgnoreCase);
    }
}