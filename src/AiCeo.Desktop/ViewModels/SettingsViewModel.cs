using AiCeo.Desktop.Theme;
using AiCeo.Models;
using AiCeo.Services;
using Avalonia.Media;

namespace AiCeo.Desktop.ViewModels;

/// <summary>One selectable theme card (Dark / Light / System).</summary>
public sealed class ThemeOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public ThemeOptionViewModel(AppTheme value)
    {
        Value = value;
        Label = UiTheme.ThemeDisplayName(value);
        Description = value switch
        {
            AppTheme.Dark => "Deep slate, easy on the eyes",
            AppTheme.Light => "Bright, high contrast",
            _ => "Follows your operating system"
        };
    }

    public AppTheme Value { get; }

    public string Label { get; }

    public string Description { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One accent swatch in the settings panel.</summary>
public sealed class AccentOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public AccentOptionViewModel(AccentColor value)
    {
        Value = value;
        Label = UiTheme.AccentDisplayName(value);
        Swatch = new SolidColorBrush(Color.Parse(UiTheme.AccentHex(value)));
    }

    public AccentColor Value { get; }

    public string Label { get; }

    public IBrush Swatch { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(Marker));
            }
        }
    }

    /// <summary>Tick shown inside the selected swatch.</summary>
    public string Marker => IsSelected ? "✓" : string.Empty;
}

