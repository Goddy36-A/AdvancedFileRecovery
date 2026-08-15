using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FileRecovery.Core.Models;

namespace FileRecovery.App.ViewModels;

public partial class RecoverableFileViewModel : ObservableObject
{
    public RecoverableFile Model { get; }

    public RecoverableFileViewModel(RecoverableFile model) => Model = model;

    [ObservableProperty] private bool isSelected;

    [ObservableProperty] private BitmapImage? thumbnail;

    public string Name => Model.Name;
    public string OriginalPath => Model.OriginalPath ?? "(original path unknown)";
    public long SizeBytes => Model.SizeBytes;
    public FileCategory Category => Model.Category;
    public string Extension => Model.Extension;
    public DateTime? ModifiedUtc => Model.ModifiedUtc;
    public Recoverability Recoverability => Model.Recoverability;
    public bool FromCarving => Model.FromCarving;

    public bool IsImage => Extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif";
}
