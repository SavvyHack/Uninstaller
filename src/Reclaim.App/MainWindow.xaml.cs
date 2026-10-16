using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Reclaim.App.Services;
using Reclaim.App.ViewModels;
using Reclaim.App.Views.Pages;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Inventory;
using Reclaim.Core.Uninstall;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Reclaim.App;

public partial class MainWindow : FluentWindow
{
    private readonly UiService _ui;
    private readonly CatalogService _catalog;

    public MainWindow(IServiceProvider services, UiService ui, ISnackbarService snackbar, IContentDialogService dialogs, CatalogService catalog)
    {
        _ui = ui;
        _catalog = catalog;
        InitializeComponent();
        UiService.ApplyTheme(SettingsStore.Current.Theme, this);

        RootNavigation.SetServiceProvider(services);
        snackbar.SetSnackbarPresenter(SnackbarPresenter);
        dialogs.SetDialogHost(RootContentDialog);
        ui.Attach(this, RootNavigation);

        Loaded += (_, _) => RootNavigation.Navigate(typeof(HomePage));
        _catalog.ExternalUninstall += OnExternalUninstall;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && SheetLayer.Visibility == Visibility.Visible && Sheet.DataContext is UninstallWizardViewModel { IsBusy: false } vm)
            {
                vm.CloseCommand.Execute(null);
                e.Handled = true;
            }
        };
        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropHint.Visibility = Visibility.Visible; };
        DragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    // ------------------------------------------------------------------ sheet

    public void ShowSheet(object viewModel)
    {
        Sheet.DataContext = viewModel;
        SheetLayer.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SheetTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(SheetPanel.Width, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    public void HideSheet()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var slide = new DoubleAnimation(0, SheetPanel.Width, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            SheetLayer.Visibility = Visibility.Collapsed;
            Sheet.DataContext = null;
        };
        SheetTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)));
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Sheet.DataContext is UninstallWizardViewModel { IsBusy: false, Step: WizardStep.Prepare or WizardStep.Done } vm)
            vm.CloseCommand.Execute(null);
    }

    // ------------------------------------------------------------------ drag & drop / outside uninstalls

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        OpenUninstallFor(files[0]);
    }

    /// <summary>Uninstall whatever a dropped file, shortcut or folder belongs to.</summary>
    public void OpenUninstallFor(string path)
    {
        var target = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.GetTarget(path) ?? path : path;
        var owner = File.Exists(target) ? AppCatalog.FindOwner(_catalog.Models, target) : null;
        var app = owner ?? ForcedUninstall.Create(target, _catalog.Models);
        var mode = owner is null ? WizardMode.Forced : WizardMode.Normal;
        _ui.ShowSheet(new UninstallWizardViewModel([app], mode, _catalog, _ui));
    }

    private void OnExternalUninstall(Core.Models.InstalledApp app)
    {
        _catalog.Remove([app]);
        var bar = new Snackbar(SnackbarPresenter)
        {
            Title = $"{app.Name} was uninstalled",
            Content = new System.Windows.Controls.StackPanel
            {
                Children =
                {
                    new System.Windows.Controls.TextBlock { Text = "It was removed outside Reclaim. Scan for what it left behind?", Margin = new Thickness(0, 0, 0, 8) },
                    MakeScanButton(app),
                },
            },
            Appearance = ControlAppearance.Info,
            Icon = new SymbolIcon(SymbolRegular.Broom24),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _ = bar.ShowAsync();
    }

    private Wpf.Ui.Controls.Button MakeScanButton(Core.Models.InstalledApp app)
    {
        var b = new Wpf.Ui.Controls.Button { Content = "Scan for leftovers", Appearance = ControlAppearance.Primary };
        b.Click += (_, _) => _ui.ShowSheet(new UninstallWizardViewModel([app], WizardMode.ScanOnly, _catalog, _ui));
        return b;
    }
}
