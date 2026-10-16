using System.Windows.Controls;
using Reclaim.App.ViewModels;

namespace Reclaim.App.Views.Pages;

public partial class HomePage : Page
{
    public HomePage(HomeViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }
}
