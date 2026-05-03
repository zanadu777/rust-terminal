using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RustTerminal
{
    public class Favorite : ObservableObject
    {
        private long id;
        private string name = string.Empty;
        private string directoryPath = string.Empty;
        private string commandsText = string.Empty;
        private bool isRunning;

        public long Id
        {
            get => id;
            set => SetProperty(ref id, value);
        }

        public string Name
        {
            get => name;
            set => SetProperty(ref name, value);
        }

        public string DirectoryPath
        {
            get => directoryPath;
            set => SetProperty(ref directoryPath, value);
        }

        public string CommandsText
        {
            get => commandsText;
            set => SetProperty(ref commandsText, value);
        }

        public bool IsRunning
        {
            get => isRunning;
            set => SetProperty(ref isRunning, value);
        }

        public string WorkingDirectory
        {
            get => DirectoryPath;
            set => DirectoryPath = value;
        }

        public List<string> Commands
        {
            get => GetCommands().ToList();
            set => CommandsText = value is null ? string.Empty : string.Join(Environment.NewLine, value);
        }

        public IReadOnlyList<string> GetCommands()
        {
            return CommandsText
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
        }
    }
}
