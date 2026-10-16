using System.Windows.Controls;
using Reclaim.App.ViewModels;

namespace Reclaim.App.Views.Pages;

public partial class ForcePage : Page
{
    public ForcePage(ForceViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }
}
