using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AegisComputerUse.App.Controls;
using AegisComputerUse.Core.Files;
using AegisComputerUse.Core.Gemini;
using AegisComputerUse.Core.Native;
using AegisComputerUse.Core.Security;
using Microsoft.Win32;

namespace AegisComputerUse.App;

public partial class MainWindow : Window
{
    private const int KillSwitchHotkeyId = 0xA361;
    private const int LiveVoiceHotkeyId = 0xA362;
    private const int WmHotkey = 0x0312;

    private bool _isLiveVoiceActive;
    private bool _isLiveVoiceTranscribing;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".cs", ".py", ".js", ".ts",
        ".html", ".css", ".sql", ".svg", ".log", ".ini", ".toml", ".java", ".cpp", ".c", ".h", ".rs", ".go"
    };

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo",
        [".mkv"] = "video/x-matroska",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mp3",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".aac"] = "audio/aac",
        [".pdf"] = "application/pdf"
    };

    private readonly MultiModelVault _multiVault;
    private readonly GeminiApiClient _apiClient;
    private readonly MultiProviderTransport _multiProvider;
    private readonly bool _ownsApiClient;
    private readonly bool _ownsMultiProvider;
    private readonly ScreenCaptureService _screenCapture = new();
    private readonly InputAutomationService _inputAutomation = new();
    private readonly VoiceCaptureService _voiceCapture = new();
    private readonly List<JsonNode> _history = [];

    private readonly List<ConfiguredModelEntry> _configuredModels = [];
    private readonly Dictionary<AiProviderKind, IReadOnlyList<ProviderModelCatalogItem>> _liveModelsByProvider = [];
    private readonly Dictionary<string, ComputerUseToolMode> _adaptedModeByModel = new(StringComparer.OrdinalIgnoreCase);
    private ConfiguredModelEntry? _activeModel;

    private readonly ObservableCollection<AttachmentViewItem> _attachments = [];
    private readonly ObservableCollection<CreatedFileViewItem> _createdFiles = [];
    private readonly ObservableCollection<ChatEntry> _chatEntries = [];
    private readonly ObservableCollection<ChatSessionItem> _chatSessions = [];
    private ChatSessionItem? _activeSession;
    private readonly ObservableCollection<DetectedModelCardItem> _detectedModels = [];
    private bool _isSidebarCollapsed;
    private readonly ObservableCollection<ModelMenuItem> _popupModelItems = [];
    private readonly ObservableCollection<ConfiguredModelViewItem> _settingsModelItems = [];
    private readonly ObservableCollection<WindowScopeItem> _scopeItems = [];

    private string _sandboxRoot;
    private CancellationTokenSource? _runCts;
    private HwndSource? _hwndSource;
    private IntPtr _mainHwnd;

    private AiProviderKind _selectedSettingsProvider = AiProviderKind.GoogleGemini;
    private ModelMenuItem? _hoveredModelItem;
    private readonly DispatcherTimer _submenuSwitchTimer = new() { Interval = TimeSpan.FromMilliseconds(130) };
    private Action? _pendingSubmenuSwitch;

    private IntPtr _selectedWindowHandle = IntPtr.Zero;
    private string _selectedWindowTitle = "Весь виртуальный экран";

    private GeminiUsageMetadata? _lastUsage;
    private long _sessionCumulativeTokens;
    private FailedPromptRetryData? _lastFailedPrompt;

    internal TimeSpan AgentRetryBaseDelayForTests { get; set; } = TimeSpan.FromMilliseconds(1200);
    internal IReadOnlyList<JsonNode> HistoryForTests => _history;
    internal IReadOnlyList<ChatEntry> ChatEntriesForTests => _chatEntries;

    internal sealed record FailedPromptRetryData(string Prompt, List<AttachmentInput> Attachments, List<ImageSource> Thumbnails);

    public MainWindow() : this(null, null, null)
    {
    }

    internal MainWindow(
        MultiModelVault? customVault,
        GeminiApiClient? customApiClient = null,
        MultiProviderTransport? customMultiProvider = null)
    {
        _multiVault = customVault ?? new MultiModelVault();
        _ownsApiClient = customApiClient is null;
        _ownsMultiProvider = customMultiProvider is null;
        _apiClient = customApiClient ?? new GeminiApiClient();
        _multiProvider = customMultiProvider ?? new MultiProviderTransport();
        InitializeComponent();

        _submenuSwitchTimer.Tick += (_, _) =>
        {
            _submenuSwitchTimer.Stop();
            var action = _pendingSubmenuSwitch;
            _pendingSubmenuSwitch = null;
            if (UnifiedSelectorPopup.IsOpen)
            {
                action?.Invoke();
            }
        };

        _sandboxRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AegisWorkspace");

        AttachmentsList.ItemsSource = _attachments;
        CreatedFilesList.ItemsSource = _createdFiles;
        ChatItems.ItemsSource = _chatEntries;
        ChatSessionsList.ItemsSource = _chatSessions;
        DetectedModelsList.ItemsSource = _detectedModels;
        ModelRowsControl.ItemsSource = _popupModelItems;
        ConfiguredModelsList.ItemsSource = _settingsModelItems;
        WindowScopeRows.ItemsSource = _scopeItems;

        _attachments.CollectionChanged += (_, _) =>
            AttachmentsList.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _chatEntries.CollectionChanged += (_, _) =>
            EmptyStatePanel.Visibility = _chatEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        CreateNewChatSession("Диалог 1");
        SandboxPathText.Text = _sandboxRoot;
        UpdateSidebarDirectoryUi();
        RefreshHardeningBadge();
        TryAutoLoadDpapiVault();
        PopulateProviderVersions(_selectedSettingsProvider);
        RefreshConfiguredModelsUi();
        RefreshUnifiedSelectorUi();
        UpdateUsageUi();

        _voiceCapture.UtteranceCompleted += OnLiveVoiceUtteranceCompleted;
        _voiceCapture.SpeechActivityChanged += OnLiveVoiceSpeechActivityChanged;
        LoadAppSettings();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => RefreshWindowsList();
        Deactivated += (_, _) => CloseAllPopups();
        Closed += OnWindowClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _mainHwnd = new WindowInteropHelper(this).Handle;
        if (_mainHwnd != IntPtr.Zero)
        {
            Win32Interop.EnableModernDarkFrame(_mainHwnd);
            ApplyCaptureExclusion();
            _hwndSource = HwndSource.FromHwnd(_mainHwnd);
            _hwndSource?.AddHook(WndProcHook);
            Win32Interop.RegisterHotKey(
                _mainHwnd,
                KillSwitchHotkeyId,
                Win32Interop.MOD_CONTROL | Win32Interop.MOD_SHIFT | Win32Interop.MOD_NOREPEAT,
                Win32Interop.VK_F12);
            Win32Interop.RegisterHotKey(
                _mainHwnd,
                LiveVoiceHotkeyId,
                Win32Interop.MOD_CONTROL | Win32Interop.MOD_ALT | Win32Interop.MOD_NOREPEAT,
                Win32Interop.VK_V);
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateWindowStateGlyph();
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateWindowStateGlyph();

    private void UpdateWindowStateGlyph()
    {
        if (MaximizeGlyph is not null)
        {
            MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "❐" : "□";
        }
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int hotkeyId = wParam.ToInt32();
            if (hotkeyId == KillSwitchHotkeyId)
            {
                TriggerKillSwitch("Активирован аппаратный Kill-Switch (Ctrl+Shift+F12).");
                handled = true;
            }
            else if (hotkeyId == LiveVoiceHotkeyId)
            {
                ToggleLiveVoiceMode();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        CloseAllPopups();
        _submenuSwitchTimer.Stop();
        _runCts?.Cancel();
        _runCts?.Dispose();
        if (_mainHwnd != IntPtr.Zero)
        {
            Win32Interop.UnregisterHotKey(_mainHwnd, KillSwitchHotkeyId);
            Win32Interop.UnregisterHotKey(_mainHwnd, LiveVoiceHotkeyId);
        }
        _hwndSource?.RemoveHook(WndProcHook);
        DisposeAllConfiguredModels();
        _voiceCapture.Dispose();
        if (_ownsMultiProvider)
            _multiProvider.Dispose();
        if (_ownsApiClient)
            _apiClient.Dispose();
    }

    private void DisposeAllConfiguredModels()
    {
        foreach (var entry in _configuredModels)
        {
            entry.Dispose();
        }
        _configuredModels.Clear();
        _activeModel = null;
    }

    private void TryAutoLoadDpapiVault()
    {
        try
        {
            if (_multiVault.VaultExists && !_multiVault.RequiresPassphrase())
            {
                var loaded = _multiVault.LoadEntries();
                _configuredModels.AddRange(loaded);
                _activeModel = _configuredModels.FirstOrDefault();
            }
        }
        catch
        {
            // Keep UI responsive; user can unlock or reconfigure inside Settings
        }
    }

    private void RefreshHardeningBadge()
    {
        var r = App.HardeningReport;
        HardeningBadgeText.Text =
            $"DLL:{(r.DllDirectoriesRestricted ? "OK" : "—")} • Heap:{(r.HeapTerminationEnabled ? "OK" : "—")} • TLS 1.3 • Anti-Capture";
    }

    private void ApplyCaptureExclusion()
    {
        if (_mainHwnd == IntPtr.Zero) return;
        bool isExcluded = ExcludeCaptureCheck.IsChecked == true;
        uint affinity = isExcluded
            ? Win32Interop.WDA_EXCLUDEFROMCAPTURE
            : Win32Interop.WDA_NONE;
        Win32Interop.SetWindowDisplayAffinity(_mainHwnd, affinity);

        if (StreamVisibilityBtn is not null)
        {
            StreamVisibilityIcon.Text = isExcluded ? "🛡" : "👁";
            StreamVisibilityText.Text = isExcluded ? "В стриме: Скрыто" : "В стриме: Видимо";
            StreamVisibilityText.Foreground = isExcluded ? BrushFromHex("#94A3B8") : BrushFromHex("#38BDF8");
            StreamVisibilityBtn.ToolTip = isExcluded
                ? "Окно Aegis скрыто от захвата DWM (не видно в Discord / OBS / скриншотах). Нажмите, чтобы сделать видимым."
                : "Окно Aegis полностью видимо в Discord / OBS / скриншотах. Нажмите, чтобы скрыть.";
        }
    }

    private void OnExcludeCaptureChanged(object sender, RoutedEventArgs e)
    {
        ApplyCaptureExclusion();
        SaveAppSettings();
    }

    private void OnToggleStreamVisibilityClick(object sender, RoutedEventArgs e)
    {
        ExcludeCaptureCheck.IsChecked = ExcludeCaptureCheck.IsChecked != true;
        ApplyCaptureExclusion();
        SaveAppSettings();

        bool isExcluded = ExcludeCaptureCheck.IsChecked == true;
        AddStatusCard(
            isExcluded ? "ЗАЩИТА СТРИМА ВКЛЮЧЕНА" : "РЕЖИМ ТРАНСЛЯЦИИ ВКЛЮЧЕН",
            isExcluded
                ? "Окно Aegis скрыто от захвата экрана DWM (теперь не отображается в Discord, OBS и скриншотах)."
                : "Окно Aegis теперь полностью видимо при демонстрации экрана в Discord, OBS и на скриншотах.",
            isExcluded ? "#8B94A6" : "#38BDF8",
            isExcluded ? "#14171F" : "#0F1B29",
            isExcluded ? "#262C38" : "#1E3A5F",
            isExcluded ? "🛡" : "👁");
    }

    // ==================== Settings Modal & Add Model Workflow ====================

    internal void OpenSettingsModal(string tab = "Models")
    {
        CloseAllPopups();
        SwitchSettingsTab(tab);
        RefreshConfiguredModelsUi();
        SettingsAddErrorText.Visibility = Visibility.Collapsed;
        SettingsAddSuccessText.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var slideUp = new DoubleAnimation(24.0, 0.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        SettingsModalTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    internal void CloseSettingsModal()
    {
        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        var slideDown = new DoubleAnimation(0.0, 16.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        };
        SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        SettingsModalTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsModal("Models");

    private void OnCloseSettingsClick(object sender, RoutedEventArgs e) => CloseSettingsModal();

    private void OnSettingsBackdropMouseDown(object sender, MouseButtonEventArgs e) => CloseSettingsModal();

    private void OnSettingsModalContentMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnSwitchSettingsTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab })
            SwitchSettingsTab(tab);
    }

    private void SwitchSettingsTab(string tab)
    {
        SettingsTabModels.Visibility = tab == "Models" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabSecurity.Visibility = tab == "Security" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabSandbox.Visibility = tab == "Sandbox" ? Visibility.Visible : Visibility.Collapsed;

        TabModelsBtn.Style = (Style)FindResource(tab == "Models" ? "DarkButton" : "GhostPillButton");
        TabSecurityBtn.Style = (Style)FindResource(tab == "Security" ? "DarkButton" : "GhostPillButton");
        TabSandboxBtn.Style = (Style)FindResource(tab == "Sandbox" ? "DarkButton" : "GhostPillButton");
    }

    private void OnSelectProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<AiProviderKind>(tag, out var provider))
        {
            _selectedSettingsProvider = provider;
            ProviderGoogleBtn.Style = (Style)FindResource(provider == AiProviderKind.GoogleGemini ? "PrimaryButton" : "DarkButton");
            ProviderOpenAiBtn.Style = (Style)FindResource(provider == AiProviderKind.OpenAI ? "PrimaryButton" : "DarkButton");
            ProviderAnthropicBtn.Style = (Style)FindResource(provider == AiProviderKind.Anthropic ? "PrimaryButton" : "DarkButton");
            _detectedModels.Clear();
            PopulateProviderVersions(provider);
        }
    }

    private void PopulateProviderVersions(AiProviderKind provider)
    {
        SettingsVersionCombo.Items.Clear();
        IReadOnlyList<ProviderModelCatalogItem> catalog = _liveModelsByProvider.TryGetValue(provider, out var discovered) && discovered.Count > 0
            ? discovered
            : AiProviderCatalog.GetModelsForProvider(provider);

        foreach (var m in catalog)
        {
            string label = string.Equals(m.DisplayName, m.ModelId, StringComparison.OrdinalIgnoreCase)
                ? m.ModelId
                : $"{m.DisplayName}  •  {m.ModelId}";
            SettingsVersionCombo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = m
            });
        }
        SettingsVersionCombo.Items.Add(new ComboBoxItem
        {
            Content = "✏ Ввести другую версию (свой ID модели)...",
            Tag = null
        });
        CustomVersionHint.Text = provider switch
        {
            AiProviderKind.OpenAI => "Точный ID модели (например: gpt-5.4)",
            AiProviderKind.Anthropic => "Точный ID модели (например: claude-opus-4-6)",
            _ => "Точный ID модели (например: gemini-2.5-flash)"
        };
        SettingsVersionCombo.SelectedIndex = 0;
    }

    internal (string ModelId, string DisplayName) GetSelectedSettingsModelInfo()
    {
        if (SettingsVersionCombo.SelectedItem is ComboBoxItem { Tag: ProviderModelCatalogItem catalogItem })
        {
            return (catalogItem.ModelId, catalogItem.DisplayName);
        }

        string safeId = GeminiApiClient.ValidateModelId(SettingsCustomVersionBox.Text);
        return (safeId, safeId);
    }

    internal bool SelectSettingsModelId(string targetModelId)
    {
        for (int i = 0; i < SettingsVersionCombo.Items.Count; i++)
        {
            if (SettingsVersionCombo.Items[i] is ComboBoxItem { Tag: ProviderModelCatalogItem item } &&
                string.Equals(item.ModelId, targetModelId, StringComparison.OrdinalIgnoreCase))
            {
                SettingsVersionCombo.SelectedIndex = i;
                return true;
            }
        }
        SettingsCustomVersionBox.Text = targetModelId;
        if (SettingsVersionCombo.Items.Count > 0)
        {
            SettingsVersionCombo.SelectedIndex = SettingsVersionCombo.Items.Count - 1;
        }
        return true;
    }

    internal static string FormatForbiddenMessage(string modelId, GeminiApiException ex)
    {
        string raw = ex.Message;
        if (raw.Contains("User location is not supported", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("location is not supported", StringComparison.OrdinalIgnoreCase))
        {
            return $"Доступ к Gemini заблокирован по региону (HTTP 403: User location is not supported).\n" +
                   $"💡 Решение: укажите адрес локального VPN/прокси в секции «🌐 Сеть и Прокси» ниже (например, нажмите «Clash (7890)» или «v2ray (10809)») для обхода региональной блокировки Google.";
        }
        if (raw.Contains("API has not been used", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Generative Language API has not been used", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("it is disabled", StringComparison.OrdinalIgnoreCase))
        {
            return $"API Generative Language отключен в проекте Google Cloud (HTTP 403).\n" +
                   $"💡 Решение: перейдите в Google Cloud Console и включите Generative Language API для проекта вашего ключа.";
        }
        return $"API-ключ отклонён провайдером (HTTP {ex.StatusCode}): {LogRedactor.Redact(ex.Message)}.\n" +
               $"💡 Проверьте правильность ключа, права доступа проекта или настройте прокси в секции «🌐 Сеть и Прокси».";
    }

    private async void OnProbeModelClick(object sender, RoutedEventArgs e) =>
        await ProbeModelFromSettingsAsync();

    internal async Task ProbeModelFromSettingsAsync()
    {
        SettingsAddErrorText.Visibility = Visibility.Collapsed;
        SettingsAddSuccessText.Visibility = Visibility.Collapsed;

        string modelId;
        try
        {
            (modelId, _) = GetSelectedSettingsModelInfo();
        }
        catch (Exception ex)
        {
            SettingsAddErrorText.Text = LogRedactor.Redact(ex.Message);
            SettingsAddErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (SettingsApiKeyBox.SecurePassword.Length == 0)
        {
            SettingsAddErrorText.Text = "Сначала вставьте API-ключ выше, затем нажмите «⚡ Проверить ключ».";
            SettingsAddErrorText.Visibility = Visibility.Visible;
            if (IsLoaded) SettingsApiKeyBox.Focus();
            return;
        }

        AiProviderKind targetProvider = _selectedSettingsProvider;
        ProbeModelBtn.IsEnabled = false;
        ProbeModelBtn.Content = "⏳ Проверка...";
        try
        {
            using SealedSecret probeKey = CreateTransientSealedSecretWithoutClearing(SettingsApiKeyBox);
            long elapsedMs = targetProvider == AiProviderKind.GoogleGemini
                ? await _apiClient.ProbeModelAsync(probeKey, modelId, CancellationToken.None)
                : await _multiProvider.ProbeModelAsync(targetProvider, probeKey, modelId, CancellationToken.None);

            SettingsAddSuccessText.Text = $"✓ Модель «{modelId}» доступна и ответила за {elapsedMs} мс. Можно нажимать «＋ Добавить нейросеть».";
            SettingsAddSuccessText.Visibility = Visibility.Visible;
        }
        catch (GeminiApiException ex) when (ex.StatusCode is 401 or 403 or 429 or 500 or 502 or 503 or 504 or 529)
        {
            // Auto-recovery: if user probed an unavailable model (like argon on free tier or 3.8-flash on overload), probe gemini-2.5-flash
            if (targetProvider == AiProviderKind.GoogleGemini &&
                !string.Equals(modelId, "gemini-2.5-flash", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using SealedSecret retryKey = CreateTransientSealedSecretWithoutClearing(SettingsApiKeyBox);
                    long flashMs = await _apiClient.ProbeModelAsync(retryKey, "gemini-2.5-flash", CancellationToken.None);
                    SelectSettingsModelId("gemini-2.5-flash");
                    SettingsAddSuccessText.Text = $"✓ Ваш API-ключ действителен и полностью работает! Модель «{modelId}» вернула {ex.StatusCode}, но резервная быстрая модель «gemini-2.5-flash» успешно ответила ({flashMs} мс). Выбор автоматически переключен на «gemini-2.5-flash» — нажмите «＋ Добавить нейросеть»!";
                    SettingsAddSuccessText.Visibility = Visibility.Visible;
                    return;
                }
                catch
                {
                    // Fall through to show the specific diagnosis message for original failure
                }
            }

            if (ex.StatusCode == 429)
            {
                SettingsAddErrorText.Text = FormatRateLimitMessage(modelId, ex);
            }
            else if (ex.StatusCode is 401 or 403)
            {
                SettingsAddErrorText.Text = FormatForbiddenMessage(modelId, ex);
            }
            else
            {
                SettingsAddErrorText.Text = FormatTransientOverloadMessage(modelId, ex);
            }
            SettingsAddErrorText.Visibility = Visibility.Visible;
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 404)
        {
            SettingsAddErrorText.Text = $"Модель «{modelId}» не найдена (HTTP 404). Нажмите «🔄 По ключу», чтобы загрузить актуальный список моделей.";
            SettingsAddErrorText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            SettingsAddErrorText.Text = LogRedactor.Redact(ex.Message);
            SettingsAddErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            ProbeModelBtn.Content = "⚡ Проверить ключ";
            ProbeModelBtn.IsEnabled = true;
        }
    }

    private async void OnLoadModelsFromKeyClick(object sender, RoutedEventArgs e) =>
        await LoadModelsFromKeyAsync();

    internal async Task LoadModelsFromKeyAsync()
    {
        SettingsAddErrorText.Visibility = Visibility.Collapsed;
        SettingsAddSuccessText.Visibility = Visibility.Collapsed;

        if (SettingsApiKeyBox.SecurePassword.Length == 0)
        {
            SettingsAddErrorText.Text = "Сначала вставьте API-ключ в шаг 3 ниже, затем нажмите «🔄 По ключу» — список доступных моделей загрузится автоматически.";
            SettingsAddErrorText.Visibility = Visibility.Visible;
            if (IsLoaded) SettingsApiKeyBox.Focus();
            return;
        }

        AiProviderKind targetProvider = _selectedSettingsProvider;
        LoadModelsFromKeyBtn.IsEnabled = false;
        LoadModelsFromKeyBtn.Content = "⏳ Загрузка...";
        try
        {
            using SealedSecret probeKey = CreateTransientSealedSecretWithoutClearing(SettingsApiKeyBox);
            IReadOnlyList<ProviderModelCatalogItem> discovered = targetProvider == AiProviderKind.GoogleGemini
                ? await _apiClient.ListModelsAsync(probeKey, CancellationToken.None)
                : await _multiProvider.ListModelsAsync(targetProvider, probeKey, CancellationToken.None);

            if (discovered.Count == 0)
            {
                SettingsAddErrorText.Text = $"Провайдер {AiProviderCatalog.GetProviderDisplayName(targetProvider)} не вернул моделей для этого ключа. Вы можете ввести точный ID вручную.";
                SettingsAddErrorText.Visibility = Visibility.Visible;
                return;
            }

            _liveModelsByProvider[targetProvider] = discovered;
            if (_selectedSettingsProvider == targetProvider)
            {
                PopulateProviderVersions(targetProvider);
            }

            _detectedModels.Clear();
            foreach (var m in discovered)
            {
                bool isRecommended = m.ModelId.Contains("flash", StringComparison.OrdinalIgnoreCase) && !m.ModelId.Contains("argon", StringComparison.OrdinalIgnoreCase);
                bool isArgon = m.ModelId.Contains("argon", StringComparison.OrdinalIgnoreCase);

                string badge = isRecommended ? "⚡ Рекомендуется • Голос, экран, файлы" :
                               isArgon ? "⚠ Требует Pay-as-you-go" :
                               m.DisplayName.Contains("Pro") ? "🧠 Глубокие рассуждения" : "✓ Доступно";

                Brush badgeBg = isRecommended ? BrushFromHex("#064E3B") :
                                isArgon ? BrushFromHex("#3B1D11") : BrushFromHex("#1E293B");
                Brush badgeFg = isRecommended ? BrushFromHex("#34D399") :
                                isArgon ? BrushFromHex("#F97316") : BrushFromHex("#94A3B8");

                string status = isArgon ? "Free Tier: 0 лимит" : "✓ Квота доступна";
                Brush statusBrush = isArgon ? BrushFromHex("#F59E0B") : BrushFromHex("#34D399");

                _detectedModels.Add(new DetectedModelCardItem(
                    ModelId: m.ModelId,
                    DisplayName: m.DisplayName,
                    BadgeText: badge,
                    BadgeBackground: badgeBg,
                    BadgeForeground: badgeFg,
                    StatusText: status,
                    StatusBrush: statusBrush,
                    Description: m.Notes,
                    ActionBtnText: "⚡ Подключить",
                    CardBackground: BrushFromHex("#11151F"),
                    CardBorder: isRecommended ? BrushFromHex("#1D4ED8") : BrushFromHex("#222A3B"),
                    TitleBrush: isRecommended ? BrushFromHex("#60A5FA") : BrushFromHex("#F1F5F9"),
                    CatalogItem: m
                ));
            }

            SettingsAddSuccessText.Text = $"✓ Найдено {discovered.Count} доступных нейросетей по вашему ключу! Нажмите «⚡ Подключить» на любой карточке.";
            SettingsAddSuccessText.Visibility = Visibility.Visible;
        }
        catch (GeminiApiException ex) when (ex.StatusCode is 401 or 403)
        {
            SettingsAddErrorText.Text = FormatForbiddenMessage(AiProviderCatalog.GetProviderDisplayName(targetProvider), ex);
            SettingsAddErrorText.Visibility = Visibility.Visible;
        }
        catch (GeminiApiException ex) when (ex.StatusCode is 500 or 502 or 503 or 504 or 529)
        {
            SettingsAddErrorText.Text = FormatTransientOverloadMessage(AiProviderCatalog.GetProviderDisplayName(targetProvider), ex);
            SettingsAddErrorText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            SettingsAddErrorText.Text = LogRedactor.Redact(ex.Message);
            SettingsAddErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadModelsFromKeyBtn.Content = "⚡ Определить доступные модели";
            LoadModelsFromKeyBtn.IsEnabled = true;
        }
    }

    private void OnSettingsVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomVersionPanel is null || SettingsVersionCombo is null)
            return;

        if (SettingsVersionCombo.SelectedItem is ComboBoxItem { Tag: ProviderModelCatalogItem catalogItem })
        {
            CustomVersionPanel.Visibility = Visibility.Collapsed;
            if (SelectedModelNoteText is not null)
            {
                SelectedModelNoteText.Text = catalogItem.Notes;
                SelectedModelNoteText.Visibility = string.IsNullOrWhiteSpace(catalogItem.Notes)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }
        else
        {
            CustomVersionPanel.Visibility = Visibility.Visible;
            if (SelectedModelNoteText is not null)
            {
                SelectedModelNoteText.Text = "Введите точный идентификатор модели вручную.";
                SelectedModelNoteText.Visibility = Visibility.Visible;
            }
            if (IsLoaded) SettingsCustomVersionBox.Focus();
        }
    }

    private void OnCustomVersionTextChanged(object sender, TextChangedEventArgs e)
    {
        if (CustomVersionHint is null || SettingsCustomVersionBox is null)
            return;

        CustomVersionHint.Visibility = string.IsNullOrEmpty(SettingsCustomVersionBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnPersistCheckChanged(object sender, RoutedEventArgs e)
    {
        if (SettingsVaultPassPanel is null || SettingsPersistVaultCheck is null)
            return;

        SettingsVaultPassPanel.Visibility = SettingsPersistVaultCheck.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnAddModelConfirmClick(object sender, RoutedEventArgs e)
    {
        SettingsAddErrorText.Visibility = Visibility.Collapsed;
        SettingsAddSuccessText.Visibility = Visibility.Collapsed;
        try
        {
            (string modelId, string displayName) = GetSelectedSettingsModelInfo();

            if (SettingsApiKeyBox.SecurePassword.Length == 0)
                throw new InvalidOperationException("Введите API-ключ для выбранной нейросети.");

            SealedSecret sealedKey = CreateSealedSecretFromPasswordBox(SettingsApiKeyBox);
            bool persist = SettingsPersistVaultCheck.IsChecked == true;

            try
            {
                if (persist && _multiVault.VaultExists && _multiVault.RequiresPassphrase() && SettingsMasterPassBox.SecurePassword.Length == 0)
                {
                    throw new InvalidOperationException("Текущий Vault защищён мастер-паролем. Введите мастер-пароль в поле ниже либо снимите галочку для сохранения только в RAM.");
                }

                // Remove existing entry for same provider + model ID if replacing key
                for (int i = _configuredModels.Count - 1; i >= 0; i--)
                {
                    var existing = _configuredModels[i];
                    if (existing.Provider == _selectedSettingsProvider &&
                        string.Equals(existing.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Dispose();
                        _configuredModels.RemoveAt(i);
                    }
                }

                var newEntry = new ConfiguredModelEntry(
                    Guid.NewGuid().ToString("N"),
                    _selectedSettingsProvider,
                    modelId,
                    displayName,
                    "MEDIUM",
                    persist,
                    sealedKey);

                _configuredModels.Add(newEntry);
                _activeModel = newEntry;

                if (persist)
                {
                    SavePersistedEntriesFromSettingsPassphrase();
                }

                SettingsMasterPassBox.Clear();
                SettingsCustomVersionBox.Clear();

                RefreshConfiguredModelsUi();
                RefreshUnifiedSelectorUi();
                UpdateUsageUi();

                SettingsAddSuccessText.Text = $"✓ Модель {newEntry.DisplayName} ({newEntry.ModelId}) подключена и активна. Закройте настройки (✕) для работы в чате или добавьте ещё одну модель.";
                SettingsAddSuccessText.Visibility = Visibility.Visible;

                AddStatusCard(
                    "МОДЕЛЬ ДОБАВЛЕНА",
                    $"{AiProviderCatalog.GetProviderDisplayName(newEntry.Provider)} • {newEntry.DisplayName} ({newEntry.ModelId}) подключена ({(persist ? "DPAPI + AES-GCM Vault" : "Только RAM AES-GCM")}).",
                    "#10B981", "#0D201A", "#164E3A", "🤖");
            }
            catch
            {
                sealedKey.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            SettingsAddErrorText.Text = LogRedactor.Redact(ex.Message);
            SettingsAddErrorText.Visibility = Visibility.Visible;
        }
    }

    private void SavePersistedEntriesFromSettingsPassphrase()
    {
        var persisted = _configuredModels.Where(m => m.SavedInVault && !m.Secret.IsDisposed).ToList();
        if (persisted.Count == 0)
        {
            _multiVault.DeleteVault();
            return;
        }

        UseSecureStringChars(SettingsMasterPassBox.SecurePassword, passChars =>
        {
            _multiVault.SaveEntries(persisted, passChars);
        });
    }

    private void OnUnlockMultiVaultClick(object sender, RoutedEventArgs e)
    {
        SettingsAddErrorText.Visibility = Visibility.Collapsed;
        try
        {
            List<ConfiguredModelEntry> unlocked = UseSecureStringCharsResult(
                UnlockVaultPassBox.SecurePassword,
                passChars => _multiVault.LoadEntries(passChars));
            UnlockVaultPassBox.Clear();

            foreach (var entry in unlocked)
            {
                for (int i = _configuredModels.Count - 1; i >= 0; i--)
                {
                    if (_configuredModels[i].Provider == entry.Provider &&
                        string.Equals(_configuredModels[i].ModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase))
                    {
                        _configuredModels[i].Dispose();
                        _configuredModels.RemoveAt(i);
                    }
                }
                _configuredModels.Add(entry);
            }

            _activeModel ??= _configuredModels.FirstOrDefault();
            RefreshConfiguredModelsUi();
            RefreshUnifiedSelectorUi();
            UpdateUsageUi();

            AddStatusCard("VAULT РАЗБЛОКИРОВАН", $"Загружено моделей в защищённую RAM: {unlocked.Count}.", "#10B981", "#0D201A", "#164E3A", "🔓");
        }
        catch (Exception ex)
        {
            SettingsAddErrorText.Text = LogRedactor.Redact(ex.Message);
            SettingsAddErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnSelectDetectedModelClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DetectedModelCardItem item })
        {
            SelectAndAddDetectedModel(item);
        }
    }

    private void SelectAndAddDetectedModel(DetectedModelCardItem item)
    {
        SettingsAddErrorText.Visibility = Visibility.Collapsed;
        SettingsAddSuccessText.Visibility = Visibility.Collapsed;
        try
        {
            if (SettingsApiKeyBox.SecurePassword.Length == 0)
                throw new InvalidOperationException("Введите API-ключ в поле выше.");

            bool persist = SettingsPersistVaultCheck.IsChecked == true;
            if (persist && _multiVault.VaultExists && _multiVault.RequiresPassphrase() && SettingsMasterPassBox.SecurePassword.Length == 0)
            {
                throw new InvalidOperationException("Текущий Vault защищён мастер-паролем. Введите мастер-пароль в поле ниже либо снимите галочку для сохранения только в RAM.");
            }

            SealedSecret sealedKey = CreateTransientSealedSecretWithoutClearing(SettingsApiKeyBox);

            for (int i = _configuredModels.Count - 1; i >= 0; i--)
            {
                var existing = _configuredModels[i];
                if (existing.Provider == _selectedSettingsProvider &&
                    string.Equals(existing.ModelId, item.ModelId, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Dispose();
                    _configuredModels.RemoveAt(i);
                }
            }

            var newEntry = new ConfiguredModelEntry(
                Guid.NewGuid().ToString("N"),
                _selectedSettingsProvider,
                item.ModelId,
                item.DisplayName,
                "MEDIUM",
                persist,
                sealedKey);

            _configuredModels.Add(newEntry);
            _activeModel = newEntry;

            if (persist)
            {
                SavePersistedEntriesFromSettingsPassphrase();
            }

            RefreshConfiguredModelsUi();
            RefreshUnifiedSelectorUi();
            UpdateUsageUi();

            CloseSettingsModal();

            AddStatusCard(
                "НЕЙРОСЕТЬ ПОДКЛЮЧЕНА",
                $"{AiProviderCatalog.GetProviderDisplayName(newEntry.Provider)} • {newEntry.DisplayName} ({newEntry.ModelId}) подключена и выбрана для работы.",
                "#10B981", "#0D201A", "#164E3A", "🤖");
        }
        catch (Exception ex)
        {
            SettingsAddErrorText.Text = LogRedactor.Redact(ex.Message);
            SettingsAddErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnToggleManualVersionClick(object sender, RoutedEventArgs e)
    {
        if (ManualVersionSelectionPanel is not null)
        {
            ManualVersionSelectionPanel.Visibility = ManualVersionSelectionPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void OnActivateConfiguredModelClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConfiguredModelViewItem item })
        {
            _activeModel = _configuredModels.FirstOrDefault(m => m.EntryId == item.EntryId);
            RefreshConfiguredModelsUi();
            RefreshUnifiedSelectorUi();
            UpdateUsageUi();
        }
    }

    private void OnDeleteConfiguredModelClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConfiguredModelViewItem item })
        {
            var entry = _configuredModels.FirstOrDefault(m => m.EntryId == item.EntryId);
            if (entry is null) return;

            bool wasPersisted = entry.SavedInVault;
            entry.Dispose();
            _configuredModels.Remove(entry);
            if (ReferenceEquals(_activeModel, entry))
            {
                _activeModel = _configuredModels.FirstOrDefault();
            }

            if (wasPersisted)
            {
                var remainingPersisted = _configuredModels.Where(m => m.SavedInVault && !m.Secret.IsDisposed).ToList();
                if (remainingPersisted.Count == 0)
                {
                    _multiVault.DeleteVault();
                }
                else if (_multiVault.VaultExists && !_multiVault.RequiresPassphrase())
                {
                    _multiVault.SaveEntries(remainingPersisted);
                }
            }

            RefreshConfiguredModelsUi();
            RefreshUnifiedSelectorUi();
            UpdateUsageUi();
        }
    }

    private void OnWipeAllKeysClick(object sender, RoutedEventArgs e)
    {
        DisposeAllConfiguredModels();
        _multiVault.DeleteVault();
        SettingsApiKeyBox.Clear();
        SettingsMasterPassBox.Clear();
        UnlockVaultPassBox.Clear();

        RefreshConfiguredModelsUi();
        RefreshUnifiedSelectorUi();
        UpdateUsageUi();
        AddStatusCard("ЭКСТРЕННАЯ ОЧИСТКА", "Все API-ключи стёрты из RAM, файл Vault перезаписан и удалён.", "#F59E0B", "#21190C", "#78350F", "🧹");
    }

    private void RefreshConfiguredModelsUi()
    {
        bool vaultNeedsUnlock = false;
        try
        {
            vaultNeedsUnlock = _multiVault.VaultExists && _multiVault.RequiresPassphrase() &&
                               !_configuredModels.Any(m => m.SavedInVault);
        }
        catch { }

        UnlockVaultBanner.Visibility = vaultNeedsUnlock ? Visibility.Visible : Visibility.Collapsed;
        NoConfiguredModelsHint.Visibility = _configuredModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfiguredCountText.Text = $"{_configuredModels.Count} шт.";

        _settingsModelItems.Clear();
        foreach (var entry in _configuredModels)
        {
            bool isActive = ReferenceEquals(entry, _activeModel);
            _settingsModelItems.Add(new ConfiguredModelViewItem(
                entry.EntryId,
                AiProviderCatalog.GetProviderDisplayName(entry.Provider),
                entry.DisplayName,
                $"({entry.ModelId} • {FormatThinkingLabel(entry.ThinkingLevel)})",
                $"{entry.Secret.GetSafeStatusLabel()} • {(entry.SavedInVault ? "Vault DPAPI+AES-GCM" : "Только RAM")}",
                isActive ? BrushFromHex("#0EA5E9") : BrushFromHex("#252C3A"),
                isActive ? "Активна ✓" : "Выбрать"));
        }

        if (_activeModel is not null)
        {
            TopKeyStatusText.Text = $"🔐 {_activeModel.DisplayName} ({_configuredModels.Count})";
            TopKeyStatusText.Foreground = BrushFromHex("#34D399");
            EmptyHeroSubtitle.Text = $"Активна модель {_activeModel.DisplayName}. Задайте задачу, прикрепите фото через Ctrl+V или используйте голос.";
            HeroAddModelBtn.Content = "⚙  Настройки и модели";
        }
        else if (vaultNeedsUnlock)
        {
            TopKeyStatusText.Text = "🔒 Vault ждёт мастер-пароль (⚙)";
            TopKeyStatusText.Foreground = BrushFromHex("#38BDF8");
            EmptyHeroSubtitle.Text = "Найден зашифрованный Vault. Нажмите ⚙ и введите мастер-пароль для разблокировки моделей.";
            HeroAddModelBtn.Content = "🔓  Разблокировать Vault (⚙)";
        }
        else
        {
            TopKeyStatusText.Text = "⚠ Модель не добавлена (⚙)";
            TopKeyStatusText.Foreground = BrushFromHex("#F59E0B");
            EmptyHeroSubtitle.Text = "Добавьте нейросеть в настройках (⚙): выберите компанию, версию модели и API-ключ.";
            HeroAddModelBtn.Content = "⚙  Подключить нейросеть";
        }
    }

    // ==================== Unified Model + Hover Power Submenu + Agent Params + Usage ====================

    private int GetConfiguredMaxSteps() =>
        MaxStepsBox is not null && int.TryParse(MaxStepsBox.Text, out int parsed)
            ? Math.Clamp(parsed, 1, 100)
            : 25;

    private void RefreshUnifiedSelectorUi()
    {
        if (UnifiedModelLabel is null || UnifiedThinkingLabel is null || PopupNoModelsPanel is null)
            return;

        if (_activeModel is not null)
        {
            UnifiedModelLabel.Text = _activeModel.DisplayName;
            UnifiedThinkingLabel.Text = FormatThinkingLabel(_activeModel.ThinkingLevel);
            PopupNoModelsPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            UnifiedModelLabel.Text = "Модель не добавлена";
            UnifiedThinkingLabel.Text = string.Empty;
            PopupNoModelsPanel.Visibility = Visibility.Visible;
        }

        bool cuEnabled = EnableComputerUseCheck?.IsChecked == true;
        int steps = GetConfiguredMaxSteps();

        if (UnifiedScopeBadge is not null)
        {
            if (!cuEnabled)
            {
                UnifiedScopeBadge.Text = "Чат";
            }
            else if (_selectedWindowHandle == IntPtr.Zero)
            {
                UnifiedScopeBadge.Text = $"Экран • {steps} ш.";
            }
            else
            {
                string shortTitle = _selectedWindowTitle.Length > 15
                    ? _selectedWindowTitle[..15] + "…"
                    : _selectedWindowTitle;
                UnifiedScopeBadge.Text = $"{shortTitle} • {steps} ш.";
            }
        }

        if (QuickScreenModeBtn is not null && QuickChatModeBtn is not null)
        {
            QuickScreenModeBtn.Style = (Style)FindResource(cuEnabled ? "PrimaryButton" : "DarkButton");
            QuickChatModeBtn.Style = (Style)FindResource(!cuEnabled ? "PrimaryButton" : "DarkButton");
        }

        // ONLY models explicitly added by the user!
        _popupModelItems.Clear();
        foreach (var entry in _configuredModels)
        {
            bool isSelected = ReferenceEquals(entry, _activeModel);
            _popupModelItems.Add(new ModelMenuItem(
                entry.EntryId,
                entry.DisplayName,
                isSelected ? FormatThinkingLabel(entry.ThinkingLevel) : string.Empty,
                entry.ThinkingLevel,
                isSelected ? BrushFromHex("#212634") : Brushes.Transparent,
                isSelected ? FontWeights.SemiBold : FontWeights.Normal));
        }
    }

    private void RefreshWindowsList()
    {
        var list = new List<WindowScopeItem>();
        bool allSelected = _selectedWindowHandle == IntPtr.Zero;
        list.Add(new WindowScopeItem(
            IntPtr.Zero,
            "Весь виртуальный экран",
            "🖥 Весь виртуальный экран",
            allSelected ? BrushFromHex("#212634") : Brushes.Transparent,
            allSelected ? Visibility.Visible : Visibility.Collapsed));

        bool foundSelected = allSelected;

        var monitors = ScreenCaptureService.GetMonitors();
        if (monitors.Count > 1)
        {
            foreach (var mon in monitors)
            {
                IntPtr monHandle = (IntPtr)(-100 - mon.Index);
                bool isMonSel = _selectedWindowHandle == monHandle;
                if (isMonSel) foundSelected = true;
                list.Add(new WindowScopeItem(
                    monHandle,
                    mon.Name,
                    $"🖥 {mon}",
                    isMonSel ? BrushFromHex("#212634") : Brushes.Transparent,
                    isMonSel ? Visibility.Visible : Visibility.Collapsed));
            }
        }

        foreach (var win in ScreenCaptureService.GetVisibleWindows(_mainHwnd))
        {
            bool isSel = win.Handle == _selectedWindowHandle;
            if (isSel) foundSelected = true;
            list.Add(new WindowScopeItem(
                win.Handle,
                win.Title,
                $"🪟 {win.Title}",
                isSel ? BrushFromHex("#212634") : Brushes.Transparent,
                isSel ? Visibility.Visible : Visibility.Collapsed));
        }

        if (!foundSelected)
        {
            _selectedWindowHandle = IntPtr.Zero;
            _selectedWindowTitle = "Весь виртуальный экран";
        }

        _scopeItems.Clear();
        foreach (var item in list) _scopeItems.Add(item);
        RefreshUnifiedSelectorUi();
    }

    private void CloseAllPopups()
    {
        _submenuSwitchTimer.Stop();
        _pendingSubmenuSwitch = null;
        ThinkingSubmenuPopup.IsOpen = false;
        UnifiedSelectorPopup.IsOpen = false;
        UsagePopup.IsOpen = false;
        AgentParamsPanel.Visibility = Visibility.Collapsed;
    }

    private void OnUnifiedModelButtonClick(object sender, RoutedEventArgs e)
    {
        bool openNext = !UnifiedSelectorPopup.IsOpen;
        CloseAllPopups();
        if (openNext)
        {
            UnifiedSelectorPopup.IsOpen = true;
        }
    }

    private void OnAgentParamsButtonClick(object sender, RoutedEventArgs e)
    {
        bool openNext = AgentParamsPanel.Visibility != Visibility.Visible;
        CloseAllPopups();
        if (openNext)
        {
            RefreshWindowsList();
            AgentParamsPanel.Visibility = Visibility.Visible;
        }
    }

    private void OnCloseAgentParamsClick(object sender, RoutedEventArgs e) =>
        AgentParamsPanel.Visibility = Visibility.Collapsed;

    private void OnQuickScreenModeClick(object sender, RoutedEventArgs e)
    {
        EnableComputerUseCheck.IsChecked = true;
        RefreshUnifiedSelectorUi();
    }

    private void OnQuickChatModeClick(object sender, RoutedEventArgs e)
    {
        EnableComputerUseCheck.IsChecked = false;
        RefreshUnifiedSelectorUi();
    }

    private void OnStepDecrementClick(object sender, RoutedEventArgs e)
    {
        int next = Math.Max(1, GetConfiguredMaxSteps() - 1);
        MaxStepsBox.Text = next.ToString();
    }

    private void OnStepIncrementClick(object sender, RoutedEventArgs e)
    {
        int next = Math.Min(100, GetConfiguredMaxSteps() + 1);
        MaxStepsBox.Text = next.ToString();
    }

    private void OnStepPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string raw } && int.TryParse(raw, out int val))
        {
            MaxStepsBox.Text = Math.Clamp(val, 1, 100).ToString();
        }
    }

    private void OnMaxStepsTextChanged(object sender, TextChangedEventArgs e) =>
        RefreshUnifiedSelectorUi();

    private void OnPopupGoToAddModelClick(object sender, RoutedEventArgs e)
    {
        CloseAllPopups();
        OpenSettingsModal("Models");
    }

    private void OnModelRowMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button { Tag: ModelMenuItem item } btn)
        {
            SimulateHoverModelRow(btn, item);
        }
    }

    internal void SimulateHoverModelRow(Button btn, ModelMenuItem item)
    {
        _submenuSwitchTimer.Stop();
        _pendingSubmenuSwitch = null;
        _hoveredModelItem = item;
        UpdateThinkingSubmenuChecks(item.ThinkingLevel);

        // Reset IsOpen when PlacementTarget changes so WPF repositions flush next to THIS hovered row & arrow
        if (!ReferenceEquals(ThinkingSubmenuPopup.PlacementTarget, btn))
        {
            ThinkingSubmenuPopup.IsOpen = false;
            ThinkingSubmenuPopup.PlacementTarget = btn;
        }
        ThinkingSubmenuPopup.IsOpen = true;
    }

    private void OnSubmenuMouseEnter(object sender, MouseEventArgs e)
    {
        _submenuSwitchTimer.Stop();
        _pendingSubmenuSwitch = null;
    }

    private void OnModelRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModelMenuItem item })
        {
            var entry = _configuredModels.FirstOrDefault(m => m.EntryId == item.EntryId);
            if (entry is not null)
            {
                _activeModel = entry;
                RefreshConfiguredModelsUi();
                RefreshUnifiedSelectorUi();
                UpdateUsageUi();
            }
            CloseAllPopups();
        }
    }

    private void OnThinkingOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string level })
        {
            if (_hoveredModelItem is not null)
            {
                var entry = _configuredModels.FirstOrDefault(m => m.EntryId == _hoveredModelItem.EntryId);
                if (entry is not null)
                {
                    entry.ThinkingLevel = level;
                    _activeModel = entry;
                    PersistThinkingIfDpapiOnly(entry);
                }
            }
            else if (_activeModel is not null)
            {
                _activeModel.ThinkingLevel = level;
                PersistThinkingIfDpapiOnly(_activeModel);
            }

            RefreshConfiguredModelsUi();
            RefreshUnifiedSelectorUi();
            UpdateUsageUi();
            CloseAllPopups();
        }
    }

    private void PersistThinkingIfDpapiOnly(ConfiguredModelEntry entry)
    {
        try
        {
            if (entry.SavedInVault && _multiVault.VaultExists && !_multiVault.RequiresPassphrase())
            {
                var persisted = _configuredModels.Where(m => m.SavedInVault && !m.Secret.IsDisposed).ToList();
                if (persisted.Count > 0) _multiVault.SaveEntries(persisted);
            }
        }
        catch { }
    }

    private void UpdateThinkingSubmenuChecks(string thinkingLevel)
    {
        CheckLow.Visibility = thinkingLevel == "LOW" ? Visibility.Visible : Visibility.Collapsed;
        CheckMedium.Visibility = thinkingLevel == "MEDIUM" ? Visibility.Visible : Visibility.Collapsed;
        CheckHigh.Visibility = thinkingLevel == "HIGH" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNonSubmenuRowMouseEnter(object sender, MouseEventArgs e)
    {
        if (!ThinkingSubmenuPopup.IsOpen)
        {
            return;
        }

        _pendingSubmenuSwitch = () => ThinkingSubmenuPopup.IsOpen = false;
        _submenuSwitchTimer.Stop();
        _submenuSwitchTimer.Start();
    }

    private void OnRefreshWindowsClick(object sender, RoutedEventArgs e) => RefreshWindowsList();

    private void OnToolToggleChanged(object sender, RoutedEventArgs e) => RefreshUnifiedSelectorUi();

    private void OnSelectWindowScopeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WindowScopeItem item })
        {
            _selectedWindowHandle = item.Handle;
            _selectedWindowTitle = item.Title;
            if (EnableComputerUseCheck.IsChecked != true)
            {
                EnableComputerUseCheck.IsChecked = true;
            }
            RefreshWindowsList();
            CloseAllPopups();
        }
    }

    private void OnViewUsageRowClick(object sender, RoutedEventArgs e)
    {
        CloseAllPopups();
        UpdateUsageUi();
        UsagePopup.IsOpen = true;
    }

    private void OnContextRingClick(object sender, MouseButtonEventArgs e)
    {
        bool openNext = !UsagePopup.IsOpen;
        CloseAllPopups();
        if (openNext)
        {
            UpdateUsageUi();
            UsagePopup.IsOpen = true;
        }
    }

    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseAllPopups();
            e.Handled = true;
        }
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (!IsDescendantOf(source, UnifiedModelButton) &&
            !IsDescendantOf(source, ContextRing) &&
            !IsDescendantOf(source, AgentParamsButton) &&
            !IsDescendantOf(source, AgentParamsPanel))
        {
            CloseAllPopups();
        }
    }

    private int GetActiveContextWindowLimit()
    {
        if (_activeModel is null) return 1_048_576;
        return AiProviderCatalog.ResolveContextLimit(_activeModel.Provider, _activeModel.ModelId);
    }

    private void UpdateUsageUi()
    {
        int contextLimit = GetActiveContextWindowLimit();
        int windowTotal = _lastUsage?.TotalTokens ?? 0;
        int prompt = _lastUsage?.PromptTokens ?? 0;
        int candidates = _lastUsage?.CandidatesTokens ?? 0;
        int thoughts = _lastUsage?.ThoughtsTokens ?? 0;
        int cached = _lastUsage?.CachedTokens ?? 0;

        ContextRing.UpdateUsage(windowTotal, contextLimit);

        double pct = Math.Clamp((double)windowTotal * 100.0 / Math.Max(1, contextLimit), 0.0, 100.0);
        UsageProgressBar.Value = pct;
        UsagePercentText.Text = $"{pct:0.#}%";
        UsagePromptText.Text = prompt.ToString("N0");
        UsageOutputText.Text = candidates.ToString("N0");
        UsageThoughtsText.Text = thoughts.ToString("N0");
        UsageCachedText.Text = cached.ToString("N0");
        UsageWindowTotalText.Text = $"{windowTotal:N0} / {contextLimit:N0}";
        UsageSessionTotalText.Text = $"{_sessionCumulativeTokens:N0} токенов";
    }

    private static string FormatThinkingLabel(string level) => level switch
    {
        "LOW" => "Low",
        "HIGH" => "High",
        _ => "Medium"
    };

    // ==================== Attachments & Ctrl+V Image Paste ====================

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            CloseAllPopups();
            return;
        }

        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            // Never hijack clipboard when Settings modal is open or user focuses any PasswordBox
            if (SettingsOverlay.Visibility == Visibility.Visible || Keyboard.FocusedElement is PasswordBox)
                return;

            if (TryAttachFromClipboard())
            {
                e.Handled = true;
                PromptBox.Focus();
            }
        }
    }

    private bool TryAttachFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                var list = new List<string>(files.Count);
                foreach (string? f in files)
                {
                    if (!string.IsNullOrWhiteSpace(f)) list.Add(f);
                }
                if (list.Count > 0)
                {
                    AddAttachmentsFromPaths(list);
                    return true;
                }
            }

            if (Clipboard.ContainsImage())
            {
                BitmapSource? bmp = Clipboard.GetImage();
                if (bmp is not null)
                {
                    byte[]? pngBytes = TryEncodeBitmapSourceToPng(bmp);
                    if (pngBytes is { Length: > 0 })
                    {
                        string fileName = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                        _attachments.Add(new AttachmentViewItem(new AttachmentInput(fileName, "image/png", pngBytes, false)));
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddStatusCard("БУФЕР ОБМЕНА", LogRedactor.Redact(ex.Message), "#F59E0B", "#21190C", "#78350F", "⚠");
        }

        return false;
    }

    private static byte[]? TryEncodeBitmapSourceToPng(BitmapSource source)
    {
        BitmapSource normalized = source;
        try
        {
            if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Bgr32)
            {
                var conv = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                conv.Freeze();
                normalized = conv;
            }
        }
        catch { }

        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(normalized));
        encoder.Save(ms);
        return ms.ToArray();
    }

    private void OnAddAttachmentClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите изображения, видео, аудио, PDF или текстовые/кодовые файлы",
            Multiselect = true,
            Filter = "Все поддерживаемые|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.mp4;*.webm;*.mov;*.avi;*.mkv;*.wav;*.mp3;*.ogg;*.pdf;*.txt;*.md;*.csv;*.json;*.xml;*.yaml;*.cs;*.py;*.js;*.ts;*.html;*.css;*.sql;*.svg|Изображения|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|Видео|*.mp4;*.webm;*.mov;*.avi;*.mkv|Аудио|*.wav;*.mp3;*.ogg;*.flac;*.aac|Документы и код|*.pdf;*.txt;*.md;*.csv;*.json;*.xml;*.yaml;*.cs;*.py;*.js;*.ts;*.html;*.css;*.sql"
        };

        if (dlg.ShowDialog() == true)
        {
            AddAttachmentsFromPaths(dlg.FileNames);
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddAttachmentsFromPaths(files);
        }
    }

    private void AddAttachmentsFromPaths(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var info = new FileInfo(path);
                if (info.Length > 250L * 1024 * 1024)
                {
                    AddStatusCard("ВЛОЖЕНИЕ ОТКЛОНЕНО", $"Файл {info.Name} превышает 250 МБ.", "#F59E0B", "#21190C", "#78350F", "⚠");
                    continue;
                }

                string ext = info.Extension.ToLowerInvariant();
                bool isText = TextExtensions.Contains(ext);
                string mime = MimeTypes.TryGetValue(ext, out string? m)
                    ? m
                    : (isText ? "text/plain" : "application/octet-stream");

                byte[] bytes = File.ReadAllBytes(path);
                _attachments.Add(new AttachmentViewItem(new AttachmentInput(info.Name, mime, bytes, isText)));
            }
            catch (Exception ex)
            {
                AddStatusCard("ОШИБКА ВЛОЖЕНИЯ", LogRedactor.Redact(ex.Message), "#EF4444", "#261215", "#7F1D1D", "⛔");
            }
        }
    }

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AttachmentViewItem item })
        {
            _attachments.Remove(item);
        }
    }

    // ==================== Chat Controls & Sandbox ====================

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        _isSidebarCollapsed = !_isSidebarCollapsed;
        double targetWidth = _isSidebarCollapsed ? 0 : 260;

        if (!_isSidebarCollapsed)
        {
            SidebarBorder.Visibility = Visibility.Visible;
            SidebarExpandBtn.Visibility = Visibility.Collapsed;
        }

        var anim = new DoubleAnimation(SidebarBorder.ActualWidth, targetWidth, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        if (_isSidebarCollapsed)
        {
            anim.Completed += (_, _) =>
            {
                if (_isSidebarCollapsed)
                {
                    SidebarBorder.Visibility = Visibility.Collapsed;
                    SidebarExpandBtn.Visibility = Visibility.Visible;
                }
            };
        }

        SidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    private void OnNewChatSessionClick(object sender, RoutedEventArgs e) =>
        CreateNewChatSession();

    private void OnClearChatClick(object sender, RoutedEventArgs e) =>
        CreateNewChatSession();

    private void CreateNewChatSession(string? title = null)
    {
        SaveActiveSessionState();

        _history.Clear();
        _chatEntries.Clear();
        _lastUsage = null;
        _sessionCumulativeTokens = 0;
        UpdateUsageUi();
        CloseAllPopups();

        string sessionTitle = title ?? $"Чат {_chatSessions.Count + 1}";
        var newSession = new ChatSessionItem
        {
            Title = sessionTitle,
            WorkingDirectory = _sandboxRoot,
            IsActive = true
        };

        foreach (var s in _chatSessions) s.IsActive = false;
        _chatSessions.Insert(0, newSession);
        _activeSession = newSession;

        if (SidebarChatCountText is not null)
            SidebarChatCountText.Text = _chatSessions.Count.ToString();
    }

    private void SaveActiveSessionState()
    {
        if (_activeSession is null) return;
        _activeSession.Entries = new List<ChatEntry>(_chatEntries);
        _activeSession.History = new List<JsonNode>(_history);
        _activeSession.LastUsage = _lastUsage;
        _activeSession.CumulativeTokens = _sessionCumulativeTokens;
        _activeSession.WorkingDirectory = _sandboxRoot;
        _activeSession.NotifyAll();
    }

    private void OnChatSessionCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatSessionItem session })
        {
            SwitchToChatSession(session);
        }
    }

    private void SwitchToChatSession(ChatSessionItem session)
    {
        if (_activeSession == session) return;

        SaveActiveSessionState();

        foreach (var s in _chatSessions) s.IsActive = (s == session);
        _activeSession = session;

        _chatEntries.Clear();
        foreach (var entry in session.Entries)
            _chatEntries.Add(entry);

        _history.Clear();
        _history.AddRange(session.History);

        _lastUsage = session.LastUsage;
        _sessionCumulativeTokens = session.CumulativeTokens;
        UpdateUsageUi();
        CloseAllPopups();
    }

    private void OnDeleteChatSessionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ChatSessionItem session })
        {
            DeleteChatSession(session);
        }
    }

    private void DeleteChatSession(ChatSessionItem session)
    {
        bool wasActive = session.IsActive;
        _chatSessions.Remove(session);

        if (_chatSessions.Count == 0)
        {
            CreateNewChatSession("Диалог 1");
            return;
        }

        if (wasActive)
        {
            SwitchToChatSession(_chatSessions[0]);
        }

        if (SidebarChatCountText is not null)
            SidebarChatCountText.Text = _chatSessions.Count.ToString();
    }

    private void UpdateSidebarDirectoryUi()
    {
        if (SidebarDirectoryText is null) return;
        string dirName = Path.GetFileName(_sandboxRoot);
        if (string.IsNullOrWhiteSpace(dirName))
            dirName = _sandboxRoot;
        SidebarDirectoryText.Text = dirName;
        SidebarDirectoryText.ToolTip = $"Подключенная папка песочницы:\n{_sandboxRoot}";
    }

    private void OnExportReportClick(object sender, RoutedEventArgs e) => ExportSessionReport();

    internal string ExportSessionReport()
    {
        string reportsDir = Path.Combine(_sandboxRoot, "Reports");
        Directory.CreateDirectory(reportsDir);

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string reportFile = Path.Combine(reportsDir, $"Aegis_Session_Report_{timestamp}.md");

        var sb = new StringBuilder();
        sb.AppendLine("# Аудиторский отчёт сессии Aegis Computer Use");
        sb.AppendLine();
        sb.AppendLine($"- **Дата и время**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Активная модель**: {_activeModel?.DisplayName ?? "Не выбрана"} ({_activeModel?.ModelId ?? "нет"})");
        sb.AppendLine($"- **Провайдер**: {_activeModel?.Provider.ToString() ?? "нет"}");
        sb.AppendLine($"- **Область захвата**: {_selectedWindowTitle}");
        sb.AppendLine($"- **Использовано токенов**: {_sessionCumulativeTokens:N0}");
        sb.AppendLine($"- **Создано файлов в песочнице**: {_createdFiles.Count}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Журнал диалога и действий");
        sb.AppendLine();

        if (_chatEntries.Count == 0)
        {
            sb.AppendLine("*В текущей сессии диалог пуст.*");
        }
        else
        {
            foreach (var entry in _chatEntries)
            {
                sb.AppendLine($"### [{entry.TimeText}] {entry.IconBadge} {entry.Header}");
                sb.AppendLine();
                if (entry.RichVisual is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
                {
                    sb.AppendLine(tb.Text);
                    sb.AppendLine();
                }
            }
        }

        if (_createdFiles.Count > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Созданные файлы в SafeFileSandbox");
            sb.AppendLine();
            foreach (var f in _createdFiles)
            {
                sb.AppendLine($"- **{f.DisplayLabel}** — `{f.FullPath}`");
            }
            sb.AppendLine();
        }

        File.WriteAllText(reportFile, sb.ToString(), Encoding.UTF8);

        AddStatusCard(
            "ОТЧЁТ ЭКСПОРТИРОВАН",
            $"Аудиторский отчёт сессии успешно сохранён в файл:\n`{reportFile}`",
            "#10B981", "#092218", "#064E3B", "📄");

        return reportFile;
    }

    private void OnQuickPromptClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string samplePrompt })
        {
            PromptBox.Text = samplePrompt;
            PromptBox.CaretIndex = PromptBox.Text.Length;
            PromptBox.Focus();
        }
    }

    private void OnPickSandboxClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Выберите безопасную папку для создаваемых файлов",
            InitialDirectory = Directory.Exists(_sandboxRoot) ? _sandboxRoot : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                _ = new SafeFileSandbox(dlg.FolderName);
                _sandboxRoot = dlg.FolderName;
                SandboxPathText.Text = _sandboxRoot;
                UpdateSidebarDirectoryUi();
                if (_activeSession is not null)
                {
                    _activeSession.WorkingDirectory = _sandboxRoot;
                }
            }
            catch (Exception ex)
            {
                AddStatusCard("ОШИБКА ПЕСОЧНИЦЫ", LogRedactor.Redact(ex.Message), "#EF4444", "#261215", "#7F1D1D", "⛔");
            }
        }
    }

    private void OnOpenSandboxClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string fullRoot = Path.GetFullPath(_sandboxRoot);
            Directory.CreateDirectory(fullRoot);
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(fullRoot);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AddStatusCard("ОШИБКА", LogRedactor.Redact(ex.Message), "#EF4444", "#261215", "#7F1D1D", "⛔");
        }
    }

    private void OnOpenCreatedFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CreatedFileViewItem item } && File.Exists(item.FullPath))
        {
            try
            {
                string fullRoot = Path.GetFullPath(_sandboxRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullFile = Path.GetFullPath(item.FullPath);
                if (!fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    return;

                string? folder = Path.GetDirectoryName(fullFile);
                if (folder is not null && Directory.Exists(folder))
                {
                    var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                    psi.ArgumentList.Add(folder);
                    Process.Start(psi);
                }
            }
            catch { }
        }
    }

    // ==================== Voice Recognition & Prompt Run ====================

    private async void OnVoiceToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_voiceCapture.IsRecording)
        {
            try
            {
                if (_activeModel is null || _activeModel.Secret.IsDisposed)
                {
                    OpenSettingsModal("Models");
                    AddStatusCard("ГОЛОС", "Сначала добавьте модель с API-ключом в Настройках (⚙) для распознавания речи.", "#F59E0B", "#21190C", "#78350F", "🎙");
                    return;
                }

                _voiceCapture.StartRecording();
                VoiceButton.Content = "⏹";
                VoiceButton.Foreground = BrushFromHex("#EF4444");
                VoiceButton.ToolTip = "Остановить запись и распознать речь";
            }
            catch (Exception ex)
            {
                AddStatusCard("ОШИБКА МИКРОФОНА", LogRedactor.Redact(ex.Message), "#EF4444", "#261215", "#7F1D1D", "⛔");
            }
            return;
        }

        VoiceButton.IsEnabled = false;
        VoiceButton.Content = "⏳";
        byte[] wav = [];
        try
        {
            wav = _voiceCapture.StopAndBuildWav();
            VoiceButton.Foreground = BrushFromHex("#CBD5E1");

            if (wav.Length < 100)
                return;

            string? transcript = await TranscribeWithConfiguredProviderAsync(wav);
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                PromptBox.Text = string.IsNullOrWhiteSpace(PromptBox.Text)
                    ? transcript
                    : PromptBox.Text + " " + transcript;
                PromptBox.CaretIndex = PromptBox.Text.Length;
                PromptBox.Focus();

                if (AutoSendVoiceCheck.IsChecked == true)
                {
                    await ExecutePromptAsync();
                }
            }
            else if (transcript is not null)
            {
                AddStatusCard("ГОЛОС", "Речь не распознана (тишина).", "#8B94A6", "#14171F", "#262C38", "🎙");
            }
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 404)
        {
            string activeId = _activeModel?.ModelId ?? "не выбрана";
            AddStatusCard(
                "МОДЕЛЬ НЕ НАЙДЕНА (404)",
                $"{LogRedactor.Redact(ex.Message)}\n\n💡 Модель «{activeId}» недоступна для голоса. Откройте «⚙ Настройки» → вставьте API-ключ → нажмите «🔄 По ключу», чтобы выбрать точную модель (например: gemini-2.5-flash).",
                "#EF4444", "#261215", "#7F1D1D", "⛔");
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 429)
        {
            string activeId = _activeModel?.ModelId ?? "не выбрана";
            AddStatusCard("КВОТА API ИСЧЕРПАНА (429)", FormatRateLimitMessage(activeId, ex), "#F59E0B", "#21190C", "#78350F", "⏳");
        }
        catch (GeminiApiException ex) when (ex.StatusCode is 401 or 403)
        {
            string activeId = _activeModel?.ModelId ?? "не выбрана";
            AddStatusCard(
                $"ОШИБКА ДОСТУПА ({ex.StatusCode})",
                FormatForbiddenMessage(activeId, ex),
                "#EF4444", "#261215", "#7F1D1D", "⛔");
        }
        catch (GeminiApiException ex) when (ex.StatusCode is 500 or 502 or 503 or 504 or 529)
        {
            string activeId = _activeModel?.ModelId ?? "не выбрана";
            AddStatusCard($"СЕРВЕР ПЕРЕГРУЖЕН ({ex.StatusCode})", FormatTransientOverloadMessage(activeId, ex), "#F59E0B", "#21190C", "#78350F", "⏳");
        }
        catch (Exception ex)
        {
            AddStatusCard("ОШИБКА ГОЛОСА", LogRedactor.Redact(ex.Message), "#EF4444", "#261215", "#7F1D1D", "⛔");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wav);
            VoiceButton.Content = "🎙";
            VoiceButton.Foreground = BrushFromHex("#CBD5E1");
            VoiceButton.ToolTip = "Голосовая команда (запись в RAM)";
            VoiceButton.IsEnabled = true;
        }
    }

    private async Task<string?> TranscribeWithConfiguredProviderAsync(byte[] wav)
    {
        if (_activeModel is { Provider: AiProviderKind.GoogleGemini, Secret.IsDisposed: false } gemModel)
        {
            try
            {
                return await _apiClient.TranscribeAudioAsync(gemModel.Secret, gemModel.ModelId, wav, CancellationToken.None);
            }
            catch (GeminiApiException ex) when (ex.StatusCode is 500 or 502 or 503 or 504 or 529 &&
                                                AutoFallbackOnOverloadCheck.IsChecked == true &&
                                                !string.Equals(gemModel.ModelId, "gemini-2.5-flash", StringComparison.OrdinalIgnoreCase))
            {
                AddStatusCard("АВТО-РЕЗЕРВ ГОЛОСА", $"Модель «{gemModel.ModelId}» перегружена (HTTP {ex.StatusCode}) — распознаю речь через gemini-2.5-flash.", "#38BDF8", "#0F1B29", "#1E3A5F", "🔄");
                return await _apiClient.TranscribeAudioAsync(gemModel.Secret, "gemini-2.5-flash", wav, CancellationToken.None);
            }
        }
        if (_activeModel is { Provider: AiProviderKind.OpenAI, Secret.IsDisposed: false })
        {
            return await _multiProvider.TranscribeAudioOpenAiAsync(_activeModel.Secret, wav, CancellationToken.None);
        }

        // Anthropic Messages API has no audio-WAV transcription endpoint; check if user also configured Gemini/OpenAI
        var sttModel = _configuredModels.FirstOrDefault(m =>
            !m.Secret.IsDisposed && m.Provider is AiProviderKind.GoogleGemini or AiProviderKind.OpenAI);
        if (sttModel is not null)
        {
            return sttModel.Provider == AiProviderKind.GoogleGemini
                ? await _apiClient.TranscribeAudioAsync(sttModel.Secret, sttModel.ModelId, wav, CancellationToken.None)
                : await _multiProvider.TranscribeAudioOpenAiAsync(sttModel.Secret, wav, CancellationToken.None);
        }

        AddStatusCard("ГОЛОС", "Anthropic API не поддерживает прямое распознавание WAV. Добавьте в ⚙ также модель Google Gemini или OpenAI для голосового ввода.", "#F59E0B", "#21190C", "#78350F", "🎙");
        return null;
    }

    private void OnPromptTextChanged(object sender, TextChangedEventArgs e)
    {
        PromptPlaceholder.Visibility = string.IsNullOrEmpty(PromptBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnPromptPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            e.Handled = true;
            ToggleLiveVoiceMode();
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await ExecutePromptAsync();
        }
    }

    private void OnLiveVoiceToggleClick(object sender, RoutedEventArgs e)
    {
        ToggleLiveVoiceMode();
    }

    private void OnLiveVoiceCheckChanged(object sender, RoutedEventArgs e)
    {
        bool isChecked = LiveVoiceModeCheck.IsChecked == true;
        if (isChecked != _isLiveVoiceActive)
        {
            SetLiveVoiceMode(isChecked);
        }
    }

    private void ToggleLiveVoiceMode()
    {
        SetLiveVoiceMode(!_isLiveVoiceActive);
    }

    private void SetLiveVoiceMode(bool active)
    {
        if (_isLiveVoiceActive == active) return;
        _isLiveVoiceActive = active;

        if (LiveVoiceModeCheck is not null && LiveVoiceModeCheck.IsChecked != active)
        {
            LiveVoiceModeCheck.IsChecked = active;
        }

        if (active)
        {
            if (_activeModel is null || _activeModel.Secret.IsDisposed)
            {
                _isLiveVoiceActive = false;
                if (LiveVoiceModeCheck is not null) LiveVoiceModeCheck.IsChecked = false;
                OpenSettingsModal("Models");
                AddStatusCard("ГОЛОС В РЕАЛЬНОМ ВРЕМЕНИ", "Сначала настройте модель и API-ключ в Настройках (⚙) для голосового управления.", "#F59E0B", "#21190C", "#78350F", "🎙");
                UpdateLiveVoiceUi(false, false);
                return;
            }

            try
            {
                _voiceCapture.ContinuousMode = true;
                _voiceCapture.StartRecording();
                UpdateLiveVoiceUi(isListening: true, isSpeaking: false);
                AddStatusCard("LIVE ГОЛОС ВКЛЮЧЕН", "Режим непрерывного голосового управления активен. Говорите команды свободно (автопауза 1.2 с) — агент сам слушает и выполняет действия на экране. Для переключения: Alt+V или Ctrl+Alt+V.", "#10B981", "#092218", "#064E3B", "🎙");
            }
            catch (Exception ex)
            {
                _isLiveVoiceActive = false;
                if (LiveVoiceModeCheck is not null) LiveVoiceModeCheck.IsChecked = false;
                AddStatusCard("ОШИБКА МИКРОФОНА", LogRedactor.Redact(ex.Message), "#EF4444", "#261215", "#7F1D1D", "⛔");
                UpdateLiveVoiceUi(false, false);
            }
        }
        else
        {
            try
            {
                if (_voiceCapture.IsRecording)
                {
                    _voiceCapture.StopRecording();
                }
            }
            catch { }

            _voiceCapture.ContinuousMode = false;
            UpdateLiveVoiceUi(isListening: false, isSpeaking: false);
            AddStatusCard("LIVE ГОЛОС ПРИОСТАНОВЛЕН", "Режим непрерывного прослушивания отключен.", "#8B94A6", "#14171F", "#262C38", "⏸");
        }

        SaveAppSettings();
    }

    private void UpdateLiveVoiceUi(bool isListening, bool isSpeaking)
    {
        if (LiveVoiceToggleBtn is null) return;

        if (!isListening)
        {
            LiveVoiceToggleBtn.Content = "🔴 Live Голос";
            LiveVoiceToggleBtn.Foreground = BrushFromHex("#94A3B8");
            LiveVoiceToggleBtn.Background = BrushFromHex("#1E2430");
            LiveVoiceToggleBtn.ToolTip = "Включить непрерывное голосовое управление в реальном времени (Alt+V)";
            return;
        }

        if (isSpeaking)
        {
            LiveVoiceToggleBtn.Content = "🗣 Говорите...";
            LiveVoiceToggleBtn.Foreground = BrushFromHex("#38BDF8");
            LiveVoiceToggleBtn.Background = BrushFromHex("#0C2A44");
            LiveVoiceToggleBtn.ToolTip = "Обнаружена речь... Завершите фразу для выполнения команды";
        }
        else if (_isLiveVoiceTranscribing)
        {
            LiveVoiceToggleBtn.Content = "⏳ Думаю...";
            LiveVoiceToggleBtn.Foreground = BrushFromHex("#F59E0B");
            LiveVoiceToggleBtn.Background = BrushFromHex("#2C220E");
            LiveVoiceToggleBtn.ToolTip = "Распознавание речи и выполнение команды агентом...";
        }
        else
        {
            LiveVoiceToggleBtn.Content = "🟢 В эфире";
            LiveVoiceToggleBtn.Foreground = BrushFromHex("#10B981");
            LiveVoiceToggleBtn.Background = BrushFromHex("#063D2E");
            LiveVoiceToggleBtn.ToolTip = "Live Голос активен (слушает микрофон). Нажмите или Alt+V для паузы";
        }
    }

    private void OnLiveVoiceSpeechActivityChanged(bool isSpeaking)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_isLiveVoiceActive) return;
            UpdateLiveVoiceUi(isListening: true, isSpeaking: isSpeaking);
        });
    }

    private async void OnLiveVoiceUtteranceCompleted(byte[] wavData)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            if (!_isLiveVoiceActive || wavData.Length < 100) return;

            if (_isLiveVoiceTranscribing || (_runCts is not null && !_runCts.IsCancellationRequested))
                return;

            _isLiveVoiceTranscribing = true;
            UpdateLiveVoiceUi(isListening: true, isSpeaking: false);

            try
            {
                string? transcript = await TranscribeWithConfiguredProviderAsync(wavData);
                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    PromptBox.Text = transcript;
                    PromptBox.CaretIndex = PromptBox.Text.Length;
                    await ExecutePromptAsync();
                }
            }
            catch (Exception ex)
            {
                AddStatusCard("ОШИБКА LIVE РАСПОЗНАВАНИЯ", LogRedactor.Redact(ex.Message), "#EF4444", "#261215", "#7F1D1D", "⛔");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wavData);
                _isLiveVoiceTranscribing = false;
                if (_isLiveVoiceActive)
                {
                    UpdateLiveVoiceUi(isListening: true, isSpeaking: false);
                }
            }
        });
    }

    private void OnProxyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SettingsProxyBox is null) return;
        string proxyText = SettingsProxyBox.Text.Trim();
        GeminiApiClient.GlobalProxyUrl = string.IsNullOrWhiteSpace(proxyText) ? null : proxyText;
        SaveAppSettings();
    }

    private void OnPresetProxyClashClick(object sender, RoutedEventArgs e)
    {
        SettingsProxyBox.Text = "http://127.0.0.1:7890";
    }

    private void OnPresetProxyV2RayClick(object sender, RoutedEventArgs e)
    {
        SettingsProxyBox.Text = "http://127.0.0.1:10809";
    }

    private void OnPresetProxySystemClick(object sender, RoutedEventArgs e)
    {
        SettingsProxyBox.Text = "system";
    }

    private void OnPresetProxyClearClick(object sender, RoutedEventArgs e)
    {
        SettingsProxyBox.Text = string.Empty;
    }

    private static string GetAppSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AegisComputerUse", "settings.json");

    private void LoadAppSettings()
    {
        try
        {
            string path = GetAppSettingsPath();
            if (!File.Exists(path)) return;
            string json = File.ReadAllText(path);
            var node = JsonNode.Parse(json)?.AsObject();
            if (node is null) return;

            if (node["proxy"] is JsonNode proxyNode)
            {
                string? proxy = proxyNode.GetValue<string?>();
                if (!string.IsNullOrWhiteSpace(proxy))
                {
                    SettingsProxyBox.Text = proxy;
                    GeminiApiClient.GlobalProxyUrl = proxy;
                }
            }

            if (node["autoFallback"] is JsonNode fallbackNode && fallbackNode.GetValue<bool>() is bool fb)
            {
                AutoFallbackOnOverloadCheck.IsChecked = fb;
            }

            if (node["autoSendVoice"] is JsonNode autoSendNode && autoSendNode.GetValue<bool>() is bool asv)
            {
                AutoSendVoiceCheck.IsChecked = asv;
            }

            if (node["liveVoice"] is JsonNode liveVoiceNode && liveVoiceNode.GetValue<bool>() is bool lv)
            {
                LiveVoiceModeCheck.IsChecked = lv;
                if (lv) SetLiveVoiceMode(true);
            }

            if (node["realTimeScreen"] is JsonNode rtsNode && rtsNode.GetValue<bool>() is bool rts)
            {
                RealTimeScreenWatchCheck.IsChecked = rts;
            }

            if (node["stepsPerPrompt"] is JsonNode stepsNode && stepsNode.GetValue<int>() is int steps && steps >= 1 && steps <= 30)
            {
                MaxStepsBox.Text = steps.ToString();
            }

            if (node["excludeCapture"] is JsonNode excNode && excNode.GetValue<bool>() is bool exc)
            {
                ExcludeCaptureCheck.IsChecked = exc;
                ApplyCaptureExclusion();
            }
        }
        catch
        {
            // Ignore settings load errors gracefully
        }
    }

    private void SaveAppSettings()
    {
        try
        {
            string path = GetAppSettingsPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            int steps = int.TryParse(MaxStepsBox?.Text, out int s) && s >= 1 && s <= 30 ? s : 25;

            var node = new JsonObject
            {
                ["proxy"] = SettingsProxyBox?.Text.Trim() ?? string.Empty,
                ["autoFallback"] = AutoFallbackOnOverloadCheck?.IsChecked ?? true,
                ["autoSendVoice"] = AutoSendVoiceCheck?.IsChecked ?? true,
                ["liveVoice"] = _isLiveVoiceActive,
                ["realTimeScreen"] = RealTimeScreenWatchCheck?.IsChecked ?? true,
                ["excludeCapture"] = ExcludeCaptureCheck?.IsChecked ?? false,
                ["stepsPerPrompt"] = steps
            };

            File.WriteAllText(path, node.ToJsonString());
        }
        catch
        {
            // Ignore settings save errors gracefully
        }
    }

    private async void OnRunClick(object sender, RoutedEventArgs e) => await ExecutePromptAsync();

    private void OnStopClick(object sender, RoutedEventArgs e) =>
        TriggerKillSwitch("Остановлено пользователем.");

    private void TriggerKillSwitch(string reason)
    {
        if (_runCts is { IsCancellationRequested: false })
        {
            _runCts.Cancel();
            AddStatusCard("KILL-SWITCH", reason, "#EF4444", "#261215", "#7F1D1D", "⛔");
        }
    }

    internal async Task ExecutePromptAsync(FailedPromptRetryData? retryData = null)
    {
        if (_runCts is not null) return;
        CloseAllPopups();

        if (_activeModel is null || _activeModel.Secret.IsDisposed)
        {
            OpenSettingsModal("Models");
            AddStatusCard("ТРЕБУЕТСЯ МОДЕЛЬ", "Сначала выберите компанию, модель и добавьте API-ключ в Настройках (⚙).", "#F59E0B", "#21190C", "#78350F", "🔐");
            return;
        }

        string prompt = retryData is not null ? retryData.Prompt : PromptBox.Text.Trim();
        var attachmentsSnapshot = retryData is not null
            ? retryData.Attachments
            : _attachments.Select(a => a.Input).ToList();
        var userThumbs = retryData is not null
            ? retryData.Thumbnails
            : _attachments.Where(a => a.Thumbnail is not null).Select(a => a.Thumbnail!).ToList();

        if (string.IsNullOrWhiteSpace(prompt) && attachmentsSnapshot.Count == 0)
            return;

        if (_activeSession is not null && (_activeSession.Title.StartsWith("Чат ") || _activeSession.Title.StartsWith("Диалог ") || _activeSession.Title == "Новый диалог"))
        {
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                string cleanPrompt = prompt.Trim();
                if (cleanPrompt.Length > 28) cleanPrompt = cleanPrompt[..26] + "…";
                _activeSession.Title = cleanPrompt;
                _activeSession.NotifyAll();
            }
        }

        var modelToRun = _activeModel;
        int maxSteps = GetConfiguredMaxSteps();

        Win32Interop.RECT? targetRect = null;
        if (_selectedWindowHandle.ToInt64() < -100)
        {
            int monitorIndex = (int)(-_selectedWindowHandle.ToInt64() - 100);
            var mon = ScreenCaptureService.GetMonitors().FirstOrDefault(m => m.Index == monitorIndex);
            if (mon is not null)
            {
                targetRect = mon.Bounds;
            }
        }
        else if (_selectedWindowHandle != IntPtr.Zero &&
            Win32Interop.IsWindow(_selectedWindowHandle) &&
            Win32Interop.GetWindowRect(_selectedWindowHandle, out var freshRect))
        {
            targetRect = freshRect;
            Win32Interop.SetForegroundWindow(_selectedWindowHandle);
            await Task.Delay(180);
        }

        var policy = new ActionSecurityPolicyOptions
        {
            MaxStepsPerRun = maxSteps,
            EnforceTargetWindowBounds = targetRect is not null,
            TargetWindowRect = targetRect,
            BlockSensitiveForegroundWindows = SensitiveWindowCheck.IsChecked == true,
            RequireConfirmationOnTypingCommands = CommandConfirmCheck.IsChecked == true
        };

        var guard = new ActionSecurityGuard(policy);
        var sandbox = new SafeFileSandbox(_sandboxRoot);
        var fileCreator = new UniversalFileCreator(sandbox);
        var agent = new ComputerUseAgent(_apiClient, _screenCapture, _inputAutomation, guard, fileCreator, policy, _multiProvider)
        {
            TransientServerRetryBaseDelay = AgentRetryBaseDelayForTests
        };

        string modelCacheKey = $"{modelToRun.Provider}:{modelToRun.ModelId}";
        ComputerUseToolMode initialMode = _adaptedModeByModel.TryGetValue(modelCacheKey, out var cachedMode)
            ? cachedMode
            : ComputerUseToolMode.Auto;

        var options = new GeminiRequestOptions(
            Model: modelToRun.ModelId,
            ThinkingLevel: modelToRun.ThinkingLevel,
            ToolMode: initialMode,
            EnableComputerUse: EnableComputerUseCheck.IsChecked == true,
            EnableFileCreation: EnableFileCreationCheck.IsChecked == true,
            Provider: modelToRun.Provider);

        AddUserMessage(
            string.IsNullOrWhiteSpace(prompt) ? $"[Прикреплено файлов: {attachmentsSnapshot.Count}]" : prompt,
            userThumbs);

        if (retryData is null)
        {
            PromptBox.Clear();
            _attachments.Clear();
        }

        _runCts = new CancellationTokenSource();
        RunButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        AgentRunningLabel.Text = $"{modelToRun.DisplayName} анализирует...";
        AgentRunningPill.Visibility = Visibility.Visible;
        ChatScroll.ScrollToEnd();

        int historyCountBeforeTurn = _history.Count;
        bool primarySucceeded = false;

        void RollbackFailedHistory()
        {
            while (_history.Count > historyCountBeforeTurn)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }

        try
        {
            await _agentRunAsync(agent, modelToRun.Secret, prompt, attachmentsSnapshot, options, _runCts.Token);
            primarySucceeded = true;
            _lastFailedPrompt = null;
        }
        catch (OperationCanceledException)
        {
            RollbackFailedHistory();
            _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
            AddStatusCard("ОСТАНОВЛЕНО", "Выполнение прервано.", "#F59E0B", "#21190C", "#78350F", "⏹", BuildErrorActionBar(modelToRun, null));
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 404)
        {
            RollbackFailedHistory();
            _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
            AddStatusCard(
                "МОДЕЛЬ НЕ НАЙДЕНА (404)",
                $"{LogRedactor.Redact(ex.Message)}\n\n💡 Модель «{modelToRun.ModelId}» недоступна для вашего ключа или API v1beta.\nОткройте «⚙ Настройки» → выберите провайдера → вставьте API-ключ → нажмите «🔄 По ключу», чтобы загрузить актуальный список доступных моделей.",
                "#EF4444", "#261215", "#7F1D1D", "⛔",
                BuildErrorActionBar(modelToRun, ex));
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 429)
        {
            RollbackFailedHistory();
            _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
            AddStatusCard(
                "КВОТА API ИСЧЕРПАНА (429)",
                FormatRateLimitMessage(modelToRun.ModelId, ex),
                "#F59E0B", "#21190C", "#78350F", "⏳",
                BuildErrorActionBar(modelToRun, ex));
        }
        catch (GeminiApiException ex) when (ex.StatusCode is 401 or 403)
        {
            RollbackFailedHistory();
            _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
            AddStatusCard(
                $"ДОСТУП ОТКЛОНЁН ({ex.StatusCode})",
                FormatForbiddenMessage(modelToRun.ModelId, ex),
                "#EF4444", "#261215", "#7F1D1D", "⛔",
                BuildErrorActionBar(modelToRun, ex));
        }
        catch (GeminiApiException ex) when (ex.StatusCode is 500 or 502 or 503 or 504 or 529)
        {
            RollbackFailedHistory();
            bool executedBackup = false;

            if (AutoFallbackOnOverloadCheck.IsChecked == true &&
                _runCts is { IsCancellationRequested: false } &&
                TryResolveBackupTarget(modelToRun, out var backupProvider, out var backupModelId, out var backupDisplayName, out var backupSecret))
            {
                executedBackup = true;
                string backupCacheKey = $"{backupProvider}:{backupModelId}";
                ComputerUseToolMode backupInitialMode = _adaptedModeByModel.TryGetValue(backupCacheKey, out var cachedBackupMode)
                    ? cachedBackupMode
                    : ComputerUseToolMode.Auto;

                var backupOptions = options with
                {
                    Provider = backupProvider,
                    Model = backupModelId,
                    ToolMode = backupInitialMode
                };

                AddStatusCard(
                    $"АВТО-РЕЗЕРВ (HTTP {ex.StatusCode})",
                    $"Модель «{modelToRun.ModelId}» перегружена — автоматически выполняю этот запрос через резервную «{backupDisplayName} ({backupModelId})».",
                    "#38BDF8", "#0F1B29", "#1E3A5F", "🔄");
                AgentRunningLabel.Text = $"Резерв: {backupModelId}...";

                var backupAgent = new ComputerUseAgent(_apiClient, _screenCapture, _inputAutomation, guard, fileCreator, policy, _multiProvider)
                {
                    TransientServerRetryBaseDelay = AgentRetryBaseDelayForTests
                };

                try
                {
                    await _agentRunAsync(backupAgent, backupSecret, prompt, attachmentsSnapshot, backupOptions, _runCts.Token);
                    if (backupAgent.LastEffectiveToolMode != ComputerUseToolMode.Auto)
                    {
                        _adaptedModeByModel[backupCacheKey] = backupAgent.LastEffectiveToolMode;
                    }
                    _lastFailedPrompt = null;
                }
                catch (OperationCanceledException)
                {
                    RollbackFailedHistory();
                    _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
                    AddStatusCard("ОСТАНОВЛЕНО", "Выполнение резервной моделью прервано.", "#F59E0B", "#21190C", "#78350F", "⏹", BuildErrorActionBar(modelToRun, null));
                }
                catch (GeminiApiException backupEx) when (backupEx.StatusCode is 500 or 502 or 503 or 504 or 529)
                {
                    RollbackFailedHistory();
                    _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
                    AddStatusCard(
                        $"СЕРВЕР ПЕРЕГРУЖЕН ({backupEx.StatusCode})",
                        FormatTransientOverloadMessage(backupModelId, backupEx),
                        "#F59E0B", "#21190C", "#78350F", "⏳",
                        BuildErrorActionBar(modelToRun, backupEx));
                }
                catch (GeminiApiException backupEx) when (backupEx.StatusCode == 429)
                {
                    RollbackFailedHistory();
                    _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
                    AddStatusCard(
                        "КВОТА API ИСЧЕРПАНА (429)",
                        FormatRateLimitMessage(backupModelId, backupEx),
                        "#F59E0B", "#21190C", "#78350F", "⏳",
                        BuildErrorActionBar(modelToRun, backupEx));
                }
                catch (Exception backupEx)
                {
                    RollbackFailedHistory();
                    _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
                    AddStatusCard("ОШИБКА", LogRedactor.Redact(backupEx.Message), "#EF4444", "#261215", "#7F1D1D", "⛔", BuildErrorActionBar(modelToRun, null));
                }
            }

            if (!executedBackup)
            {
                _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
                AddStatusCard(
                    $"СЕРВЕР ПЕРЕГРУЖЕН ({ex.StatusCode})",
                    FormatTransientOverloadMessage(modelToRun.ModelId, ex),
                    "#F59E0B", "#21190C", "#78350F", "⏳",
                    BuildErrorActionBar(modelToRun, ex));
            }
        }
        catch (Exception ex)
        {
            RollbackFailedHistory();
            _lastFailedPrompt = new FailedPromptRetryData(prompt, attachmentsSnapshot, userThumbs);
            AddStatusCard("ОШИБКА", LogRedactor.Redact(ex.Message), "#EF4444", "#261215", "#7F1D1D", "⛔", BuildErrorActionBar(modelToRun, null));
        }
        finally
        {
            if (primarySucceeded && agent.LastEffectiveToolMode != ComputerUseToolMode.Auto)
            {
                _adaptedModeByModel[modelCacheKey] = agent.LastEffectiveToolMode;
            }
            _runCts.Dispose();
            _runCts = null;
            AgentRunningPill.Visibility = Visibility.Collapsed;
            RunButton.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Collapsed;
        }
    }

    internal ConfiguredModelEntry EnsureOrActivateModelFromExistingGoogleKey(
        ConfiguredModelEntry sourceGoogleEntry,
        string targetModelId,
        string targetDisplayName)
    {
        var existing = _configuredModels.FirstOrDefault(m =>
            m.Provider == AiProviderKind.GoogleGemini &&
            string.Equals(m.ModelId, targetModelId, StringComparison.OrdinalIgnoreCase) &&
            !m.Secret.IsDisposed);

        if (existing is not null)
        {
            _activeModel = existing;
            RefreshConfiguredModelsUi();
            RefreshUnifiedSelectorUi();
            UpdateUsageUi();
            return existing;
        }

        SealedSecret clonedSecret = sourceGoogleEntry.Secret.UseSecret(chars => SealedSecret.Create(chars));
        bool canAutoPersist = sourceGoogleEntry.SavedInVault &&
                              (!_multiVault.VaultExists || !_multiVault.RequiresPassphrase());

        var added = new ConfiguredModelEntry(
            Guid.NewGuid().ToString("N"),
            AiProviderKind.GoogleGemini,
            targetModelId,
            targetDisplayName,
            sourceGoogleEntry.ThinkingLevel,
            canAutoPersist,
            clonedSecret);

        _configuredModels.Add(added);
        _activeModel = added;

        if (canAutoPersist)
        {
            var persisted = _configuredModels.Where(m => m.SavedInVault && !m.Secret.IsDisposed).ToList();
            if (persisted.Count > 0)
            {
                _multiVault.SaveEntries(persisted);
            }
        }

        RefreshConfiguredModelsUi();
        RefreshUnifiedSelectorUi();
        UpdateUsageUi();
        return added;
    }

    private bool TryResolveBackupTarget(
        ConfiguredModelEntry primary,
        out AiProviderKind provider,
        out string modelId,
        out string displayName,
        out SealedSecret secret)
    {
        var candidate = _configuredModels
            .Where(m => !m.Secret.IsDisposed &&
                        !(m.Provider == primary.Provider && string.Equals(m.ModelId, primary.ModelId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.Provider == primary.Provider)
            .ThenByDescending(m => m.ModelId.Contains("2.5-flash", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.ModelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (candidate is not null)
        {
            provider = candidate.Provider;
            modelId = candidate.ModelId;
            displayName = candidate.DisplayName;
            secret = candidate.Secret;
            return true;
        }

        if (primary.Provider == AiProviderKind.GoogleGemini &&
            !primary.Secret.IsDisposed &&
            !string.Equals(primary.ModelId, "gemini-2.5-flash", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProviderKind.GoogleGemini;
            modelId = "gemini-2.5-flash";
            displayName = "Gemini 2.5 Flash";
            secret = primary.Secret;
            return true;
        }

        provider = default;
        modelId = string.Empty;
        displayName = string.Empty;
        secret = primary.Secret;
        return false;
    }

    private UIElement? BuildErrorActionBar(ConfiguredModelEntry modelToRun, GeminiApiException? apiEx)
    {
        var retrySnapshot = _lastFailedPrompt;
        if (retrySnapshot is null)
            return null;

        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Style? btnStyle = TryFindResource("DarkButton") as Style;

        Button MakeActionButton(string text)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 8, 2),
                Cursor = Cursors.Hand
            };
            if (btnStyle is not null) btn.Style = btnStyle;
            return btn;
        }

        if (modelToRun.Provider == AiProviderKind.GoogleGemini &&
            !string.Equals(modelToRun.ModelId, "gemini-2.5-flash", StringComparison.OrdinalIgnoreCase) &&
            !modelToRun.Secret.IsDisposed &&
            (apiEx is null || apiEx.StatusCode is 401 or 403 or 404 or 429 or 500 or 502 or 503 or 504 or 529))
        {
            bool alreadyConfigured = _configuredModels.Any(m =>
                m.Provider == AiProviderKind.GoogleGemini &&
                string.Equals(m.ModelId, "gemini-2.5-flash", StringComparison.OrdinalIgnoreCase) &&
                !m.Secret.IsDisposed);

            var switchBtn = MakeActionButton(alreadyConfigured
                ? "⚡ Переключить на gemini-2.5-flash и повторить"
                : "⚡ Подключить gemini-2.5-flash и повторить");

            switchBtn.Click += async (_, _) =>
            {
                if (_runCts is not null || modelToRun.Secret.IsDisposed) return;
                EnsureOrActivateModelFromExistingGoogleKey(modelToRun, "gemini-2.5-flash", "Gemini 2.5 Flash");
                await ExecutePromptAsync(retrySnapshot);
            };
            panel.Children.Add(switchBtn);
        }

        if (apiEx?.StatusCode == 403)
        {
            var proxyBtn = MakeActionButton("🌐 Настроить Прокси в ⚙");
            proxyBtn.Click += (_, _) =>
            {
                OpenSettingsModal("Models");
            };
            panel.Children.Add(proxyBtn);
        }

        if (!string.Equals(modelToRun.ThinkingLevel, "LOW", StringComparison.OrdinalIgnoreCase) &&
            apiEx?.StatusCode is 500 or 502 or 503 or 504 or 529)
        {
            var lowBtn = MakeActionButton("📉 Снизить до Low и повторить");
            lowBtn.Click += async (_, _) =>
            {
                if (_runCts is not null || modelToRun.Secret.IsDisposed) return;
                modelToRun.ThinkingLevel = "LOW";
                _activeModel = modelToRun;
                RefreshConfiguredModelsUi();
                RefreshUnifiedSelectorUi();
                await ExecutePromptAsync(retrySnapshot);
            };
            panel.Children.Add(lowBtn);
        }

        var retryBtn = MakeActionButton("↻ Повторить запрос");
        retryBtn.Click += async (_, _) =>
        {
            if (_runCts is not null) return;
            await ExecutePromptAsync(retrySnapshot);
        };
        panel.Children.Add(retryBtn);

        return panel;
    }

    internal static string FormatTransientOverloadMessage(string modelId, GeminiApiException ex)
    {
        string shortMsg = LogRedactor.Redact(ex.ExtractShortMessage());
        return $"Серверы провайдера для «{modelId}» сейчас временно перегружены (HTTP {ex.StatusCode} — пик нагрузки).\n" +
               $"• Ответ API: {shortMsg}\n\n" +
               "💡 Это временный сбой на стороне серверов провайдера (ваш API-ключ не заблокирован и лимиты в порядке):\n" +
               "1. Переключитесь на резервную модель (например, gemini-2.5-flash) или снизьте мощность рассуждений до Low.\n" +
               "2. Либо повторите запрос через 10–30 секунд — при включённой опции «Авто-резерв при перегрузке» (🎛) агент сам использует запасную модель.";
    }

    internal static string FormatRateLimitMessage(string modelId, GeminiApiException ex)
    {
        GeminiRateLimitInfo info = ex.ParseRateLimitInfo();
        string retryHint = info.FormatRetryDelayRussian() is string waitStr
            ? $"\n• Повтор по API через: ~{waitStr}"
            : string.Empty;

        if (info.IsZeroLimit && info.IsFreeTier)
        {
            return $"Модель «{modelId}» имеет нулевую бесплатную квоту (Free Tier limit: 0) для текущего API-ключа.{retryHint}\n\n" +
                   "💡 Как решить прямо сейчас:\n" +
                   "1. Бесплатно: откройте «⚙ Настройки» → нажмите «🔄 По ключу» и подключите «gemini-2.5-flash» (или «gemini-3.8-flash») — для них в Aegis работает голос, создание файлов и Computer Use.\n" +
                   "2. Для «" + modelId + "»: подключите платный тариф (Billing / Pay-as-you-go) к проекту ключа в Google AI Studio (https://aistudio.google.com/docs/rate-limits).";
        }

        return $"Превышен лимит запросов или токенов для модели «{modelId}» (HTTP 429).{retryHint}\n" +
               $"{LogRedactor.Redact(info.ShortMessage)}\n\n" +
               "💡 Подождите окончания лимита, переключитесь на более лёгкую модель (например, gemini-2.5-flash) или проверьте квоту в https://ai.dev/rate-limit.";
    }

    private Task<string> _agentRunAsync(
        ComputerUseAgent agent,
        SealedSecret secret,
        string prompt,
        List<AttachmentInput> attachments,
        GeminiRequestOptions options,
        CancellationToken ct)
    {
        return agent.RunTurnAsync(
            secret,
            prompt,
            attachments,
            _history,
            options,
            ConfirmActionOnUiThreadAsync,
            ev => Dispatcher.Invoke(() => HandleAgentEvent(ev)),
            ct);
    }

    private Task<bool> ConfirmActionOnUiThreadAsync(ConfirmationRequest req)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            var dlg = new ConfirmationDialog(req) { Owner = this };
            return dlg.ShowDialog() == true;
        }).Task;
    }

    private void HandleAgentEvent(AgentEvent ev)
    {
        switch (ev.Kind)
        {
            case AgentEventKind.UsageUpdated:
                if (ev.Usage is not null)
                {
                    _lastUsage = ev.Usage;
                    _sessionCumulativeTokens += ev.Usage.TotalTokens;
                    UpdateUsageUi();
                }
                break;

            case AgentEventKind.ModelReasoning:
                AddReasoningEntry(ev.Message);
                break;

            case AgentEventKind.ModelThoughtOrText:
                AddAssistantMessage(ev.Message);
                break;

            case AgentEventKind.ActionExecuted:
                AddStatusCard("COMPUTER USE", ev.Message, "#34D399", "#0E2019", "#164E3A", "🖱");
                break;

            case AgentEventKind.FileCreated:
                _createdFiles.Insert(0, new CreatedFileViewItem(ev.Message, ev.Detail ?? ev.Message));
                AddStatusCard("ФАЙЛ СОЗДАН", $"{ev.Message}\n{ev.Detail}", "#34D399", "#0E2019", "#164E3A", "📄");
                break;

            case AgentEventKind.ActionBlocked:
            case AgentEventKind.SecurityWarning:
                AddStatusCard("SECURITY GUARD", ev.Message, "#F59E0B", "#21190C", "#78350F", "⚠");
                break;

            case AgentEventKind.Error:
                AddStatusCard("ОШИБКА", LogRedactor.Redact(ev.Message), "#EF4444", "#261215", "#7F1D1D", "⛔");
                break;

            case AgentEventKind.Progress:
                AgentRunningLabel.Text = ev.Message;
                break;

            case AgentEventKind.Info:
                AddStatusCard("СИСТЕМА", ev.Message, "#8B94A6", "#11141B", "#222733", "ℹ");
                break;
        }
    }

    // ==================== Rich Chat Entry Builders ====================

    private void AddUserMessage(string promptText, List<ImageSource> thumbnails)
    {
        UIElement visual = MarkdownChatRenderer.BuildRichMessage(promptText);
        _chatEntries.Add(new ChatEntry(
            Header: "Вы",
            IconBadge: "👤",
            TimeText: DateTime.Now.ToString("HH:mm"),
            HeaderBrush: BrushFromHex("#7DD3FC"),
            CardBackground: BrushFromHex("#171C26"),
            CardBorder: BrushFromHex("#283142"),
            CardBorderThickness: new Thickness(1),
            CardCornerRadius: new CornerRadius(14),
            CardPadding: new Thickness(14, 10, 14, 10),
            CardMargin: new Thickness(64, 0, 0, 12),
            HeaderVisibility: Visibility.Visible,
            RichVisual: visual,
            MessageImages: thumbnails));
        ChatScroll.ScrollToEnd();
    }

    private void AddAssistantMessage(string markdownText)
    {
        UIElement visual = MarkdownChatRenderer.BuildRichMessage(markdownText);
        string headerName = _activeModel?.DisplayName ?? "Assistant";
        _chatEntries.Add(new ChatEntry(
            Header: headerName,
            IconBadge: "✨",
            TimeText: DateTime.Now.ToString("HH:mm"),
            HeaderBrush: BrushFromHex("#E2E8F0"),
            CardBackground: Brushes.Transparent,
            CardBorder: Brushes.Transparent,
            CardBorderThickness: new Thickness(0),
            CardCornerRadius: new CornerRadius(0),
            CardPadding: new Thickness(2, 4, 2, 8),
            CardMargin: new Thickness(0, 2, 0, 12),
            HeaderVisibility: Visibility.Visible,
            RichVisual: visual,
            MessageImages: []));
        ChatScroll.ScrollToEnd();
    }

    private void AddReasoningEntry(string thoughtText)
    {
        var expander = new Expander
        {
            Header = "💭 Ход мыслей модели",
            Foreground = BrushFromHex("#9CA3AF"),
            FontSize = 12,
            IsExpanded = false,
            Cursor = Cursors.Hand,
            Content = new Border
            {
                Margin = new Thickness(0, 8, 0, 2),
                Padding = new Thickness(12, 8, 12, 6),
                Background = BrushFromHex("#11141B"),
                BorderBrush = BrushFromHex("#232834"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Child = MarkdownChatRenderer.BuildRichMessage(thoughtText)
            }
        };

        _chatEntries.Add(new ChatEntry(
            Header: string.Empty,
            IconBadge: string.Empty,
            TimeText: string.Empty,
            HeaderBrush: Brushes.Transparent,
            CardBackground: BrushFromHex("#10131B"),
            CardBorder: BrushFromHex("#242B3A"),
            CardBorderThickness: new Thickness(3, 1, 1, 1),
            CardCornerRadius: new CornerRadius(9),
            CardPadding: new Thickness(12, 7, 12, 7),
            CardMargin: new Thickness(0, 0, 0, 8),
            HeaderVisibility: Visibility.Collapsed,
            RichVisual: expander,
            MessageImages: []));
        ChatScroll.ScrollToEnd();
    }

    private void AddStatusCard(string header, string body, string headerHex, string bgHex, string borderHex, string icon, UIElement? actionBar = null)
    {
        var selectBox = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
            FontSize = 11.8,
            Foreground = BrushFromHex("#D6DFEC"),
            SelectionBrush = BrushFromHex("#2563EB")
        };

        UIElement richVisual = selectBox;
        if (actionBar is not null)
        {
            var container = new StackPanel();
            container.Children.Add(selectBox);
            container.Children.Add(actionBar);
            richVisual = container;
        }

        _chatEntries.Add(new ChatEntry(
            Header: header,
            IconBadge: icon,
            TimeText: DateTime.Now.ToString("HH:mm:ss"),
            HeaderBrush: BrushFromHex(headerHex),
            CardBackground: BrushFromHex(bgHex),
            CardBorder: BrushFromHex(borderHex),
            CardBorderThickness: new Thickness(3, 1, 1, 1),
            CardCornerRadius: new CornerRadius(9),
            CardPadding: new Thickness(12, 8, 12, 8),
            CardMargin: new Thickness(0, 0, 0, 8),
            HeaderVisibility: Visibility.Visible,
            RichVisual: richVisual,
            MessageImages: []));
        ChatScroll.ScrollToEnd();
    }

    // ==================== Helpers & ViewModels ====================

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject target)
    {
        DependencyObject? cur = child;
        while (cur is not null)
        {
            if (ReferenceEquals(cur, target)) return true;
            cur = cur is FrameworkContentElement fce
                ? fce.Parent ?? LogicalTreeHelper.GetParent(cur)
                : VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var br = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        br.Freeze();
        return br;
    }

    private static SealedSecret CreateSealedSecretFromPasswordBox(PasswordBox box)
    {
        try
        {
            return CreateTransientSealedSecretWithoutClearing(box);
        }
        finally
        {
            box.Clear();
        }
    }

    private static SealedSecret CreateTransientSealedSecretWithoutClearing(PasswordBox box) =>
        UseSecureStringCharsResult(box.SecurePassword, chars => SealedSecret.Create(chars));

    private static void UseSecureStringChars(SecureString sec, Action<ReadOnlySpan<char>> action)
    {
        UseSecureStringCharsResult(sec, span =>
        {
            action(span);
            return true;
        });
    }

    private static T UseSecureStringCharsResult<T>(SecureString sec, Func<ReadOnlySpan<char>, T> func)
    {
        if (sec.Length == 0)
            return func(ReadOnlySpan<char>.Empty);

        IntPtr ptr = IntPtr.Zero;
        char[] chars = new char[sec.Length];
        try
        {
            ptr = Marshal.SecureStringToCoTaskMemUnicode(sec);
            for (int i = 0; i < sec.Length; i++)
            {
                chars[i] = (char)Marshal.ReadInt16(ptr, i * 2);
            }
            return func(chars);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            if (ptr != IntPtr.Zero)
                Marshal.ZeroFreeCoTaskMemUnicode(ptr);
        }
    }

    public sealed class AttachmentViewItem
    {
        public AttachmentInput Input { get; }
        public ImageSource? Thumbnail { get; }
        public bool IsImage => Thumbnail is not null;
        public string DisplayLabel => $"{Input.FileName} ({FormatBytes(Input.DataBytes.LongLength)})";
        public string TypeIcon => Input.MimeType switch
        {
            var m when m.StartsWith("video/", StringComparison.Ordinal) => "🎬",
            var m when m.StartsWith("audio/", StringComparison.Ordinal) => "🎵",
            "application/pdf" => "📕",
            _ when Input.IsTextDocument => "📝",
            _ => "📎"
        };

        public AttachmentViewItem(AttachmentInput input)
        {
            Input = input;
            if (input.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                Thumbnail = TryCreateThumbnail(input.DataBytes);
            }
        }

        private static ImageSource? TryCreateThumbnail(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 112;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch
            {
                return null;
            }
        }

        private static string FormatBytes(long b) =>
            b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:F1} KB" : $"{b / (1024.0 * 1024.0):F1} MB";
    }

    public sealed record CreatedFileViewItem(string DisplayLabel, string FullPath);

    public sealed record ModelMenuItem(
        string EntryId,
        string DisplayName,
        string ThinkingBadge,
        string ThinkingLevel,
        Brush RowBackground,
        FontWeight DisplayWeight);

    public sealed record ConfiguredModelViewItem(
        string EntryId,
        string ProviderBadge,
        string DisplayName,
        string PowerLabel,
        string SecurityDetails,
        Brush CardBorder,
        string ActivateButtonText);

    public sealed record WindowScopeItem(
        IntPtr Handle,
        string Title,
        string DisplayLabel,
        Brush RowBackground,
        Visibility CheckVisibility);

    public sealed record ChatEntry(
        string Header,
        string IconBadge,
        string TimeText,
        Brush HeaderBrush,
        Brush CardBackground,
        Brush CardBorder,
        Thickness CardBorderThickness,
        CornerRadius CardCornerRadius,
        Thickness CardPadding,
        Thickness CardMargin,
        Visibility HeaderVisibility,
        UIElement RichVisual,
        List<ImageSource> MessageImages)
    {
        public Visibility HasMessageImages => MessageImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed class ChatSessionItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        private string _title = "Новый диалог";
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(nameof(Title)); }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string WorkingDirectory { get; set; } = string.Empty;
        public List<ChatEntry> Entries { get; set; } = [];
        public List<JsonNode> History { get; set; } = [];
        public GeminiUsageMetadata? LastUsage { get; set; }
        public long CumulativeTokens { get; set; }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                _isActive = value;
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(BackgroundBrush));
                OnPropertyChanged(nameof(BorderBrush));
                OnPropertyChanged(nameof(TitleBrush));
            }
        }

        public string FormattedDate => CreatedAt.Date == DateTime.Today
            ? CreatedAt.ToString("HH:mm")
            : CreatedAt.ToString("dd.MM HH:mm");

        public string MessageCountText => Entries.Count == 0
            ? "Пустой чат"
            : $"{Entries.Count} сообщ.";

        public Brush BackgroundBrush => IsActive ? BrushFromHex("#161F2E") : BrushFromHex("#10141D");
        public Brush BorderBrush => IsActive ? BrushFromHex("#2563EB") : BrushFromHex("#1C2433");
        public Brush TitleBrush => IsActive ? BrushFromHex("#7DD3FC") : BrushFromHex("#E2E8F0");

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        public void NotifyAll() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public sealed record DetectedModelCardItem(
        string ModelId,
        string DisplayName,
        string BadgeText,
        Brush BadgeBackground,
        Brush BadgeForeground,
        string StatusText,
        Brush StatusBrush,
        string Description,
        string ActionBtnText,
        Brush CardBackground,
        Brush CardBorder,
        Brush TitleBrush,
        ProviderModelCatalogItem CatalogItem);
}
