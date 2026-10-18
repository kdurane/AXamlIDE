using System;
using AXamlIDE.Documents;
using AXamlIDE.Lsp;
using AXamlIDE.Preview;
using AXamlIDE.Settings;
using AXamlIDE.Shared;
using AXamlIDE.Shell;
using AXamlIDE.Workspace;
using AXamlIDE.Xaml;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using TextMateSharp.Grammars;

namespace AXamlIDE;

/// <summary>
/// The composition root: the one place that decides which concrete class backs each service.
/// To add a service, register it here and take it as a constructor parameter where it's needed.
/// To swap one (a fake for tests, a different implementation), change only its registration.
///
/// Everything is a singleton: there is one main window and one of each service. The container
/// owns them and disposes the IDisposable ones when the app exits (see App.axaml.cs).
/// </summary>
public static class AppServices
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // ---- cross-cutting
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<IDialogService, DialogService>();

        // ---- settings: loaded once at startup; SettingsViewModel is also the live IEditorSettings
        services.AddSingleton<EditorSettingsStore>();
        services.AddSingleton(sp => sp.GetRequiredService<EditorSettingsStore>().Load());
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<EditorSettingsLoadResult>().Settings,
            sp.GetRequiredService<EditorSettingsStore>()));
        services.AddSingleton<IEditorSettings>(sp => sp.GetRequiredService<SettingsViewModel>());
        services.AddSingleton(_ => new RegistryOptions(ThemeName.DarkPlus));

        // ---- workspace, documents, languages
        services.AddSingleton<WorkspaceModel>();
        services.AddSingleton<DocumentManager>();
        services.AddSingleton(_ => new LspManager(LspServers.Defaults));
        services.AddSingleton<XamlLanguageService>();

        // ---- view models and the window
        services.AddSingleton(sp => new PreviewPaneViewModel(
            sp.GetRequiredService<XamlLanguageService>().GetPreviewTargetAsync));
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,   // a missing registration fails at startup, not on first use
            ValidateScopes = true
        });
    }
}
