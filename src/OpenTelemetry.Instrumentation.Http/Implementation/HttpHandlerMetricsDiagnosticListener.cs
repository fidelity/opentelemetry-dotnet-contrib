// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
#if NETFRAMEWORK
using System.Net.Http;
#endif
using System.Reflection;
using OpenTelemetry.Internal;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Instrumentation.Http.Implementation;

internal sealed class HttpHandlerMetricsDiagnosticListener : ListenerHandler
{
    internal const string OnStopEvent = "System.Net.Http.HttpRequestOut.Stop";

    internal static readonly AssemblyName AssemblyName = typeof(HttpClientMetrics).Assembly.GetName();
    internal static readonly string MeterName = AssemblyName.Name!;
    internal static readonly string MeterVersion = AssemblyName.Version!.ToString();
    internal static readonly Meter Meter = new(MeterName, MeterVersion);
    private const string OnUnhandledExceptionEvent = "System.Net.Http.Exception";
    private static readonly Histogram<double> HttpClientRequestDuration = Meter.CreateHistogram(
        "http.client.request.duration",
        unit: "s",
        description: "Duration of HTTP client requests.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10] });

    private static readonly PropertyFetcher<HttpRequestMessage> StopRequestFetcher = new("Request");
    private static readonly PropertyFetcher<HttpResponseMessage> StopResponseFetcher = new("Response");
    private static readonly PropertyFetcher<Exception> StopExceptionFetcher = new("Exception");
    private static readonly PropertyFetcher<HttpRequestMessage> RequestFetcher = new("Request");
#if NET
    private static readonly HttpRequestOptionsKey<string?> HttpRequestOptionsErrorKey = new(SemanticConventions.AttributeErrorType);
#endif

    public HttpHandlerMetricsDiagnosticListener(string name)
        : base(name)
    {
    }

    public static void OnStopEventWritten(Activity activity, object? payload)
    {
        if (TryFetchRequest(payload, out var request))
        {
            // see the spec https://github.com/open-telemetry/semantic-conventions/blob/v1.23.0/docs/http/http-metrics.md
            TagList tags = default;

            var httpMethod = HttpTagHelper.RequestDataHelper.GetNormalizedHttpMethod(request.Method.Method);
            tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeHttpRequestMethod, httpMethod));

            if (request.RequestUri != null)
            {
                tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeServerAddress, request.RequestUri.Host));
                tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeUrlScheme, request.RequestUri.Scheme));

                if (!request.RequestUri.IsDefaultPort)
                {
                    tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeServerPort, request.RequestUri.Port));
                }
            }

            if (TryFetchResponse(payload, out var response))
            {
                tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeNetworkProtocolVersion, RequestDataHelper.GetHttpProtocolVersion(response.Version)));
                tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeHttpResponseStatusCode, TelemetryHelper.GetBoxedStatusCode(response.StatusCode)));

                // Set error.type to status code for failed requests
                // https://github.com/open-telemetry/semantic-conventions/blob/v1.23.0/docs/http/http-spans.md#common-attributes
                if (SpanHelper.ResolveActivityStatusForHttpStatusCode(ActivityKind.Client, (int)response.StatusCode) == ActivityStatusCode.Error)
                {
                    tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeErrorType, TelemetryHelper.GetStatusCodeString(response.StatusCode)));
                }
            }

            if (response == null)
            {
#if !NET
                request.Properties.TryGetValue(SemanticConventions.AttributeErrorType, out var errorType);
#else
                request.Options.TryGetValue(HttpRequestOptionsErrorKey, out var errorType);
#endif

                // Set error.type to exception type if response was not received.
                // https://github.com/open-telemetry/semantic-conventions/blob/v1.23.0/docs/http/http-spans.md#common-attributes
                if (errorType != null)
                {
                    tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeErrorType, errorType));
                }
            }

            // We are relying here on HttpClient library to set duration before writing the stop event.
            // https://github.com/dotnet/runtime/blob/90603686d314147017c8bbe1fa8965776ce607d0/src/libraries/System.Net.Http/src/System/Net/Http/DiagnosticsHandler.cs#L178
            // TODO: Follow up with .NET team if we can continue to rely on this behavior.
            HttpClientRequestDuration.Record(activity.Duration.TotalSeconds, tags);
        }

        // The AOT-annotation DynamicallyAccessedMembers in System.Net.Http library ensures that top-level properties on the payload object are always preserved.
        // see https://github.com/dotnet/runtime/blob/f9246538e3d49b90b0e9128d7b1defef57cd6911/src/libraries/System.Net.Http/src/System/Net/Http/DiagnosticsHandler.cs#L325
