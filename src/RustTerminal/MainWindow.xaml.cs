using System.Windows;

namespace RustTerminal
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            if (DataContext is MainWindowVm vm)
            {
                vm.AttachTerminal(TerminalControl);
            }
        }
    }
}
