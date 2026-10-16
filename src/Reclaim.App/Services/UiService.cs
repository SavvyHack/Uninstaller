using System.Windows;
using Reclaim.Core.Infrastructure;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace Reclaim.App.Services;

/// <summary>Small façade over WPF-UI's dialog, snackbar and navigation services plus the uninstall sheet.</summary>
public sealed class UiService(ISnackbarService snackbar, IContentDialogService dialogs)
{
    private INavigationView? _navigation;
    private MainWindow? _window;

    internal void Attach(MainWindow window, INavigationView navigation)
    {
        _window = window;
        _navigation = navigation;
    }

    public void Navigate(Type pageType) => _navigation?.Navigate(pageType);

    public void Toast(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary, SymbolRegular icon = SymbolRegular.Info24, int seconds = 5) =>
        Application.Current.Dispatcher.Invoke(() =>
            snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(seconds)));

    public void Success(string title, string message) => Toast(title, message, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
    public void Error(string title, string message) => Toast(title, message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24, 8);

    public async Task<bool> ConfirmAsync(string title, string message, string primary = "Continue", string close = "Cancel")
    {
        var result = await dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = close,
        });
        return result == ContentDialogResult.Primary;
    }

    public async Task<string?> PromptAsync(string title, string label, string initial = "", string primary = "OK")
    {
        var box = new Wpf.Ui.Controls.TextBox { Text = initial, PlaceholderText = label, MinWidth = 360 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialogs.ShowAsync(dialog, CancellationToken.None);
        return result == ContentDialogResult.Primary ? box.Text : null;
    }

    public void ShowSheet(object viewModel) => _window?.ShowSheet(viewModel);
    public void HideSheet() => _window?.HideSheet();

    public Window? Window => _window;

    public static void ApplyTheme(ThemePreference pref, Window? window)
    {
        var theme = pref switch
        {
            ThemePreference.Light => ApplicationTheme.Light,
            ThemePreference.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
        if (window is not null)
        {
            if (pref == ThemePreference.System) SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
            else SystemThemeWatcher.UnWatch(window);
        }
    }
}
