using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using PowershellTerminal;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace RustTerminal
{
    internal partial class MainWindowVm : ObservableObject
    {
        private const string BaseDirectoryKey = "BaseDirectory";

        [ObservableProperty]
        private string baseDirectory = string.Empty;

        [ObservableProperty]
        private bool hasValidBaseDirectory;

        [ObservableProperty]
        private StoredWorkingDirectory? selectedStoredDirectory;

        private PowerShellTerminalControl? terminal;
        private Window? executionLogWindow;
        private Window? configWindow;

        public ObservableCollection<string> WorkingDirectoryHistory { get; } = new();
        public ObservableCollection<StoredWorkingDirectory> StoredWorkingDirectories { get; } = new();
        public ObservableCollection<CommandExecutionResult> CommandExecutions { get; } = new();

        public ICommand CargoBuildCommand { get; }
        public ICommand CargoCleanCommand { get; }
        public ICommand CargoRebuildCommand { get; }
        public ICommand OpenExecutionLogCommand { get; }
        public ICommand BrowseWorkingDirectoryCommand { get; }
        public ICommand OpenConfigCommand { get; }
        public ICommand RemoveSelectedDirectoryCommand { get; }
        public ICommand UseSelectedDirectoryCommand { get; }

        public MainWindowVm()
        {
            CargoCleanCommand = new RelayCommand(CargoClean, () => HasValidBaseDirectory);
            CargoBuildCommand = new RelayCommand(CargoBuild, () => HasValidBaseDirectory);
            CargoRebuildCommand = new RelayCommand(CargoRebuild, () => HasValidBaseDirectory);
            OpenExecutionLogCommand = new RelayCommand(OpenExecutionLog);
            BrowseWorkingDirectoryCommand = new RelayCommand(BrowseWorkingDirectory);
            OpenConfigCommand = new RelayCommand(OpenConfig);
            RemoveSelectedDirectoryCommand = new RelayCommand(RemoveSelectedDirectory, () => SelectedStoredDirectory is not null);
            UseSelectedDirectoryCommand = new RelayCommand(UseSelectedDirectory, () => SelectedStoredDirectory is not null);

            LoadDirectoryHistory();
            var storedBaseDirectory = LoadSetting(BaseDirectoryKey);
            if (!string.IsNullOrWhiteSpace(storedBaseDirectory))
            {
                BaseDirectory = storedBaseDirectory;
            }
            else if (WorkingDirectoryHistory.Count > 0)
            {
                BaseDirectory = WorkingDirectoryHistory[0];
            }

            ValidateBaseDirectory();
        }

        partial void OnSelectedStoredDirectoryChanged(StoredWorkingDirectory? value)
        {
            (RemoveSelectedDirectoryCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (UseSelectedDirectoryCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        partial void OnBaseDirectoryChanged(string value)
        {
            ValidateBaseDirectory();

            if (Directory.Exists(value))
            {
                SaveSetting(BaseDirectoryKey, value);
                UpsertWorkingDirectory(value, DateTimeOffset.Now);
                ReloadDirectoryHistory(value);
            }
        }

        private void ValidateBaseDirectory()
        {
            HasValidBaseDirectory = Directory.Exists(BaseDirectory);
            (CargoCleanCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (CargoBuildCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (CargoRebuildCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        private void BrowseWorkingDirectory()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select working directory",
                InitialDirectory = Directory.Exists(BaseDirectory)
                    ? BaseDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (dialog.ShowDialog() == true && Directory.Exists(dialog.FolderName))
            {
                BaseDirectory = dialog.FolderName;
            }
        }

        private void OpenConfig()
        {
            if (configWindow is not null)
            {
                configWindow.Show();
                configWindow.Activate();
                return;
            }

            var configView = new ConfigDirectoriesView
            {
                DataContext = this
            };

            configWindow = new Window
            {
                Title = "Configuration",
                Width = 760,
                Height = 460,
                Owner = Application.Current.MainWindow,
                Content = configView
            };

            configWindow.Closing += (s, e) =>
            {
                e.Cancel = true;
                configWindow?.Hide();
            };

            configWindow.Show();
            configWindow.Activate();
        }

        private void RemoveSelectedDirectory()
        {
            if (SelectedStoredDirectory is null)
            {
                return;
            }

            DeleteWorkingDirectory(SelectedStoredDirectory.DirectoryPath);
            var removedPath = SelectedStoredDirectory.DirectoryPath;

            ReloadDirectoryHistory(BaseDirectory);

            if (string.Equals(BaseDirectory, removedPath, StringComparison.OrdinalIgnoreCase))
            {
                if (WorkingDirectoryHistory.Count > 0)
                {
                    BaseDirectory = WorkingDirectoryHistory[0];
                }
                else
                {
                    BaseDirectory = string.Empty;
                }
            }
        }

        private void UseSelectedDirectory()
        {
            if (SelectedStoredDirectory is null)
            {
                return;
            }

            if (!Directory.Exists(SelectedStoredDirectory.DirectoryPath))
            {
                DeleteWorkingDirectory(SelectedStoredDirectory.DirectoryPath);
                ReloadDirectoryHistory(BaseDirectory);
                return;
            }

            BaseDirectory = SelectedStoredDirectory.DirectoryPath;
        }

        private void CargoRebuild()
        {
            terminal?.ExecuteCommand("clear", "cargo clean", "cargo build");
        }

        private void CargoClean()
        {
            terminal?.ExecuteCommand("cargo clean");
        }

        private void CargoBuild()
        {
            terminal?.ExecuteCommand("cargo build");
        }

        private void OpenExecutionLog()
        {
            if (executionLogWindow is not null)
            {
                executionLogWindow.Show();
                executionLogWindow.Activate();
                return;
            }

            var logView = new ExecutionLog
            {
                Source = CommandExecutions,
                ShowDates = false,
                IsStartVisible = false,
                Use24HourClock = false
            };

            executionLogWindow = new Window
            {
                Title = "Execution Log",
                Width = 520,
                Height = 600,
                Owner = Application.Current.MainWindow,
                Content = logView
            };

            executionLogWindow.Closing += (s, e) =>
            {
                e.Cancel = true;
                executionLogWindow?.Hide();
            };

            executionLogWindow.Show();
            executionLogWindow.Activate();
        }

        public void AttachTerminal(PowerShellTerminalControl terminalControl)
        {
            if (terminal is not null)
            {
                terminal.CommandCompleted -= Terminal_CommandCompleted;
            }

            terminal = terminalControl;
            terminal.CommandCompleted += Terminal_CommandCompleted;
        }

        private void Terminal_CommandCompleted(object? sender, CommandExecutionCompletedEventArgs e)
        {
            if (Application.Current.Dispatcher.CheckAccess())
            {
                CommandExecutions.Add(e.Result);
                return;
            }

            Application.Current.Dispatcher.Invoke(() => CommandExecutions.Add(e.Result));
        }

        private static string? LoadSetting(string key)
        {
            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key LIMIT 1;";
                command.Parameters.AddWithValue("$key", key);
                return command.ExecuteScalar() as string;
            }
            catch
            {
                return null;
            }
        }

        private static void SaveSetting(string key, string value)
        {
            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO AppSettings(Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                command.ExecuteNonQuery();
            }
            catch
            {
            }
        }

        private void LoadDirectoryHistory()
        {
            ReloadDirectoryHistory(null);
        }

        private void ReloadDirectoryHistory(string? selectedFirst)
        {
            WorkingDirectoryHistory.Clear();
            StoredWorkingDirectories.Clear();

            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT DirectoryPath, LastUsedUtc FROM WorkingDirectories ORDER BY LastUsedUtc DESC;";
                using var reader = command.ExecuteReader();

                var staged = new List<StoredWorkingDirectory>();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    var lastUsedText = reader.GetString(1);

                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    {
                        var parsed = DateTimeOffset.TryParse(lastUsedText, out var dto)
                            ? dto
                            : DateTimeOffset.MinValue;

                        staged.Add(new StoredWorkingDirectory
                        {
                            DirectoryPath = path,
                            LastUsedUtc = parsed
                        });
                    }
                }

                if (!string.IsNullOrWhiteSpace(selectedFirst) && Directory.Exists(selectedFirst))
                {
                    var selectedExisting = staged.FirstOrDefault(x => string.Equals(x.DirectoryPath, selectedFirst, StringComparison.OrdinalIgnoreCase));
                    if (selectedExisting is null)
                    {
                        selectedExisting = new StoredWorkingDirectory
                        {
                            DirectoryPath = selectedFirst,
                            LastUsedUtc = DateTimeOffset.UtcNow
                        };
                    }

                    StoredWorkingDirectories.Add(selectedExisting);
                }

                foreach (var item in staged)
                {
                    if (!StoredWorkingDirectories.Any(x => string.Equals(x.DirectoryPath, item.DirectoryPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        StoredWorkingDirectories.Add(item);
                    }
                }

                foreach (var item in StoredWorkingDirectories)
                {
                    WorkingDirectoryHistory.Add(item.DirectoryPath);
                }
            }
            catch
            {
            }
        }

        private static void UpsertWorkingDirectory(string directoryPath, DateTimeOffset lastUsed)
        {
            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO WorkingDirectories(DirectoryPath, LastUsedUtc) VALUES ($directoryPath, $lastUsedUtc) ON CONFLICT(DirectoryPath) DO UPDATE SET LastUsedUtc = excluded.LastUsedUtc;";
                command.Parameters.AddWithValue("$directoryPath", directoryPath);
                command.Parameters.AddWithValue("$lastUsedUtc", lastUsed.UtcDateTime.ToString("O"));
                command.ExecuteNonQuery();
            }
            catch
            {
            }
        }

        private static void DeleteWorkingDirectory(string directoryPath)
        {
            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM WorkingDirectories WHERE DirectoryPath = $directoryPath;";
                command.Parameters.AddWithValue("$directoryPath", directoryPath);
                command.ExecuteNonQuery();
            }
            catch
            {
            }
        }

        private static SqliteConnection OpenSettingsConnection()
        {
            var dbPath = GetSettingsDbPath();
            var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                                  CREATE TABLE IF NOT EXISTS AppSettings (
                                      Key TEXT PRIMARY KEY,
                                      Value TEXT NOT NULL
                                  );

                                  CREATE TABLE IF NOT EXISTS WorkingDirectories (
                                      DirectoryPath TEXT PRIMARY KEY,
                                      LastUsedUtc TEXT NOT NULL
                                  );
                                  """;
            command.ExecuteNonQuery();

            return connection;
        }

        private static string GetSettingsDbPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, "RustTerminal");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "settings.db");
        }
    }
}
