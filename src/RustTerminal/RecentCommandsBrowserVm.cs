using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace RustTerminal
{
    public class RecentCommandData
    {
        public string CommandText { get; set; }
        public string DirectoryPath { get; set; }
        public string ExecutedUtc { get; set; }
    }

    public partial class RecentCommandsBrowserVm : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<RecentCommandData> recentCommandsData = new();

        [ObservableProperty]
        private RecentCommandData? selectedCommand;

        [ObservableProperty]
        private string currentDirectory = string.Empty;

        [ObservableProperty]
        private bool filterByDirectory = false;

        public ICommand DeleteSelectedCommand { get; }
        public ICommand DeleteByDirectoryCommand { get; }
        public ICommand ClearAllCommand { get; }

        public RecentCommandsBrowserVm()
        {
            DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => SelectedCommand is not null);
            DeleteByDirectoryCommand = new RelayCommand(DeleteByDirectory, () => SelectedCommand is not null);
            ClearAllCommand = new RelayCommand(ClearAll);
            LoadData();
        }

        partial void OnSelectedCommandChanged(RecentCommandData? value)
        {
            (DeleteSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DeleteByDirectoryCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        public void SetCurrentDirectory(string directory)
        {
            CurrentDirectory = directory;
        }

        private void LoadData()
        {
            RecentCommandsData.Clear();

            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT CommandText, DirectoryPath, ExecutedUtc FROM RecentCommands WHERE CommandText NOT LIKE 'cd%' ORDER BY ExecutedUtc DESC;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var commandText = reader.GetString(0);
                    var directoryPath = reader.GetString(1);
                    var executedUtc = reader.GetString(2);

                    RecentCommandsData.Add(new RecentCommandData
                    {
                        CommandText = commandText,
                        DirectoryPath = directoryPath,
                        ExecutedUtc = executedUtc
                    });
                }
            }
            catch
            {
            }
        }

        private void DeleteSelected()
        {
            if (SelectedCommand is null)
            {
                return;
            }

            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM RecentCommands WHERE CommandText = $command AND DirectoryPath = $directory;";
                command.Parameters.AddWithValue("$command", SelectedCommand.CommandText);
                command.Parameters.AddWithValue("$directory", SelectedCommand.DirectoryPath);
                command.ExecuteNonQuery();
            }
            catch
            {
            }

            RecentCommandsData.Remove(SelectedCommand);
            SelectedCommand = null;
        }

        private void DeleteByDirectory()
        {
            if (SelectedCommand is null)
            {
                return;
            }

            var result = MessageBox.Show(
                $"Delete all commands for directory:\n{SelectedCommand.DirectoryPath}?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM RecentCommands WHERE DirectoryPath = $directory;";
                command.Parameters.AddWithValue("$directory", SelectedCommand.DirectoryPath);
                command.ExecuteNonQuery();
            }
            catch
            {
            }

            var toRemove = RecentCommandsData.Where(x => x.DirectoryPath == SelectedCommand.DirectoryPath).ToList();
            foreach (var item in toRemove)
            {
                RecentCommandsData.Remove(item);
            }

            SelectedCommand = null;
        }

        private void ClearAll()
        {
            var result = MessageBox.Show(
                "Delete ALL recent commands?",
                "Confirm Clear All",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM RecentCommands;";
                command.ExecuteNonQuery();
            }
            catch
            {
            }

            RecentCommandsData.Clear();
            SelectedCommand = null;
        }

        private static SqliteConnection OpenSettingsConnection()
        {
            var dbPath = GetSettingsDbPath();
            var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
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
