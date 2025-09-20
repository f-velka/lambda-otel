using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Runtime.Credentials;
using AwsSignatureVersion4;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Instrumentation.AWSLambda;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text.Json.Serialization;

namespace NativeAotOtel;

public class Function
{
    private static readonly TracerProvider tracerProvider = Sdk.CreateTracerProviderBuilder()
        .AddXRayTraceId()
        .SetResourceBuilder(
            ResourceBuilder.CreateDefault()
                .AddService(Environment.GetEnvironmentVariable("SERVICE_NAME") ?? "")
        )
        .AddAWSLambdaConfigurations()
        .AddAWSInstrumentation()
        .AddHttpClientInstrumentation()
        .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
        .AddOtlpExporter(o =>
        {
            // use AWS OTLP Endpoint
            // https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-OTLPEndpoint.html
            var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-northeast-1";
            o.Endpoint = new($"https://xray.{region}.amazonaws.com/v1/traces");
            o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
            o.HttpClientFactory = () =>
            {
                // sign by sigV4
                var credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials();
                var settings = new AwsSignatureHandlerSettings(region, "xray", credentials);
                return new HttpClient(
                    new AwsSignatureHandler(settings)
                    {
                        InnerHandler = new HttpClientHandler()
                    }
                );
            };
        })
        .Build();

    static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    /// <summary>
    /// The main entry point for the Lambda function. The main function is called once during the Lambda init phase. It
    /// initializes the .NET Lambda runtime client passing in the function handler to invoke for each Lambda event and
    /// the JSON serializer to use for converting Lambda JSON format to the .NET types.
    /// </summary>
    private static async Task Main()
    {
        var handler = FunctionHandler;
        await LambdaBootstrapBuilder.Create(handler, new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>())
            .Build()
            .RunAsync();
    }

    public static async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var headers = request.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in headers)
        {
            context.Logger.LogInformation($"{kv.Key}: {kv.Value}");
        }
        var parent = Propagator.Extract(default, headers, (c, key) =>
        {
            if (c.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                return [v];
            return [];
        });
        Baggage.Current = parent.Baggage;
        foreach (var kv in parent.Baggage.GetBaggage())
        {
            context.Logger.LogInformation($"{kv.Key}: {kv.Value}");
        }

        var response = await AWSLambdaWrapper.Trace(tracerProvider, OriginalFunctionHandler, request, context, parent.ActivityContext);
        // force flush before lambda freezes
        var flashSucceeded = tracerProvider.ForceFlush();
        if (!flashSucceeded)
        {
            context.Logger.LogWarning("failed to force flush");
        }

        return response;
    }

    /// <summary>
    /// A simple function that takes a string and does a ToUpper.
    ///
    /// To use this handler to respond to an AWS event, reference the appropriate package from
    /// https://github.com/aws/aws-lambda-dotnet#events
    /// and change the string input parameter to the desired event type. When the event type
    /// is changed, the handler type registered in the main method needs to be updated and the LambdaFunctionJsonSerializerContext
    /// defined below will need the JsonSerializable updated. If the return type and event type are different then the
    /// LambdaFunctionJsonSerializerContext must have two JsonSerializable attributes, one for each type.
    ///
    // When using Native AOT extra testing with the deployed Lambda functions is required to ensure
    // the libraries used in the Lambda function work correctly with Native AOT. If a runtime
    // error occurs about missing types or methods the most likely solution will be to remove references to trim-unsafe
    // code or configure trimming options. This sample defaults to partial TrimMode because currently the AWS
    // SDK for .NET does not support trimming. This will result in a larger executable size, and still does not
    // guarantee runtime trimming errors won't be hit.
    /// </summary>
    /// <param name="input">The event for the Lambda function handler to process.</param>
    /// <param name="context">The ILambdaContext that provides methods for logging and describing the Lambda environment.</param>
    /// <returns></returns>
    private static async Task<APIGatewayHttpApiV2ProxyResponse> OriginalFunctionHandler(APIGatewayHttpApiV2ProxyRequest input, ILambdaContext context)
    {
        // test
        var client = new HttpClient();
        var url = Environment.GetEnvironmentVariable("ENDPOINT_URL");
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        await client.GetAsync(url);
        return new();
    }
}

/// <summary>
/// This class is used to register the input event and return type for the FunctionHandler method with the System.Text.Json source generator.
/// There must be a JsonSerializable attribute for each type used as the input and return type or a runtime error will occur
/// from the JSON serializer unable to find the serialization information for unknown types.
/// </summary>
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyRequest))]
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyResponse))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext
{
    // By using this partial class derived from JsonSerializerContext, we can generate reflection free JSON Serializer code at compile time
    // which can deserialize our class and properties. However, we must attribute this class to tell it what types to generate serialization code for.
    // See https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-source-generation
}