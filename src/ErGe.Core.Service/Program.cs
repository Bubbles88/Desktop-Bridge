using ErGe.Core.Actions;
using ErGe.Core.Policy;
using ErGe.Core.Runtime;
using ErGe.Core.Security;
using ErGe.Core.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string ServiceName = "ErGe Core";

if (args.Contains("--probe-once", StringComparer.OrdinalIgnoreCase))
{
    var policyStore = new FilePolicyStore(CorePaths.PolicyPath);
    var policyEngine = new PolicyEngine(policyStore);
    var statusStore = new CoreStatusStore(CorePaths.StatusPath);

    using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
    var worker = new CoreWorker(
        policyEngine,
        statusStore,
        loggerFactory.CreateLogger<CoreWorker>());

    worker.WriteHeartbeat();

    Console.WriteLine($"ERGE_CORE_PROBE_OK {CorePaths.StatusPath}");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ServiceName;
});

builder.Services.AddSingleton(new FilePolicyStore(CorePaths.PolicyPath));
builder.Services.AddSingleton<PolicyEngine>();
builder.Services.AddSingleton(new CoreStatusStore(CorePaths.StatusPath));
builder.Services.AddSingleton(new SessionAgentStatusStore(CorePaths.SessionStatusPath));
builder.Services.AddSingleton(new SessionOwnerStore(CorePaths.SessionOwnerPath));

builder.Services.AddSingleton<SessionAgentActionQueue>();
builder.Services.AddSingleton<IInteractiveCapabilityExecutor>(
    services => services.GetRequiredService<SessionAgentActionQueue>());
builder.Services.AddSingleton<ICapabilityHandler, ScreenInfoCapabilityHandler>();
builder.Services.AddSingleton<CapabilityBroker>();

builder.Services.AddHostedService<CoreWorker>();
builder.Services.AddHostedService<SessionAgentPipeWorker>();
builder.Services.AddHostedService<LocalProviderPipeWorker>();
builder.Services.AddHostedService<OwnerControlPipeWorker>();

var host = builder.Build();
await host.RunAsync();
