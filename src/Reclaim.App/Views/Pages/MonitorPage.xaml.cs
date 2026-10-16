using System.Windows.Controls;
using Reclaim.App.ViewModels;

namespace Reclaim.App.Views.Pages;

public partial class MonitorPage : Page
{
    public MonitorPage(MonitorViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }
}
