using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace RustTerminal
{
    internal partial class FavoritesManageVm : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<Favorite> favorites = new();

        [ObservableProperty]
        private Favorite? selectedFavorite;

        private readonly string defaultDirectory;

        public ICommand NewFavoriteCommand { get; }
        public ICommand SaveFavoriteCommand { get; }
        public ICommand DeleteFavoriteCommand { get; }

        public FavoritesManageVm(string? currentDirectory = null)
        {
            defaultDirectory = currentDirectory ?? string.Empty;
            NewFavoriteCommand = new RelayCommand(NewFavorite);
            SaveFavoriteCommand = new RelayCommand(SaveFavorite, () => SelectedFavorite is not null);
            DeleteFavoriteCommand = new RelayCommand(DeleteFavorite, () => SelectedFavorite is not null && SelectedFavorite.Id > 0);
            Reload();
        }

        partial void OnSelectedFavoriteChanged(Favorite? value)
        {
            (SaveFavoriteCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DeleteFavoriteCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        public void Reload()
        {
            Favorites.Clear();
            foreach (var favorite in FavoritesStore.LoadAll())
            {
                Favorites.Add(favorite);
            }

            SelectedFavorite = Favorites.FirstOrDefault();
        }

        private void NewFavorite()
        {
            var item = new Favorite
            {
                Name = "New Favorite",
                DirectoryPath = defaultDirectory,
                CommandsText = "cargo clean\ncargo build"
            };

            Favorites.Add(item);
            SelectedFavorite = item;
        }

        private void SaveFavorite()
        {
            if (SelectedFavorite is null)
            {
                return;
            }

            FavoritesStore.Upsert(SelectedFavorite);
            Reload();
        }

        private void DeleteFavorite()
        {
            if (SelectedFavorite is null || SelectedFavorite.Id <= 0)
            {
                return;
            }

            FavoritesStore.Delete(SelectedFavorite.Id);
            Reload();
        }
    }
}
