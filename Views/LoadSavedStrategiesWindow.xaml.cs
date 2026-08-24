using System.Windows;
using MoneyGenerator_v5.ViewModels;


namespace MoneyGenerator_v5.Views
{
    /// <summary>
    /// Логика взаимодействия для LoadSavedStrategiesWindow.xaml
    /// </summary>
    public partial class LoadSavedStrategiesWindow : Window
    {
        public LoadSavedStrategiesViewModel ViewModel { get; }

        public LoadSavedStrategiesWindow(LoadSavedStrategiesViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;
        }

       
    }
}
