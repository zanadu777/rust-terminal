using System.Windows.Controls;

namespace RustTerminal
{
    /// <summary>
    /// Interaction logic for RecentCommandsBrowser.xaml
    /// </summary>
    public partial class RecentCommandsBrowser : UserControl
    {
        public RecentCommandsBrowser()
        {
            InitializeComponent();
        }

        public RecentCommandsBrowserVm? ViewModel => DataContext as RecentCommandsBrowserVm;
    }
}
