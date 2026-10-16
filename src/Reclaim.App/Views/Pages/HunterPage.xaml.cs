using System.Windows;
using System.Windows.Controls;
using Reclaim.App.Services;

namespace Reclaim.App.Views.Pages;

public partial class HunterPage : Page
{
    private readonly CatalogService _catalog;
    private readonly UiService _ui;

    public HunterPage(CatalogService catalog, UiService ui)
    {
        _catalog = catalog;
        _ui = ui;
        InitializeComponent();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var hunter = new HunterWindow(_catalog, _ui);
        if (HideMain.IsChecked == true && Application.Current.MainWindow is { } main) main.WindowState = WindowState.Minimized;
        hunter.Show();
        hunter.Activate();
    }
}
