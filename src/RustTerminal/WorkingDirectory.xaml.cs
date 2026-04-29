using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RustTerminal
{
    /// <summary>
    /// Interaction logic for WorkingDirectory.xaml
    /// </summary>
    public partial class WorkingDirectory : UserControl
    {
        public static readonly DependencyProperty PathProperty =
            DependencyProperty.Register(
                nameof(Path),
                typeof(string),
                typeof(WorkingDirectory),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty PathsProperty =
            DependencyProperty.Register(
                nameof(Paths),
                typeof(IEnumerable),
                typeof(WorkingDirectory),
                new PropertyMetadata(null));

        public static readonly DependencyProperty BrowseCommandProperty =
            DependencyProperty.Register(
                nameof(BrowseCommand),
                typeof(ICommand),
                typeof(WorkingDirectory),
                new PropertyMetadata(null));

        public WorkingDirectory()
        {
            InitializeComponent();
        }

        public string? Path
        {
            get => (string?)GetValue(PathProperty);
            set => SetValue(PathProperty, value);
        }

        public IEnumerable? Paths
        {
            get => (IEnumerable?)GetValue(PathsProperty);
            set => SetValue(PathsProperty, value);
        }

        public ICommand? BrowseCommand
        {
            get => (ICommand?)GetValue(BrowseCommandProperty);
            set => SetValue(BrowseCommandProperty, value);
        }

        private void PathCombo_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return || e.Key == Key.Tab)
            {
                e.Handled = true;
                MoveFocusToTerminal();
            }
        }

        private void MoveFocusToTerminal()
        {
            Dispatcher.BeginInvoke(() =>
            {
                var window = Window.GetWindow(this) as MainWindow;
                if (window?.TerminalControl is null)
                {
                    return;
                }

                var terminalControl = window.TerminalControl;
                var webView = terminalControl.GetWebView2();
                
                if (webView is not null && webView.CoreWebView2 is not null)
                {
                    try
                    {
                        webView.Focus();
                        webView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new { type = "focus" }));
                    }
                    catch
                    {
                    }
                }
            });
        }
    }
}
