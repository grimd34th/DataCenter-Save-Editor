using System.Windows;
using DataCenterSaveEditor.App.ViewModels;

namespace DataCenterSaveEditor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Refresh();
    }

    private void AdvancedTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.SelectedAdvancedNode = e.NewValue as NodeViewModel;
}
