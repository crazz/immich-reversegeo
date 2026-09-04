using System;
using System.Collections.Generic;
using System.Linq;
using ImmichReverseGeo.Web.ApplicationRole;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var workerErrorWriter = Console.Error;

ApplicationRoleStartup.Begin(
    args,
    workerErrorWriter,
    RunWebApplication,
    RunInternalWorker,
    _ => Environment.ExitCode = InternalWorkerProcess.CompleteInvalidInvocation(workerErrorWriter));

void RunInternalWorker(IReadOnlyList<string> selectedArguments)
{
    Environment.ExitCode = InternalWorkerProcess.Run(
        selectedArguments,
        workerErrorWriter,
        InternalWorkerHost.RunProductionAsync);
}

void RunWebApplication(IReadOnlyList<string> selectedArguments)
{
    var builder = WebApplication.CreateBuilder(selectedArguments.ToArray());
    var environment = builder.Environment.IsDevelopment()
        ? CompositionEnvironment.Development
        : CompositionEnvironment.Production;
    var context = ApplicationCompositionContext.Create(
        environment,
        builder.Environment.ContentRootPath,
        Environment.GetEnvironmentVariable("DATA_DIR"),
        Environment.GetEnvironmentVariable("CONFIG_DIR"));

    builder.Services.AddWebComposition(context);

    var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
    }

    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapRazorComponents<ImmichReverseGeo.Web.Components.App>()
        .AddInteractiveServerRenderMode();

    app.Run();
}