#if NET
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The System.Net.Http library guarantees that top-level properties are preserved")]
#endif
        static bool TryFetchRequest(object? payload, [NotNullWhen(true)] out HttpRequestMessage? request)
        {
            return StopRequestFetcher.TryFetch(payload, out request) && request != null;
        }

        // The AOT-annotation DynamicallyAccessedMembers in System.Net.Http library ensures that top-level properties on the payload object are always preserved.
        // see https://github.com/dotnet/runtime/blob/f9246538e3d49b90b0e9128d7b1defef57cd6911/src/libraries/System.Net.Http/src/System/Net/Http/DiagnosticsHandler.cs#L325
#if NET
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The System.Net.Http library guarantees that top-level properties are preserved")]
#endif
        static bool TryFetchResponse(object? payload, [NotNullWhen(true)] out HttpResponseMessage? response)
        {
            return StopResponseFetcher.TryFetch(payload, out response) && response != null;
        }
    }

    public static void OnExceptionEventWritten(Activity activity, object? payload)
    {
        if (!TryFetchException(payload, out var exc) || !TryFetchRequest(payload, out var request))
        {
            HttpInstrumentationEventSource.Log.NullPayload(nameof(HttpHandlerMetricsDiagnosticListener), nameof(OnExceptionEventWritten));
            return;
        }

#if !NET
        request.Properties.Add(SemanticConventions.AttributeErrorType, exc.GetType().FullName);
#else
        request.Options.Set(HttpRequestOptionsErrorKey, exc.GetType().FullName);
#endif

        // The AOT-annotation DynamicallyAccessedMembers in System.Net.Http library ensures that top-level properties on the payload object are always preserved.
        // see https://github.com/dotnet/runtime/blob/f9246538e3d49b90b0e9128d7b1defef57cd6911/src/libraries/System.Net.Http/src/System/Net/Http/DiagnosticsHandler.cs#L325
#if NET
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The System.Net.Http library guarantees that top-level properties are preserved")]
#endif
        static bool TryFetchException(object? payload, [NotNullWhen(true)] out Exception? exc)
        {
            return StopExceptionFetcher.TryFetch(payload, out exc) && exc != null;
        }

        // The AOT-annotation DynamicallyAccessedMembers in System.Net.Http library ensures that top-level properties on the payload object are always preserved.
        // see https://github.com/dotnet/runtime/blob/f9246538e3d49b90b0e9128d7b1defef57cd6911/src/libraries/System.Net.Http/src/System/Net/Http/DiagnosticsHandler.cs#L325
#if NET
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The System.Net.Http library guarantees that top-level properties are preserved")]
#endif
        static bool TryFetchRequest(object? payload, [NotNullWhen(true)] out HttpRequestMessage? request)
        {
            return RequestFetcher.TryFetch(payload, out request) && request != null;
        }
    }

    public override void OnEventWritten(string name, object? payload)
    {
        var activity = Activity.Current!;

        if (name == OnStopEvent)
        {
            OnStopEventWritten(activity, payload);
        }
        else if (name == OnUnhandledExceptionEvent)
        {
            OnExceptionEventWritten(activity, payload);
        }
    }
}