/// <summary>
/// The Settings panel: theme, accent colour and text size. Every change paints the UI immediately
/// (the "dynamic variable" behaviour) and is written to the <c>UserSettings</c> table shortly
/// afterwards, so dragging the size slider does not hit SQLite on every pixel.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static readonly TimeSpan PersistDelay = TimeSpan.FromMilliseconds(250);

    private readonly UserSettingsService _settings;
    private readonly Action _close;
    private readonly List<ThemeOptionViewModel> _themes;
    private readonly List<AccentOptionViewModel> _accents;

    private CancellationTokenSource? _persistCts;
    private AppTheme _theme = AppTheme.System;
    private AccentColor _accent = AccentColor.Green;
    private FontSizePreference _fontSize = FontSizePreference.Medium;
    private bool _isBusy;
    private string? _status;

    public SettingsViewModel(UserSettingsService settings, Action close)
    {
        _settings = settings;
        _close = close;

        _themes = new List<ThemeOptionViewModel>
        {
            new(AppTheme.Dark),
            new(AppTheme.Light),
            new(AppTheme.System)
        };

        _accents = new List<AccentOptionViewModel>
        {
            new(AccentColor.Green),
            new(AccentColor.Blue),
            new(AccentColor.Purple),
            new(AccentColor.Amber),
            new(AccentColor.Rose)
        };

        SelectThemeCommand = new RelayCommand<ThemeOptionViewModel>(option => SelectTheme(option.Value));
        SelectAccentCommand = new RelayCommand<AccentOptionViewModel>(option => SelectAccent(option.Value));
        ResetCommand = new AsyncRelayCommand(ResetAsync, () => !IsBusy);
        CloseCommand = new RelayCommand(close);
    }

    public IReadOnlyList<ThemeOptionViewModel> Themes => _themes;

    public IReadOnlyList<AccentOptionViewModel> Accents => _accents;

    public RelayCommand<ThemeOptionViewModel> SelectThemeCommand { get; }

    public RelayCommand<AccentOptionViewModel> SelectAccentCommand { get; }

    public AsyncRelayCommand ResetCommand { get; }

    public RelayCommand CloseCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ResetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>"Saved" / error line under the panel.</summary>
    public string? Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    /// <summary>The text-size slider position: 0 = Small, 1 = Medium, 2 = Large.</summary>
    public double FontSizeIndex
    {
        get => (int)_fontSize;
        set
        {
            var clamped = (FontSizePreference)Math.Clamp((int)Math.Round(value), 0, 2);

            if (clamped == _fontSize)
            {
                return;
            }

            _fontSize = clamped;
            OnPropertyChanged(nameof(FontSizeIndex), nameof(FontSizeLabel), nameof(Summary), nameof(FontPreviewSize));
            ApplyTheme();
            PersistSoon();
        }
    }

    public string FontSizeLabel => $"{UiTheme.FontDisplayName(_fontSize)} · {UiTheme.FontScale(_fontSize):0.00}×";

    /// <summary>Live preview size for the sample sentence.</summary>
    public double FontPreviewSize => 13 * UiTheme.FontScale(_fontSize) + 2;

    public string Summary => _theme switch
    {
        AppTheme.Dark => $"Dark · {UiTheme.AccentDisplayName(_accent)} accent · {UiTheme.FontDisplayName(_fontSize)} text",
        AppTheme.Light => $"Light · {UiTheme.AccentDisplayName(_accent)} accent · {UiTheme.FontDisplayName(_fontSize)} text",
        _ => $"System ({ThemeManager.Current?.Variant.ToString() ?? "auto"}) · {UiTheme.AccentDisplayName(_accent)} accent · {UiTheme.FontDisplayName(_fontSize)} text"
    };

    /// <summary>Reads the saved preferences and paints them.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var snapshot = await _settings.GetAsync();
            _theme = snapshot.Theme;
            _accent = snapshot.AccentColor;
            _fontSize = snapshot.FontSize;
        }
        catch (Exception exception)
        {
            // Best effort: a broken database must never stop the panel from opening.
            Status = "Could not read saved settings: " + exception.Message;
        }

        foreach (var option in _themes)
        {
            option.IsSelected = option.Value == _theme;
        }

        foreach (var option in _accents)
        {
            option.IsSelected = option.Value == _accent;
        }

        OnPropertyChanged(nameof(FontSizeIndex), nameof(FontSizeLabel), nameof(Summary));
        ApplyTheme();
    }

    private void SelectTheme(AppTheme theme)
    {
        if (_theme == theme)
        {
            return;
        }

        _theme = theme;
        foreach (var option in _themes)
        {
            option.IsSelected = option.Value == theme;
        }

        OnPropertyChanged(nameof(Summary));
        ApplyTheme();
        PersistSoon();
    }

    private void SelectAccent(AccentColor accent)
    {
        if (_accent == accent)
        {
            return;
        }

        _accent = accent;
        foreach (var option in _accents)
        {
            option.IsSelected = option.Value == accent;
        }

        OnPropertyChanged(nameof(Summary));
        ApplyTheme();
        PersistSoon();
    }

    /// <summary>Paints the shell with the current choices.</summary>
    public void ApplyTheme() => ThemeManager.Apply(new UserSettingsSnapshot(_theme, _accent, _fontSize));

    /// <summary>Repaints after the OS light/dark setting changed (only matters for System).</summary>
    public void OnSystemThemeChanged()
    {
        ThemeManager.ReapplyIfFollowingSystem();
        OnPropertyChanged(nameof(Summary));
    }

    private async Task ResetAsync()
    {
        _theme = UserSettingsSnapshot.Default.Theme;
        _accent = UserSettingsSnapshot.Default.AccentColor;
        _fontSize = UserSettingsSnapshot.Default.FontSize;

        foreach (var option in _themes)
        {
            option.IsSelected = option.Value == _theme;
        }

        foreach (var option in _accents)
        {
            option.IsSelected = option.Value == _accent;
        }

        OnPropertyChanged(nameof(FontSizeIndex), nameof(FontSizeLabel), nameof(Summary));
        ApplyTheme();
        await PersistAsync(UserSettingsSnapshot.Default);
    }

    /// <summary>Coalesces rapid changes (slider drags) into one write.</summary>
    private void PersistSoon()
    {
        _persistCts?.Cancel();
        _persistCts?.Dispose();
        _persistCts = new CancellationTokenSource();

        // Started on the UI thread, so the continuations (and the Status update) stay on it.
        _ = PersistAfterDelayAsync(new UserSettingsSnapshot(_theme, _accent, _fontSize), _persistCts.Token);
    }

    private async Task PersistAfterDelayAsync(UserSettingsSnapshot snapshot, CancellationToken token)
    {
        try
        {
            await Task.Delay(PersistDelay, token);

            if (!token.IsCancellationRequested)
            {
                await PersistAsync(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change.
        }
    }

    private async Task PersistAsync(UserSettingsSnapshot snapshot)
    {
        try
        {
            IsBusy = true;
            await _settings.SaveAsync(snapshot);
            Status = "Saved";
        }
        catch (Exception exception)
        {
            Status = "Could not save settings: " + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
