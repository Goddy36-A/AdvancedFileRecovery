using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileRecovery.Core.Disk;
using FileRecovery.Core.Models;
using FileRecovery.Core.Recovery;

namespace FileRecovery.App.ViewModels;

public enum WizardStep { SelectVolume, ConfigureScan, Scanning, Results, ChooseDestination, Recovering, Summary }

public partial class MainViewModel : ObservableObject
{
    private readonly RecoveryEngine _engine = new();
    private readonly PreviewService _previewService = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _recoveryCts;
    private readonly List<RecoverableFileViewModel> _allResults = new();

    public MainViewModel()
    {
        RefreshVolumes();
    }

    // ---------- Step tracking ----------
    [ObservableProperty] private WizardStep currentStep = WizardStep.SelectVolume;

    // ---------- Step 1: volume selection ----------
    public ObservableCollection<VolumeInfo> Volumes { get; } = new();
    [ObservableProperty] private VolumeInfo? selectedVolume;

    [RelayCommand]
    private void RefreshVolumes()
    {
        Volumes.Clear();
        foreach (var v in VolumeEnumerator.EnumerateVolumes())
            Volumes.Add(v);
    }

    [RelayCommand(CanExecute = nameof(CanGoToScanConfig))]
    private void GoToScanConfig() => CurrentStep = WizardStep.ConfigureScan;
    private bool CanGoToScanConfig() => SelectedVolume != null;

    [RelayCommand]
    private void SelectVolumeAndContinue(VolumeInfo volume)
    {
        SelectedVolume = volume;
        CurrentStep = WizardStep.ConfigureScan;
    }

    partial void OnSelectedVolumeChanged(VolumeInfo? value) => GoToScanConfigCommand.NotifyCanExecuteChanged();

    // ---------- Step 2: scan configuration ----------
    [ObservableProperty] private ScanType scanType = ScanType.Quick;
    public bool IsQuickSelected
    {
        get => ScanType == ScanType.Quick;
        set { if (value) ScanType = ScanType.Quick; }
    }
    public bool IsDeepSelected
    {
        get => ScanType == ScanType.Deep;
        set { if (value) ScanType = ScanType.Deep; }
    }
    partial void OnScanTypeChanged(ScanType value)
    {
        OnPropertyChanged(nameof(IsQuickSelected));
        OnPropertyChanged(nameof(IsDeepSelected));
    }

    [RelayCommand] private void SetQuickScan() => ScanType = ScanType.Quick;
    [RelayCommand] private void SetDeepScan() => ScanType = ScanType.Deep;

    public ObservableCollection<CategoryFilterOption> CategoryOptions { get; } = new(
        Enum.GetValues<FileCategory>().Select(c => new CategoryFilterOption(c)));

    // ---------- Step 3: scanning ----------
    [ObservableProperty] private double scanPercent;
    [ObservableProperty] private string scanStatusText = "";
    [ObservableProperty] private string scanEtaText = "";
    [ObservableProperty] private int scanTotalFound;
    public ObservableCollection<CategoryCount> LiveCategoryCounts { get; } = new();

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (SelectedVolume == null) return;
        CurrentStep = WizardStep.Scanning;
        ScanPercent = 0;
        ScanTotalFound = 0;
        ScanStatusText = "Starting scan…";
        _allResults.Clear();
        _scanCts = new CancellationTokenSource();

