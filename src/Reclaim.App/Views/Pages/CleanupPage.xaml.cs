using System.Windows;
using System.Windows.Controls;
using Reclaim.App.ViewModels;

namespace Reclaim.App.Views.Pages;

public partial class CleanupPage : Page
{
    private readonly CleanupViewModel _vm;

    public CleanupPage(CleanupViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private void ShredDrop_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _vm.AddShredPaths(files);
        e.Handled = true; // don't let the main window treat it as "uninstall this"
    }
}
