using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using PowershellTerminal;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;

namespace RustTerminal
{
    public partial class RecentCommandsVm : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<CommandExecutionResult> recentCommands = new();

        [ObservableProperty]
        private bool filterByDirectory = true;

        public ICommand ExecuteRecentCommandCommand { get; }

        private PowerShellTerminalControl? terminal;
        private string currentDirectory = string.Empty;

        public RecentCommandsVm()
        {
            ExecuteRecentCommandCommand = new RelayCommand<string?>(ExecuteRecentCommand);
        }

        public void AttachTerminal(PowerShellTerminalControl terminalControl)
        {
            terminal = terminalControl;
            terminal.CommandCompleted += Terminal_CommandCompleted;
            
            // Load initial commands for current directory
            RefreshDisplayedCommands();
        }

        public void SetCurrentDirectory(string directory)
        {
            currentDirectory = directory;
            RefreshDisplayedCommands();
        }

        private void Terminal_CommandCompleted(object? sender, CommandExecutionCompletedEventArgs e)
        {
            AddCommand(e.Result);
        }

        public void AddCommand(CommandExecutionResult result)
        {
            if (string.IsNullOrWhiteSpace(result.Command))
            {
                return;
            }

            var trimmedCommand = result.Command.Trim();

            // Don't save navigation commands (cd is auto-generated when switching directories)
            if (trimmedCommand.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || 
                trimmedCommand.Equals("cd", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Only save if we have a valid working directory
            if (string.IsNullOrWhiteSpace(currentDirectory))
            {
                return;
            }

            SaveRecentCommand(trimmedCommand, currentDirectory, result.ResponseText);
            RefreshDisplayedCommands();
        }

        private void RefreshDisplayedCommands()
        {
            RecentCommands.Clear();

            if (FilterByDirectory && !string.IsNullOrWhiteSpace(currentDirectory))
            {
                LoadCommandsForDirectory(currentDirectory);
            }
            else
            {
                LoadAllCommands();
            }
        }

        private void LoadAllCommands()
        {
            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT CommandText, ResponseText FROM RecentCommands WHERE CommandText NOT LIKE 'cd%' ORDER BY ExecutedUtc DESC LIMIT 10;";

                using var reader = command.ExecuteReader();
                var seen = new HashSet<string>();
                while (reader.Read())
                {
                    var commandText = reader.GetString(0);
                    
                    // Skip if already added
                    if (!seen.Add(commandText))
                    {
                        continue;
                    }
                    
                    var responseText = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    RecentCommands.Add(new CommandExecutionResult(commandText, responseText, DateTimeOffset.Now, DateTimeOffset.Now, false));
                }
            }
            catch
            {
            }
        }

        private void LoadCommandsForDirectory(string directory)
        {
            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT CommandText, ResponseText FROM RecentCommands WHERE DirectoryPath = $directory AND CommandText NOT LIKE 'cd%' ORDER BY ExecutedUtc DESC LIMIT 10;";
                command.Parameters.AddWithValue("$directory", directory);

                using var reader = command.ExecuteReader();
                var seen = new HashSet<string>();
                while (reader.Read())
                {
                    var commandText = reader.GetString(0);
                    
                    // Skip if already added
                    if (!seen.Add(commandText))
                    {
                        continue;
                    }
                    
                    var responseText = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    RecentCommands.Add(new CommandExecutionResult(commandText, responseText, DateTimeOffset.Now, DateTimeOffset.Now, false));
                }
            }
            catch
            {
            }
        }

        private void SaveRecentCommand(string commandText, string directory, string responseText)
        {
            try
            {
                using var connection = OpenSettingsConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO RecentCommands(CommandText, DirectoryPath, ExecutedUtc, ResponseText) VALUES ($command, $directory, $executedUtc, $responseText) ON CONFLICT(CommandText, DirectoryPath) DO UPDATE SET ExecutedUtc = excluded.ExecutedUtc, ResponseText = excluded.ResponseText;";
                command.Parameters.AddWithValue("$command", commandText);
                command.Parameters.AddWithValue("$directory", directory);
                command.Parameters.AddWithValue("$executedUtc", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$responseText", responseText);
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
                                  CREATE TABLE IF NOT EXISTS RecentCommands (
                                      Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                      CommandText TEXT NOT NULL,
                                      DirectoryPath TEXT NOT NULL,
                                      ExecutedUtc TEXT NOT NULL,
                                      ResponseText TEXT,
                                      UNIQUE(CommandText, DirectoryPath)
                                  );
                                  """;
            command.ExecuteNonQuery();

            // Clean up navigation commands (cd is auto-generated) and orphaned entries
            using var cleanupCommand = connection.CreateCommand();
            cleanupCommand.CommandText = "DELETE FROM RecentCommands WHERE CommandText LIKE 'cd%' OR DirectoryPath IS NULL OR DirectoryPath = '';";

            cleanupCommand.ExecuteNonQuery();

            return connection;
        }

        private static string GetSettingsDbPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, "RustTerminal");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "settings.db");
        }

        private void ExecuteRecentCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command) || terminal is null)
            {
                return;
            }

            terminal.ExecuteCommand(command);
        }
    }
}