        var categoryFilter = CategoryOptions.Where(o => o.IsChecked).Select(o => o.Category).ToHashSet();
        var options = new ScanOptions { Volume = SelectedVolume, Type = ScanType, CategoryFilter = categoryFilter };

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanPercent = p.PercentComplete;
            ScanStatusText = p.StatusText;
            ScanTotalFound = p.TotalFound;
            ScanEtaText = p.EstimatedRemaining is { } eta ? $"About {FormatEta(eta)} remaining" : "";
            UpdateLiveCounts(p.CountsByCategory);
        });

        try
        {
            var results = await Task.Run(() => _engine.Scan(options, progress, _scanCts.Token), _scanCts.Token);
            _allResults.AddRange(results.Select(r => new RecoverableFileViewModel(r)));
            ApplyResultFilter();
            CurrentStep = WizardStep.Results;
        }
        catch (OperationCanceledException)
        {
            CurrentStep = WizardStep.ConfigureScan;
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(ex.Message, "Administrator access required", MessageBoxButton.OK, MessageBoxImage.Error);
            CurrentStep = WizardStep.ConfigureScan;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Scan failed: {ex.Message}", "Scan error", MessageBoxButton.OK, MessageBoxImage.Error);
            CurrentStep = WizardStep.ConfigureScan;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private void UpdateLiveCounts(IReadOnlyDictionary<FileCategory, int> counts)
    {
        foreach (var kv in counts)
        {
            var existing = LiveCategoryCounts.FirstOrDefault(c => c.Category == kv.Key);
            if (existing != null) existing.Count = kv.Value;
            else LiveCategoryCounts.Add(new CategoryCount(kv.Key, kv.Value));
        }
    }

    private static string FormatEta(TimeSpan eta) =>
        eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes}m" :
        eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes}m {eta.Seconds}s" : $"{eta.Seconds}s";

    // ---------- Step 4: results ----------
    public ObservableCollection<RecoverableFileViewModel> FilteredResults { get; } = new();
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private FileCategory? activeCategoryTab; // null = All
    [ObservableProperty] private RecoverableFileViewModel? selectedFile;

    partial void OnSearchTextChanged(string value) => ApplyResultFilter();
    partial void OnActiveCategoryTabChanged(FileCategory? value) => ApplyResultFilter();

    partial void OnSelectedFileChanged(RecoverableFileViewModel? value)
    {
        if (value is { IsImage: true, Thumbnail: null } && SelectedVolume != null)
        {
            _ = LoadThumbnailAsync(value);
        }
    }

    private async Task LoadThumbnailAsync(RecoverableFileViewModel vm)
    {
        try
        {
            byte[] bytes = await Task.Run(() => _previewService.ReadPreviewBytes(SelectedVolume!, vm.Model));
            if (bytes.Length == 0) return;
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            try { image.EndInit(); }
            catch { return; } // truncated/partially-overwritten image data — no preview available
            image.Freeze();
            vm.Thumbnail = image;
        }
        catch { /* preview is best-effort only */ }
    }

    private void ApplyResultFilter()
    {
        FilteredResults.Clear();
        IEnumerable<RecoverableFileViewModel> query = _allResults;
        if (ActiveCategoryTab.HasValue)
            query = query.Where(f => f.Category == ActiveCategoryTab.Value);
        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(f => f.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        foreach (var f in query.OrderByDescending(f => f.Recoverability == Recoverability.Excellent).ThenBy(f => f.Name))
            FilteredResults.Add(f);

        OnPropertyChanged(nameof(ResultCountsByCategory));
        OnPropertyChanged(nameof(SelectedCount));
    }

    public IEnumerable<CategoryCount> ResultCountsByCategory =>
        Enum.GetValues<FileCategory>().Select(c => new CategoryCount(c, _allResults.Count(f => f.Category == c)));

    public int SelectedCount => _allResults.Count(f => f.IsSelected);

    [RelayCommand]
    private void SelectCategoryTab(FileCategory? category)
    {
        ActiveCategoryTab = category;
    }

    [RelayCommand]
    private void SelectAllInCategory(FileCategory? category)
    {
        var target = category.HasValue ? _allResults.Where(f => f.Category == category.Value) : _allResults;
        foreach (var f in target) f.IsSelected = true;
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var f in _allResults) f.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private void GoToDestinationStep()
    {
        if (SelectedCount == 0)
        {
            MessageBox.Show("Select at least one file to recover first.", "No files selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        CurrentStep = WizardStep.ChooseDestination;
    }

    // ---------- Step 5: destination + recovery ----------
    [ObservableProperty] private string? destinationFolder;
    [ObservableProperty] private bool destinationLooksUnsafe;

    partial void OnDestinationFolderChanged(string? value)
    {
        DestinationLooksUnsafe = value != null && SelectedVolume != null &&
                                  DestinationSafety.IsSameDevice(SelectedVolume, value);
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a recovery destination — must NOT be the drive you scanned",
        };
        if (dialog.ShowDialog() == true)
        {
            DestinationFolder = dialog.FolderName;
        }
    }

    [ObservableProperty] private double recoveryPercent;
    [ObservableProperty] private string recoveryStatusText = "";
    [ObservableProperty] private RecoveryResult? recoveryResult;

    [RelayCommand]
    private async Task StartRecoveryAsync()
    {
        if (SelectedVolume == null || string.IsNullOrWhiteSpace(DestinationFolder)) return;

        if (DestinationSafety.IsSameDevice(SelectedVolume, DestinationFolder))
        {
            MessageBox.Show(
                "The destination you chose is on the same drive being recovered. " +
                "Pick a different physical drive (an external drive or another internal disk) to continue.",
                "Unsafe destination", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        CurrentStep = WizardStep.Recovering;
        RecoveryPercent = 0;
        _recoveryCts = new CancellationTokenSource();
        var selected = _allResults.Where(f => f.IsSelected).Select(f => f.Model).ToList();

        var progress = new Progress<RecoveryProgress>(p =>
        {
            RecoveryPercent = p.PercentComplete;
            RecoveryStatusText = $"Recovering {p.FilesDone} of {p.FilesTotal}: {p.CurrentFileName}";
        });

        try
        {
            var result = await Task.Run(() =>
                _engine.Recover(SelectedVolume, selected, DestinationFolder, progress, _recoveryCts.Token), _recoveryCts.Token);
            RecoveryResult = result;
            CurrentStep = WizardStep.Summary;
        }
        catch (OperationCanceledException)
        {
            CurrentStep = WizardStep.ChooseDestination;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Unsafe destination", MessageBoxButton.OK, MessageBoxImage.Error);
            CurrentStep = WizardStep.ChooseDestination;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Recovery failed: {ex.Message}", "Recovery error", MessageBoxButton.OK, MessageBoxImage.Error);
            CurrentStep = WizardStep.ChooseDestination;
        }
    }

    [RelayCommand]
    private void CancelRecovery() => _recoveryCts?.Cancel();

    [RelayCommand]
    private void OpenDestinationFolder()
    {
        if (RecoveryResult == null) return;
        Process.Start(new ProcessStartInfo { FileName = RecoveryResult.DestinationFolder, UseShellExecute = true });
    }

    [RelayCommand]
    private void StartOver()
    {
        SelectedVolume = null;
        _allResults.Clear();
        FilteredResults.Clear();
        LiveCategoryCounts.Clear();
        DestinationFolder = null;
        RecoveryResult = null;
        SearchText = "";
        ActiveCategoryTab = null;
        RefreshVolumes();
        CurrentStep = WizardStep.SelectVolume;
    }

    [RelayCommand]
    private void GoBack()
    {
        CurrentStep = CurrentStep switch
        {
            WizardStep.ConfigureScan => WizardStep.SelectVolume,
            WizardStep.Results => WizardStep.ConfigureScan,
            WizardStep.ChooseDestination => WizardStep.Results,
            _ => CurrentStep,
        };
    }
}

public partial class CategoryFilterOption : ObservableObject
{
    public FileCategory Category { get; }
    [ObservableProperty] private bool isChecked = true;
    public CategoryFilterOption(FileCategory category) => Category = category;
}

public partial class CategoryCount : ObservableObject
{
    public FileCategory Category { get; }
    [ObservableProperty] private int count;
    public CategoryCount(FileCategory category, int count) { Category = category; this.count = count; }
}
