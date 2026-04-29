using System.Windows.Controls;

namespace RustTerminal
{
    /// <summary>
    /// Interaction logic for RecentCommands.xaml
    /// </summary>
    public partial class RecentCommands : UserControl
    {
        public RecentCommands()
        {
            InitializeComponent();
            DataContext = new RecentCommandsVm();
        }

        public RecentCommandsVm ViewModel => (RecentCommandsVm)DataContext;
    }
}
