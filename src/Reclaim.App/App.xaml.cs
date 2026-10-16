using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Reclaim.App.Services;
using Reclaim.App.ViewModels;
using Reclaim.App.Views.Pages;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Uninstall;
using Wpf.Ui;

namespace Reclaim.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public static IServiceProvider Services => ((App)Current)._services!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Fatal", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Background task failed", args.Exception);
            args.SetObserved();
        };

        var services = new ServiceCollection();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<UiService>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<MainWindow>();

        services.AddSingleton<AppsViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<ForceViewModel>();
        services.AddSingleton<MonitorViewModel>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<StartupViewModel>();
        services.AddSingleton<ToolsViewModel>();
        services.AddSingleton<BackupsViewModel>();
        services.AddSingleton<SettingsViewModel>();

        services.AddSingleton<HomePage>();
        services.AddSingleton<AppsPage>();
        services.AddSingleton<HunterPage>();
        services.AddSingleton<ForcePage>();
        services.AddSingleton<MonitorPage>();
        services.AddSingleton<CleanupPage>();
        services.AddSingleton<StartupPage>();
        services.AddSingleton<ToolsPage>();
        services.AddSingleton<BackupsPage>();
        services.AddSingleton<SettingsPage>();
        _services = services.BuildServiceProvider();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        var catalog = _services.GetRequiredService<CatalogService>();
        await catalog.LoadAsync();
        HandleArguments(e.Args, window, catalog);
    }

    /// <summary>
    /// --uninstall-target "path"  (Explorer menu / drag-and-drop from outside)
    /// --resume-batch             (continues a batch uninstall after a restart)
    /// </summary>
    private void HandleArguments(string[] args, MainWindow window, CatalogService catalog)
    {
        var ui = _services!.GetRequiredService<UiService>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--uninstall-target" when i + 1 < args.Length:
                    window.OpenUninstallFor(args[++i]);
                    return;
                case "--page" when i + 1 < args.Length:
                    var page = Type.GetType($"Reclaim.App.Views.Pages.{args[++i]}Page");
                    Log.Info($"Start page {args[i]} -> {page?.FullName ?? "not found"}");
                    if (page is not null) ui.Navigate(page);
                    break;
                case "--resume-batch":
                    var pending = BatchUninstallQueue.LoadPending();
                    BatchUninstallQueue.ClearPending();
                    if (pending is null) return;
                    var apps = catalog.Models.Where(a => pending.AppIds.Contains(a.Id)).ToList();
                    if (apps.Count > 0)
                        ui.ShowSheet(new UninstallWizardViewModel(apps, WizardMode.Normal, catalog, ui) { Quiet = pending.Quiet });
                    return;
            }
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception: " + e.Exception);
        e.Handled = true;
        try
        {
            _services?.GetService<UiService>()?.Error("Something went wrong", e.Exception.Message);
        }
        catch
        {
            MessageBox.Show(e.Exception.Message, "Reclaim", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
