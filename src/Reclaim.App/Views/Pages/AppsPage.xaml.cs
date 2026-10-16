using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Reclaim.App.ViewModels;

namespace Reclaim.App.Views.Pages;

public partial class AppsPage : Page
{
    private readonly AppsViewModel _vm;

    public AppsPage(AppsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        InputBindings.Add(new KeyBinding(new CommandAdapter(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
    }

    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppItemViewModel item) item.EnsureIcon();
    }

    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is AppItemViewModel item) _vm.UninstallCommand.Execute(item);
    }

    private sealed class CommandAdapter(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
