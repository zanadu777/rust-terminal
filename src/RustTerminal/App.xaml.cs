using System.Configuration;
using System.Data;
using System.Windows;

namespace RustTerminal
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly string ErrorLogPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "RustTerminal", "error.log");

        public App()
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                LogToFile("=== Application Startup ===");
                base.OnStartup(e);
                LogToFile("Startup completed successfully");
            }
            catch (Exception ex)
            {
                LogToFile($"Startup Error: {ex.GetType().Name}");
                LogToFile($"Message: {ex.Message}");
                LogToFile($"StackTrace: {ex.StackTrace}");
                MessageBox.Show($"Startup Error:\n\n{ex.Message}\n\nCheck log at:\n{ErrorLogPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Shutdown();
            }
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogToFile($"Dispatcher Error: {e.Exception.Message}");
            LogToFile($"StackTrace: {e.Exception.StackTrace}");
            MessageBox.Show($"Application Error:\n\n{e.Exception.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = false;
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogToFile($"Domain Error: {ex.Message}");
                LogToFile($"StackTrace: {ex.StackTrace}");
                MessageBox.Show($"Application Error:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void LogToFile(string message)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(ErrorLogPath);
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                System.IO.File.AppendAllText(ErrorLogPath, $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}\r\n");
            }
            catch { }
        }
    }

}
//# sourceMappingURL=App.xaml.cs.map
