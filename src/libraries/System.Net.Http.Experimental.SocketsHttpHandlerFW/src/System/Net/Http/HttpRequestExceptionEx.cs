// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Http
{
    /// <summary>
    /// A port of <see cref="HttpRequestException"/> that adds additional properties available on modern .NET.
    /// </summary>
    public sealed class HttpRequestExceptionEx : HttpRequestException
    {
        internal RequestRetryType AllowRetry { get; } = RequestRetryType.NoRetry;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRequestException" /> class.
        /// </summary>
        public HttpRequestExceptionEx()
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRequestException" /> class with a specific message that describes the current exception.
        /// </summary>
        /// <param name="message">A message that describes the current exception.</param>
        public HttpRequestExceptionEx(string? message)
            : base(message)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRequestException" /> class with a specific message that describes the current exception and an inner exception.
        /// </summary>
        /// <param name="message">A message that describes the current exception.</param>
        /// <param name="inner">The inner exception.</param>
        public HttpRequestExceptionEx(string? message, Exception? inner)
            : base(message, inner)
        {
            if (inner != null)
            {
                HResult = inner.HResult;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRequestException" /> class with a specific message that describes the current exception, an inner exception, and an HTTP status code.
        /// </summary>
        /// <param name="message">A message that describes the current exception.</param>
        /// <param name="inner">The inner exception.</param>
        /// <param name="statusCode">The HTTP status code.</param>
        public HttpRequestExceptionEx(string? message, Exception? inner, HttpStatusCode? statusCode)
            : this(message, inner)
        {
            StatusCode = statusCode;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRequestException" /> class with a specific message an inner exception, and an HTTP status code and an <see cref="HttpRequestError"/>.
        /// </summary>
        /// <param name="httpRequestError">The <see cref="HttpRequestError"/> that caused the exception.</param>
        /// <param name="message">A message that describes the current exception.</param>
        /// <param name="inner">The inner exception.</param>
        /// <param name="statusCode">The HTTP status code.</param>
        public HttpRequestExceptionEx(HttpRequestError httpRequestError, string? message = null, Exception? inner = null, HttpStatusCode? statusCode = null)
            : this(message, inner, statusCode)
        {
            HttpRequestError = httpRequestError;
        }

        /// <summary>
        /// Gets the <see cref="Http.HttpRequestError"/> that caused the exception.
        /// </summary>
        public HttpRequestError HttpRequestError { get; }

        /// <summary>
        /// Gets the HTTP status code to be returned with the exception.
        /// </summary>
        /// <value>
        /// An HTTP status code if the exception represents a non-successful result, otherwise <c>null</c>.
        /// </value>
        public HttpStatusCode? StatusCode { get; }

        // This constructor is used internally to indicate that a request was not successfully sent due to an IOException,
        // and the exception occurred early enough so that the request may be retried on another connection.
        internal HttpRequestExceptionEx(HttpRequestError httpRequestError, string? message, Exception? inner, RequestRetryType allowRetry)
            : this(httpRequestError, message, inner)
        {
            AllowRetry = allowRetry;
        }
    }
}
