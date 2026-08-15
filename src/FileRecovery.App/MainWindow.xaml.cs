using System.Windows;
using FileRecovery.App.ViewModels;

namespace FileRecovery.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void ListViewItem_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Allows clicking anywhere on a result row to toggle its checkbox except when
        // the click originated on the checkbox itself (which handles its own toggle).
    }
}
