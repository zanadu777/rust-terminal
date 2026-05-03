using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace RustTerminal
{
    internal static class FavoritesStore
    {
        public static IReadOnlyList<Favorite> LoadAll()
        {
            var list = new List<Favorite>();
            try
            {
                using var connection = OpenSettingsConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Id, Name, DirectoryPath, CommandsText FROM Favorites ORDER BY Name;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new Favorite
                    {
                        Id = reader.GetInt64(0),
                        Name = reader.GetString(1),
                        DirectoryPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        CommandsText = reader.GetString(3)
                    });
                }
            }
            catch
            {
            }

            return list;
        }

        public static void Upsert(Favorite favorite)
        {
            using var connection = OpenSettingsConnection();
            using var cmd = connection.CreateCommand();
            if (favorite.Id <= 0)
            {
                cmd.CommandText = "INSERT INTO Favorites(Name, DirectoryPath, CommandsText) VALUES($name, $directoryPath, $commandsText);";
            }
            else
            {
                cmd.CommandText = "UPDATE Favorites SET Name=$name, DirectoryPath=$directoryPath, CommandsText=$commandsText WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$id", favorite.Id);
            }

            cmd.Parameters.AddWithValue("$name", favorite.Name);
            cmd.Parameters.AddWithValue("$directoryPath", favorite.DirectoryPath ?? string.Empty);
            cmd.Parameters.AddWithValue("$commandsText", favorite.CommandsText);
            cmd.ExecuteNonQuery();
        }

        public static void Delete(long id)
        {
            using var connection = OpenSettingsConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Favorites WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        private static SqliteConnection OpenSettingsConnection()
        {
            var dbPath = GetSettingsDbPath();
            var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                                  CREATE TABLE IF NOT EXISTS Favorites (
                                      Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                      Name TEXT NOT NULL UNIQUE,
                                      DirectoryPath TEXT,
                                      CommandsText TEXT NOT NULL
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
