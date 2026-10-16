using System.Windows.Controls;
using Reclaim.App.ViewModels;

namespace Reclaim.App.Views.Pages;

public partial class StartupPage : Page
{
    public StartupPage(StartupViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }
}
