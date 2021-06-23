// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal abstract class TextMapPropagator
    {
        public delegate bool PropagatorGetterCallback(object carrier, string fieldName, out string? value);

        public abstract IReadOnlyCollection<string> Fields { get; }

        // Inject

        public abstract bool Inject(Activity activity, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter);
        public abstract bool Inject(ActivityContext context, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter);
        public abstract bool Inject(IEnumerable<KeyValuePair<string, string?>> baggage, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter);

        // Extract

        public abstract bool Extract(object carrier, PropagatorGetterCallback getter, out string? id, out string? state);
        public abstract bool Extract(object carrier, PropagatorGetterCallback getter, out ActivityContext context);
        public abstract bool Extract(object carrier, PropagatorGetterCallback getter, out IEnumerable<KeyValuePair<string, string?>>? baggage);

        //
        // Static APIs
        //

        public static TextMapPropagator Default { get; set; } = CreateLegacyPropagator();

        // For Microsoft compatibility. e.g., it will propagate Baggage header name as "Correlation-Context" instead of "baggage".
        public static TextMapPropagator CreateLegacyPropagator() => new LegacyTextMapPropagator();

        // Suppress context propagation.
        public static TextMapPropagator CreateOutputSuppressionPropagator() => new OutputSuppressionPropagator();

        // propagate only root parent context and ignore any intermediate created context.
        public static TextMapPropagator CreatePassThroughPropagator() => new PassThroughPropagator();

        // Conform to the W3C specs https://www.w3.org/TR/trace-context/ & https://www.w3.org/TR/2020/WD-baggage-20201020/
        public static TextMapPropagator CreateW3CPropagator() => new W3CPropagator();

        //
        // Internal
        //

        internal const char Space = ' ';
        internal const char Tab = (char)9;
        internal const char Comma = ',';
        internal const char Semicolon = ';';

        internal const int MaxBaggageLength = 8192;
        internal const int MaxKeyValueLength = 4096;
        internal const int MaxBaggageItems = 180;

        internal const string TraceParent = "traceparent";
        internal const string RequestId = "Request-Id";
        internal const string TraceState = "tracestate";
        internal const string Baggage = "baggage";
        internal const string CorrelationContext = "Correlation-Context";

        internal static readonly char[] s_trimmingSpaceCharacters = new char[] { Space, Tab };

        internal static void InjectBaggage(object carrier, IEnumerable<KeyValuePair<string, string?>> baggage, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter, bool injectAsW3C = false)
        {
            using (IEnumerator<KeyValuePair<string, string?>> e = baggage.GetEnumerator())
            {
                if (e.MoveNext())
                {
                    StringBuilder baggageList = new StringBuilder();
                    do
                    {
                        KeyValuePair<string, string?> item = e.Current;
                        baggageList.Append(WebUtility.UrlEncode(item.Key)).Append('=').Append(WebUtility.UrlEncode(item.Value)).Append(", ");
                    }
                    while (e.MoveNext());
                    baggageList.Length -= 2;
                    setter(carrier, injectAsW3C ? Baggage : CorrelationContext, baggageList.ToString());
                }
            }
        }

        internal static bool TryExtractBaggage(string baggagestring, out IEnumerable<KeyValuePair<string, string?>>? baggage)
        {
            baggage = null;
            int baggageLength = -1;
            List<KeyValuePair<string, string?>>? baggageList = null;

            if (string.IsNullOrEmpty(baggagestring))
            {
                return true;
            }

            int currentIndex = 0;

            do
            {
                // Skip spaces
                while (currentIndex < baggagestring.Length && (baggagestring[currentIndex] == Space || baggagestring[currentIndex] == Tab)) { currentIndex++; }

                if (currentIndex >= baggagestring.Length) { break; } // No Key exist

                int keyStart = currentIndex;

                // Search end of the key
                while (currentIndex < baggagestring.Length && baggagestring[currentIndex] != Space && baggagestring[currentIndex] != Tab && baggagestring[currentIndex] != '=') { currentIndex++; }

                if (currentIndex >= baggagestring.Length) { break; }

                int keyEnd = currentIndex;

                if (baggagestring[currentIndex] != '=')
                {
                    // Skip Spaces
                    while (currentIndex < baggagestring.Length && (baggagestring[currentIndex] == Space || baggagestring[currentIndex] == Tab)) { currentIndex++; }

                    if (currentIndex >= baggagestring.Length) { break; } // Wrong key format
                }

                if (baggagestring[currentIndex] != '=') { break; } // wrong key format.

                currentIndex++;

                // Skip spaces
                while (currentIndex < baggagestring.Length && (baggagestring[currentIndex] == Space || baggagestring[currentIndex] == Tab)) { currentIndex++; }

                if (currentIndex >= baggagestring.Length) { break; } // Wrong value format

                int valueStart = currentIndex;

                // Search end of the value
                while (currentIndex < baggagestring.Length && baggagestring[currentIndex] != Space && baggagestring[currentIndex] != Tab &&
                       baggagestring[currentIndex] != Comma && baggagestring[currentIndex] != Semicolon)
                { currentIndex++; }

                if (keyStart < keyEnd && valueStart < currentIndex)
                {
                    int keyValueLength = (keyEnd - keyStart) + (currentIndex - valueStart);
                    if (keyValueLength > MaxKeyValueLength || keyValueLength + baggageLength >= MaxBaggageLength)
                    {
                        break;
                    }

                    if (baggageList is null)
                    {
                        baggageList = new();
                    }

                    baggageLength += keyValueLength;

                    // Insert in reverse order for asp.net compatability.
                    baggageList.Insert(0, new KeyValuePair<string, string?>(
                                                WebUtility.UrlDecode(baggagestring.Substring(keyStart, keyEnd - keyStart)).Trim(s_trimmingSpaceCharacters),
                                                WebUtility.UrlDecode(baggagestring.Substring(valueStart, currentIndex - valueStart)).Trim(s_trimmingSpaceCharacters)));

                    if (baggageList.Count >= MaxBaggageItems)
                    {
                        break;
                    }
                }

                // Skip to end of values
                while (currentIndex < baggagestring.Length && baggagestring[currentIndex] != Comma) { currentIndex++; }

                currentIndex++; // Move to next key-value entry
            } while (currentIndex < baggagestring.Length);

            baggage = baggageList;
            return baggageList != null;
        }

        internal static bool InjectContext(ActivityContext context, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter)
        {
            if (context == default || setter is null || context.TraceId == default || context.SpanId == default)
            {
                return false;
            }

            Span<char> traceParent = stackalloc char[55];
            traceParent[0] = '0';
            traceParent[1] = '0';
            traceParent[2] = '-';
            traceParent[35] = '-';
            traceParent[52] = '-';
            CopyStringToSpan(context.TraceId.ToHexString(), traceParent.Slice(3, 32));
            CopyStringToSpan(context.SpanId.ToHexString(), traceParent.Slice(36, 16));
            HexConverter.ToCharsBuffer((byte)(context.TraceFlags & ActivityTraceFlags.Recorded), traceParent.Slice(53, 2), 0, HexConverter.Casing.Lower);

            setter(carrier, TraceParent, traceParent.ToString());

            string? tracestateStr = context.TraceState;
            if (tracestateStr?.Length > 0)
            {
                setter(carrier, TraceState, tracestateStr);
            }

            return true;
        }

        internal static bool LegacyExtract(object carrier, PropagatorGetterCallback getter, out string? id, out string? state)
        {
            if (getter is null)
            {
                id = null;
                state = null;
                return false;
            }

            getter(carrier, TraceParent, out id);
            if (id is null)
            {
                getter(carrier, RequestId, out id);
            }

            getter(carrier, TraceState, out state);
            return true;
        }

        internal static bool LegacyExtract(object carrier, PropagatorGetterCallback getter, out ActivityContext context)
        {
            context = default;

            if (getter is null)
            {
                return false;
            }

            getter(carrier, TraceParent, out string? traceParent);
            getter(carrier, TraceState, out string? traceState);

            return ActivityContext.TryParse(traceParent, traceState, out context);
        }

        internal static bool LegacyExtract(object carrier, PropagatorGetterCallback getter, out IEnumerable<KeyValuePair<string, string?>>? baggage)
        {
            baggage = null;
            if (getter is null)
            {
                return false;
            }

            getter(carrier, Baggage, out string? theBaggage);
            if (theBaggage is null || !TryExtractBaggage(theBaggage, out baggage))
            {
                getter(carrier, CorrelationContext, out theBaggage);
                if (theBaggage is not null)
                {
                    TryExtractBaggage(theBaggage, out baggage);
                }
            }

            return true;
        }

        internal static void CopyStringToSpan(string s, Span<char> span)
        {
            Debug.Assert(s is not null);
            Debug.Assert(s.Length == span.Length);

            for (int i = 0; i < s.Length; i++)
            {
                span[i] = s[i];
            }
        }
    }

    internal class LegacyTextMapPropagator : TextMapPropagator
    {
        //
        // Fields
        //

        public override IReadOnlyCollection<string> Fields { get; } = new HashSet<string>() { TraceParent, RequestId, TraceState, Baggage, CorrelationContext };

        //
        // Inject
        //

        public override bool Inject(Activity activity, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter)
        {
            if (activity is null || setter == default)
            {
                return false;
            }

            string? id = activity.Id;
            if (id is null)
            {
                return false;
            }

            if (activity.IdFormat == ActivityIdFormat.W3C)
            {
                setter(carrier, TraceParent, id);
                if (activity.TraceStateString is not null)
                {
                    setter(carrier, TraceState, activity.TraceStateString);
                }
            }
            else
            {
                setter(carrier, RequestId, id);
            }

            InjectBaggage(carrier, activity.Baggage, setter);

            return true;
        }

        public override bool Inject(ActivityContext context, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter) =>
                                InjectContext(context, carrier, setter);

        public override bool Inject(IEnumerable<KeyValuePair<string, string?>> baggage, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter)
        {
            if (setter is null)
            {
                return false;
            }

            if (baggage is null)
            {
                return true; // nothing need to be done
            }

            InjectBaggage(carrier, baggage, setter);
            return true;
        }

        //
        // Extract
        //

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out string? id, out string? state) =>
                                LegacyExtract(carrier, getter, out id, out state);

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out ActivityContext context) =>
                                LegacyExtract(carrier, getter, out context);

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out IEnumerable<KeyValuePair<string, string?>>? baggage) =>
                                LegacyExtract(carrier, getter, out baggage);
    }

    internal class PassThroughPropagator : TextMapPropagator
    {
        //
        // Fields
        //
        public override IReadOnlyCollection<string> Fields { get; } = new HashSet<string>() { TraceParent, RequestId, TraceState, Baggage, CorrelationContext };

        // Inject

        public override bool Inject(Activity activity, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter)
        {
            GetRootId(out string? parentId, out string? traceState, out bool isW3c);
            if (parentId is null)
            {
                return true;
            }

            setter(carrier, isW3c ? TraceParent : RequestId, parentId);

            if (traceState is not null)
            {
                setter(carrier, TraceState, traceState);
            }

            return true;
        }

        public override bool Inject(ActivityContext context, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter) =>
            Inject((Activity)null!, carrier, setter);

        public override bool Inject(IEnumerable<KeyValuePair<string, string?>> baggage, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter)
        {
            IEnumerable<KeyValuePair<string, string?>>? parentBaggage = GetRootBaggage();

            if (parentBaggage is not null)
            {
                InjectBaggage(carrier, parentBaggage, setter);
            }

            return true;
        }

        //
        // Extract
        //

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out string? id, out string? state) =>
                                LegacyExtract(carrier, getter, out id, out state);

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out ActivityContext context) =>
                                LegacyExtract(carrier, getter, out context);

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out IEnumerable<KeyValuePair<string, string?>>? baggage) =>
                                LegacyExtract(carrier, getter, out baggage);

        private static void GetRootId(out string? parentId, out string? traceState, out bool isW3c)
        {
            Activity? activity = Activity.Current;
            if (activity is null)
            {
                parentId = null;
                traceState = null;
                isW3c = false;
                return;
            }

            while (activity is not null && activity.Parent is not null)
            {
                activity = activity.Parent;
            }

            traceState = activity?.TraceStateString;
            parentId = activity?.ParentId ?? activity?.Id;
            isW3c = activity?.IdFormat == ActivityIdFormat.W3C;
        }

        private static IEnumerable<KeyValuePair<string, string?>>? GetRootBaggage()
        {
            Activity? activity = Activity.Current;
            if (activity is null)
            {
                return null;
            }

            while (activity is not null && activity.Parent is not null)
            {
                activity = activity.Parent;
            }

            return activity?.Baggage;
        }
    }

    internal class OutputSuppressionPropagator : TextMapPropagator
    {
        //
        // Fields
        //
        public override IReadOnlyCollection<string> Fields { get; } = new HashSet<string>() { TraceParent, RequestId, TraceState, Baggage, CorrelationContext };

        // Inject

        public override bool Inject(Activity activity, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter) => true;
        public override bool Inject(ActivityContext context, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter) => true;
        public override bool Inject(IEnumerable<KeyValuePair<string, string?>> baggage, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter) => true;

        // Extract

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out string? id, out string? state) =>
                                LegacyExtract(carrier, getter, out id, out state);

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out ActivityContext context) =>
                                LegacyExtract(carrier, getter, out context);

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out IEnumerable<KeyValuePair<string, string?>>? baggage) =>
                                LegacyExtract(carrier, getter, out baggage);
    }

    internal class W3CPropagator : TextMapPropagator
    {
        //
        // Fields
        //

        public override IReadOnlyCollection<string> Fields { get; } = new HashSet<string>() { TraceParent, TraceState, Baggage };

        //
        // Inject
        //

        public override bool Inject(Activity activity, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter)
        {
            if (activity is null || setter == default || activity.IdFormat != ActivityIdFormat.W3C)
            {
                return false;
            }

            string? id = activity.Id;
            if (id is null)
            {
                return false;
            }

            setter(carrier, TraceParent, id);
            if (activity.TraceStateString is not null)
            {
                setter(carrier, TraceState, activity.TraceStateString);
            }

            InjectBaggage(carrier, activity.Baggage, setter, injectAsW3C: true);

            return true;
        }

        public override bool Inject(ActivityContext context, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter) =>
                                InjectContext(context, carrier, setter);

        public override bool Inject(IEnumerable<KeyValuePair<string, string?>> baggage, object carrier, Action<object /* carrier */, string /* field name */, string /* value to inject */> setter)
        {
            if (setter is null)
            {
                return false;
            }

            if (baggage is null)
            {
                return true; // nothing need to be done
            }

            InjectBaggage(carrier, baggage, setter, true);
            return true;
        }

        //
        // Extract
        //

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out string? id, out string? state)
        {
            if (getter is null)
            {
                id = null;
                state = null;
                return false;
            }

            getter(carrier, TraceParent, out id);
            getter(carrier, TraceState, out state);

            if (id is not null && !ActivityContext.TryParse(id, state, out ActivityContext context))
            {
                return false;
            }

            return true;
        }

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out ActivityContext context) =>
                                LegacyExtract(carrier, getter, out context);

        public override bool Extract(object carrier, PropagatorGetterCallback getter, out IEnumerable<KeyValuePair<string, string?>>? baggage)
        {
            baggage = null;
            if (getter is null)
            {
                return false;
            }

            getter(carrier, Baggage, out string? theBaggage);

            if (theBaggage is not null)
            {
                return TryExtractBaggage(theBaggage, out baggage);
            }

            return true;
        }
    }

    /// <summary>
    /// DiagnosticHandler notifies DiagnosticSource subscribers about outgoing Http requests
    /// </summary>
    internal sealed class DiagnosticsHandler : DelegatingHandler
    {
        private const string Namespace                      = "System.Net.Http";
        private const string RequestWriteNameDeprecated     = Namespace + ".Request";
        private const string ResponseWriteNameDeprecated    = Namespace + ".Response";
        private const string ExceptionEventName             = Namespace + ".Exception";
        private const string ActivityName                   = Namespace + ".HttpRequestOut";
        private const string ActivityStartName              = ActivityName + ".Start";
        private const string ActivityStopName               = ActivityName + ".Stop";

        private static readonly DiagnosticListener s_diagnosticListener = new("HttpHandlerDiagnosticListener");
        private static readonly ActivitySource s_activitySource = new(Namespace);

        public static bool IsGloballyEnabled { get; } = GetEnableActivityPropagationValue();

        private static bool GetEnableActivityPropagationValue()
        {
            // First check for the AppContext switch, giving it priority over the environment variable.
            if (AppContext.TryGetSwitch(Namespace + ".EnableActivityPropagation", out bool enableActivityPropagation))
            {
                return enableActivityPropagation;
            }

            // AppContext switch wasn't used. Check the environment variable to determine which handler should be used.
            string? envVar = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_ENABLEACTIVITYPROPAGATION");
            if (envVar != null && (envVar.Equals("false", StringComparison.OrdinalIgnoreCase) || envVar.Equals("0")))
            {
                // Suppress Activity propagation.
                return false;
            }

            // Defaults to enabling Activity propagation.
            return true;
        }

        public DiagnosticsHandler(HttpMessageHandler innerHandler) : base(innerHandler)
        {
            Debug.Assert(IsGloballyEnabled);
        }

        private static bool ShouldLogDiagnostics(HttpRequestMessage request, out Activity? activity)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request), SR.net_http_handler_norequest);
            }

            activity = null;

            if (s_activitySource.HasListeners())
            {
                activity = s_activitySource.CreateActivity(ActivityName, ActivityKind.Client);
            }

            if (activity is null)
            {
                bool diagnosticListenerEnabled = s_diagnosticListener.IsEnabled();

                if (Activity.Current is not null || (diagnosticListenerEnabled && s_diagnosticListener.IsEnabled(ActivityName, request)))
                {
                    // If a diagnostics listener is enabled for the Activity, always create one
                    activity = new Activity(ActivityName);
                }
                else
                {
                    // There is no Activity, but we may still want to use the instrumented SendAsyncCore if diagnostic listeners are interested in other events
                    return diagnosticListenerEnabled;
                }
            }

            activity.Start();

            if (s_diagnosticListener.IsEnabled(ActivityStartName))
            {
                Write(ActivityStartName, new ActivityStartData(request));
            }

            TextMapPropagator.Default.Inject(activity, request, static (carrier, key, value) =>
            {
                HttpRequestHeaders headers = ((HttpRequestMessage)carrier).Headers;
                if (!headers.NonValidated.Contains(key))
                {
                    headers.TryAddWithoutValidation(key, value);
                }
            });

            return true;
        }

        protected internal override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ShouldLogDiagnostics(request, out Activity? activity))
            {
                ValueTask<HttpResponseMessage> sendTask = SendAsyncCore(request, activity, async: false, cancellationToken);
                return sendTask.IsCompleted ?
                    sendTask.Result :
                    sendTask.AsTask().GetAwaiter().GetResult();
            }
            else
            {
                return base.Send(request, cancellationToken);
            }
        }

        protected internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ShouldLogDiagnostics(request, out Activity? activity))
            {
                return SendAsyncCore(request, activity, async: true, cancellationToken).AsTask();
            }
            else
            {
                return base.SendAsync(request, cancellationToken);
            }
        }

        private async ValueTask<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request, Activity? activity, bool async, CancellationToken cancellationToken)
        {
            Guid loggingRequestId = default;

            if (s_diagnosticListener.IsEnabled(RequestWriteNameDeprecated))
            {
                loggingRequestId = Guid.NewGuid();
                Write(RequestWriteNameDeprecated, new RequestData(request, loggingRequestId, Stopwatch.GetTimestamp()));
            }

            HttpResponseMessage? response = null;
            TaskStatus taskStatus = TaskStatus.RanToCompletion;
            try
            {
                response = async ?
                    await base.SendAsync(request, cancellationToken).ConfigureAwait(false) :
                    base.Send(request, cancellationToken);
                return response;
            }
            catch (OperationCanceledException)
            {
                taskStatus = TaskStatus.Canceled;
                throw;
            }
            catch (Exception ex)
            {
                if (s_diagnosticListener.IsEnabled(ExceptionEventName))
                {
                    Write(ExceptionEventName, new ExceptionData(ex, request));
                }

                taskStatus = TaskStatus.Faulted;
                throw;
            }
            finally
            {
                if (activity is not null)
                {
                    activity.SetEndTime(DateTime.UtcNow);

                    if (s_diagnosticListener.IsEnabled(ActivityStopName))
                    {
                        Write(ActivityStopName, new ActivityStopData(response, request, taskStatus));
                    }

                    activity.Stop();
                }

                if (s_diagnosticListener.IsEnabled(ResponseWriteNameDeprecated))
                {
                    Write(ResponseWriteNameDeprecated, new ResponseData(response, loggingRequestId, Stopwatch.GetTimestamp(), taskStatus));
                }
            }
        }

        private sealed class ActivityStartData
        {
            // matches the properties selected in https://github.com/dotnet/diagnostics/blob/ffd0254da3bcc47847b1183fa5453c0877020abd/src/Microsoft.Diagnostics.Monitoring.EventPipe/Configuration/HttpRequestSourceConfiguration.cs#L36-L40
            [DynamicDependency(nameof(HttpRequestMessage.RequestUri), typeof(HttpRequestMessage))]
            [DynamicDependency(nameof(HttpRequestMessage.Method), typeof(HttpRequestMessage))]
            [DynamicDependency(nameof(HttpRequestMessage.RequestUri), typeof(HttpRequestMessage))]
            [DynamicDependency(nameof(Uri.Host), typeof(Uri))]
            [DynamicDependency(nameof(Uri.Port), typeof(Uri))]
            internal ActivityStartData(HttpRequestMessage request)
            {
                Request = request;
            }

            public HttpRequestMessage Request { get; }

            public override string ToString() => $"{{ {nameof(Request)} = {Request} }}";
        }

        private sealed class ActivityStopData
        {
            internal ActivityStopData(HttpResponseMessage? response, HttpRequestMessage request, TaskStatus requestTaskStatus)
            {
                Response = response;
                Request = request;
                RequestTaskStatus = requestTaskStatus;
            }

            public HttpResponseMessage? Response { get; }
            public HttpRequestMessage Request { get; }
            public TaskStatus RequestTaskStatus { get; }

            public override string ToString() => $"{{ {nameof(Response)} = {Response}, {nameof(Request)} = {Request}, {nameof(RequestTaskStatus)} = {RequestTaskStatus} }}";
        }

        private sealed class ExceptionData
        {
            // preserve the same properties as ActivityStartData above + common Exception properties
            [DynamicDependency(nameof(HttpRequestMessage.RequestUri), typeof(HttpRequestMessage))]
            [DynamicDependency(nameof(HttpRequestMessage.Method), typeof(HttpRequestMessage))]
            [DynamicDependency(nameof(HttpRequestMessage.RequestUri), typeof(HttpRequestMessage))]
            [DynamicDependency(nameof(Uri.Host), typeof(Uri))]
            [DynamicDependency(nameof(Uri.Port), typeof(Uri))]
            [DynamicDependency(nameof(System.Exception.Message), typeof(Exception))]
            [DynamicDependency(nameof(System.Exception.StackTrace), typeof(Exception))]
            internal ExceptionData(Exception exception, HttpRequestMessage request)
            {
                Exception = exception;
                Request = request;
            }

            public Exception Exception { get; }
            public HttpRequestMessage Request { get; }

            public override string ToString() => $"{{ {nameof(Exception)} = {Exception}, {nameof(Request)} = {Request} }}";
        }

        private sealed class RequestData
        {
            // preserve the same properties as ActivityStartData above
            [DynamicDependency(nameof(HttpRequestMessage.RequestUri), typeof(HttpRequestMessage))]
            [DynamicDependency(nameof(HttpRequestMessage.Method), typeof(HttpRequestMessage))]
            [DynamicDependency(nameof(HttpRequestMessage.RequestUri), typeof(HttpRequestMessage))]
            [DynamicDependency(nameof(Uri.Host), typeof(Uri))]
            [DynamicDependency(nameof(Uri.Port), typeof(Uri))]
            internal RequestData(HttpRequestMessage request, Guid loggingRequestId, long timestamp)
            {
                Request = request;
                LoggingRequestId = loggingRequestId;
                Timestamp = timestamp;
            }

            public HttpRequestMessage Request { get; }
            public Guid LoggingRequestId { get; }
            public long Timestamp { get; }

            public override string ToString() => $"{{ {nameof(Request)} = {Request}, {nameof(LoggingRequestId)} = {LoggingRequestId}, {nameof(Timestamp)} = {Timestamp} }}";
        }

        private sealed class ResponseData
        {
            [DynamicDependency(nameof(HttpResponseMessage.StatusCode), typeof(HttpResponseMessage))]
            internal ResponseData(HttpResponseMessage? response, Guid loggingRequestId, long timestamp, TaskStatus requestTaskStatus)
            {
                Response = response;
                LoggingRequestId = loggingRequestId;
                Timestamp = timestamp;
                RequestTaskStatus = requestTaskStatus;
            }

            public HttpResponseMessage? Response { get; }
            public Guid LoggingRequestId { get; }
            public long Timestamp { get; }
            public TaskStatus RequestTaskStatus { get; }

            public override string ToString() => $"{{ {nameof(Response)} = {Response}, {nameof(LoggingRequestId)} = {LoggingRequestId}, {nameof(Timestamp)} = {Timestamp}, {nameof(RequestTaskStatus)} = {RequestTaskStatus} }}";
        }

        private static void InjectHeaders(Activity currentActivity, HttpRequestMessage request)
        {
            const string TraceParentHeaderName = "traceparent";
            const string TraceStateHeaderName = "tracestate";
            const string RequestIdHeaderName = "Request-Id";
            const string CorrelationContextHeaderName = "Correlation-Context";

            if (currentActivity.IdFormat == ActivityIdFormat.W3C)
            {
                if (!request.Headers.Contains(TraceParentHeaderName))
                {
                    request.Headers.TryAddWithoutValidation(TraceParentHeaderName, currentActivity.Id);
                    if (currentActivity.TraceStateString != null)
                    {
                        request.Headers.TryAddWithoutValidation(TraceStateHeaderName, currentActivity.TraceStateString);
                    }
                }
            }
            else
            {
                if (!request.Headers.Contains(RequestIdHeaderName))
                {
                    request.Headers.TryAddWithoutValidation(RequestIdHeaderName, currentActivity.Id);
                }
            }

            // we expect baggage to be empty or contain a few items
            using (IEnumerator<KeyValuePair<string, string?>> e = currentActivity.Baggage.GetEnumerator())
            {
                if (e.MoveNext())
                {
                    var baggage = new List<string>();
                    do
                    {
                        KeyValuePair<string, string?> item = e.Current;
                        baggage.Add(new NameValueHeaderValue(WebUtility.UrlEncode(item.Key), WebUtility.UrlEncode(item.Value)).ToString());
                    }
                    while (e.MoveNext());
                    request.Headers.TryAddWithoutValidation(CorrelationContextHeaderName, baggage);
                }
            }
        }

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:UnrecognizedReflectionPattern",
            Justification = "The values being passed into Write have the commonly used properties being preserved with DynamicDependency.")]
        private static void Write<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string name, T value)
        {
            s_diagnosticListener.Write(name, value);
        }
    }
}
