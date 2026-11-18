using System;
using AXamlIDE.Messages;
using AXamlIDE.Services.Running;
using AXamlIDE.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace AXamlIDE.SampleData;

/// <summary>
/// Sample view models for the views' Design.DataContext, so the previewer (and Claude's render_xaml) shows each view
/// filled in rather than empty. They're the real view models from the app's own services (AppServices.Build(),
/// nothing started), with made-up content put in the way the app would put it there.
///
/// Design-time only: Avalonia's XAML compiler drops Design.* properties from the built app, so nothing here runs
/// when the IDE does.
/// </summary>
public static partial class DesignData
{
    private static readonly Lazy<IServiceProvider> LazyServices = new(AppServices.Build);

    private static IServiceProvider Services => LazyServices.Value;

    private static T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    private static void Send<TMessage>(TMessage message) where TMessage : class => Get<IMessenger>().Send(message);

    /// <summary>A file path in the made-up workspace.</summary>
    private static string SamplePath(string relative) => System.IO.Path.Combine(SampleRoot, relative);

    private static readonly string SampleRoot = OperatingSystem.IsWindows() ? @"C:\Projects\Contoso" : "/home/user/Projects/Contoso";

    // ------------------------------------------------------------ Problems

    public static ProblemsViewModel Problems => LazyProblems.Value;

    private static readonly Lazy<ProblemsViewModel> LazyProblems = new(() =>
    {
        var problems = Get<ProblemsViewModel>();
        var project = SamplePath("Contoso.App/Contoso.App.csproj");
        Send(new BuildProblemsMessage(
        [
            new BuildDiagnostic(SamplePath("Contoso.App/ViewModels/MainViewModel.cs"), 42, 17, 1, "CS0103",
                "The name 'customerName' does not exist in the current context", project),
            new BuildDiagnostic(SamplePath("Contoso.App/Services/OrderService.cs"), 88, 9, 1, "CS1002", "; expected", project),
            new BuildDiagnostic(SamplePath("Contoso.App/Views/MainWindow.axaml"), 12, 5, 2, "AVLN2000",
                "Unable to resolve property or method of name 'Titel' on type 'MainViewModel'", project),
            new BuildDiagnostic(SamplePath("Contoso.App/Models/Order.cs"), 7, 23, 2, "CS8618",
                "Non-nullable property 'Customer' must contain a non-null value when exiting constructor", project),
            new BuildDiagnostic(SamplePath("Contoso.App/App.axaml.cs"), 19, 13, 3, "IDE0059",
                "Unnecessary assignment of a value to 'window'", project)
        ]));
        return problems;
    });

    // ------------------------------------------------------------ Run: Output and Console

    public static RunViewModel Run => LazyRun.Value;

    /// <summary>Run, paused in the debugger (the debug toolbar's Continue and Step buttons are on).</summary>
    public static RunViewModel PausedRun
    {
        get
        {
            Pause();
            return Run;
        }
    }

    private static readonly Lazy<RunViewModel> LazyRun = new(() =>
    {
        var run = Get<RunViewModel>();
        foreach (var line in (string[])
                 [
                     "Building Contoso.sln (Debug)",
                     "  Determining projects to restore...",
                     "  All projects are up-to-date for restore.",
                     "  Contoso.Core -> /home/user/Projects/Contoso/Contoso.Core/bin/Debug/net10.0/Contoso.Core.dll",
                     "/home/user/Projects/Contoso/Contoso.App/Models/Order.cs(7,23): warning CS8618: Non-nullable property 'Customer' must contain a non-null value when exiting constructor. [Contoso.App.csproj]",
                     "  Contoso.App -> /home/user/Projects/Contoso/Contoso.App/bin/Debug/net10.0/Contoso.App.dll",
                     "Build succeeded."
                 ])
            run.BuildOutput.Add(line);
        foreach (var line in (string[])
                 [
                     "Running Contoso.App (Debug)",
                     "info: Contoso.App.Services.OrderService[0] Loaded 24 orders",
                     "warn: Contoso.App.Services.OrderService[0] Order 1043 has no customer",
                     "Application exited."
                 ])
            run.ConsoleOutput.Add(line);
        return run;
    });
}
