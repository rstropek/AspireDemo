using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

public static class Program
{
    private static readonly ActivitySource activitySource = new("AspireWalkthrough");

    public static async Task Main()
    {
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri("http://localhost:4317");
            })
            .AddSource(activitySource.Name)
            .Build();

        using (var stepsActivity = activitySource.StartActivity("Steps"))
        {
            await Task.Delay(100);
            using (var activity = activitySource.StartActivity("Step 1"))
            {
                await Task.Delay(150);
                activity?.SetTag("foo", 1);
                activity?.SetTag("bar", "Hello, World!");
                activity?.SetStatus(ActivityStatusCode.Ok);
            }

            using (var activity = activitySource.StartActivity("Step 2"))
            {
                await Task.Delay(250);
                activity?.SetTag("baz", 2);
                activity?.SetTag("qux", "Goodbye, World!");
                activity?.SetStatus(ActivityStatusCode.Error);
            }
        }
    }
}
