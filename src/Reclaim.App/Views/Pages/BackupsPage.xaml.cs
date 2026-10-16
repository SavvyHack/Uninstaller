using System.Windows;
using System.Windows.Controls;
using Reclaim.App.ViewModels;

namespace Reclaim.App.Views.Pages;

public partial class BackupsPage : Page
{
    private readonly BackupsViewModel _vm;

    public BackupsPage(BackupsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e) => _ = _vm.LoadAsync();
}
