using System.Windows;
using System.Windows.Controls;
using Reclaim.App.ViewModels;

namespace Reclaim.App.Views.Pages;

public partial class ToolsPage : Page
{
    private readonly ToolsViewModel _vm;

    public ToolsPage(ToolsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e) => _vm.OnShown();
}
