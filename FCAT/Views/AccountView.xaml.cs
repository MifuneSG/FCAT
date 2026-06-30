using System.Windows;
using System.Windows.Controls;
using FCAT.ViewModels;

namespace FCAT.Views;

public partial class AccountView : UserControl
{
    public AccountView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountViewModel vm) vm.StartAuto();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountViewModel vm) vm.StopAuto();
    }
}
