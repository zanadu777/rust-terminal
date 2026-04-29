using System.Windows;

namespace RustTerminal
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private MainWindowVm? viewModel;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;

            if (DataContext is MainWindowVm vm)
            {
                viewModel = vm;
                vm.AttachTerminal(TerminalControl);
                vm.DirectoryChanged += ViewModel_DirectoryChanged;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (this.FindName("RecentCommandsControl") is RecentCommands recentCommandsControl)
            {
                recentCommandsControl.ViewModel?.AttachTerminal(TerminalControl);
                
                // Initialize with current directory
                if (DataContext is MainWindowVm vm)
                {
                    recentCommandsControl.ViewModel?.SetCurrentDirectory(vm.BaseDirectory);
                }
            }
        }

        private void ViewModel_DirectoryChanged(object? sender, string directory)
        {
            if (this.FindName("RecentCommandsControl") is RecentCommands recentCommands)
            {
                recentCommands.ViewModel?.SetCurrentDirectory(directory);
            }
        }
    }
}
