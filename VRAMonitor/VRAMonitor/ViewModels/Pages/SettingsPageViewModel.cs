using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using VRAMonitor.Services;
using Windows.ApplicationModel.Resources;
using Windows.System;

namespace VRAMonitor.ViewModels.Pages
{
    // ... (保留辅助类定义: LayoutModeOption, FontOption 等)
    public class LayoutModeOption
    {
        public string Name { get; set; }
        public MainWindowLayoutStyle Value { get; set; }
        public bool IsLab { get; set; } = false;
    }

    public class FontOption
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    public class LanguageOption
    {
        public string Name { get; set; }
        public string Tag { get; set; }
    }

    public class ComboBoxOption<T>
    {
        public string Name { get; set; }
        public T Value { get; set; }

        public override bool Equals(object obj)
        {
            if (obj is ComboBoxOption<T> other)
            {
                return EqualityComparer<T>.Default.Equals(Value, other.Value);
            }
            return false;
        }

        public override int GetHashCode()
        {
            return Value?.GetHashCode() ?? 0;
        }
    }

    public enum LanguageChangeResult
    {
        Cancel,
        RestartNow,
        RestartLater
    }

    public partial class SettingsPageViewModel : ObservableObject
    {
        private readonly ResourceLoader _resourceLoader;
        private bool _isInitializing = false;
        private bool _isRestoringLanguage = false;

        public Func<Task<LanguageChangeResult>> RequestLanguageChangeConfirmation { get; set; }

        public ObservableCollection<LanguageOption> AvailableLanguages { get; } = new();
        public ObservableCollection<LayoutModeOption> AvailableLayoutModes { get; } = new();
        public ObservableCollection<FontOption> AvailableFonts { get; } = new();
        public ObservableCollection<ComboBoxOption<AppBackdrop>> AvailableMaterials { get; } = new();
        public ObservableCollection<ComboBoxOption<CloseAction>> AvailableCloseActions { get; } = new();
        public ObservableCollection<ComboBoxOption<ElementTheme>> AvailableThemes { get; } = new();

        [ObservableProperty] private LayoutModeOption _selectedLayoutMode;
        [ObservableProperty] private LanguageOption _selectedLanguage;
        [ObservableProperty] private FontOption _selectedFont;
        [ObservableProperty] private ComboBoxOption<AppBackdrop> _selectedMaterial;
        [ObservableProperty] private ComboBoxOption<CloseAction> _selectedCloseAction;
        [ObservableProperty] private ComboBoxOption<ElementTheme> _selectedTheme;

        [ObservableProperty] private bool _isStartOnBoot;
        [ObservableProperty] private bool _isWindowTopMost;

        [ObservableProperty]
        private double _scanInterval;

        [ObservableProperty] private bool _isSelectionPillVisible;

        public VirtualKeyModifiers ShortcutModifiers { get; set; }
        public VirtualKey ShortcutKey { get; set; }
        public ObservableCollection<string> ShortcutDisplayKeys { get; } = new ObservableCollection<string>();

        // [新增] 用于界面显示的扫描间隔文本（处理 "Stopped"）
        public string ScanIntervalStatusText
        {
            get
            {
                if (ScanInterval <= 0)
                {
                    return GetStr("SettingsScanIntervalStopped", "Stopped");
                }
                return $"{ScanInterval:F1} s";
            }
        }

        public SettingsPageViewModel()
        {
            try { _resourceLoader = new ResourceLoader(); } catch { _resourceLoader = null; }

            LoadAvailableLanguages();
            LoadAvailableFonts();
            LoadLayoutModes();
            LoadMaterials();
            LoadCloseActions();
            LoadThemes();

            LoadSettings();
        }

        private string GetStr(string key, string def)
        {
            if (_resourceLoader == null) return def;
            try
            {
                string val = _resourceLoader.GetString(key);
                if (!string.IsNullOrEmpty(val)) return val;
                val = _resourceLoader.GetString(key + "/Content");
                if (!string.IsNullOrEmpty(val)) return val;
            }
            catch { }
            return def;
        }

        // ... (LoadAvailableLanguages, LoadMaterials 等方法保持不变，省略以节省空间)
        private void LoadAvailableLanguages()
        {
            AvailableLanguages.Clear();
            AvailableLanguages.Add(new LanguageOption { Name = GetStr("SettingsLanguageFollowSystem", "Follow System"), Tag = "System" });
            AvailableLanguages.Add(new LanguageOption { Name = "简体中文", Tag = "zh-CN" });
            AvailableLanguages.Add(new LanguageOption { Name = "English", Tag = "en-US" });
        }

        private void LoadMaterials()
        {
            AvailableMaterials.Clear();
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialMica", "Mica"), Value = AppBackdrop.Mica });
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialMicaAlt", "Mica Alt"), Value = AppBackdrop.MicaAlt });
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialMicaSolid", "Mica Solid"), Value = AppBackdrop.MicaSolid });
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialAcrylic", "Acrylic"), Value = AppBackdrop.Acrylic });
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialAcrylicThin", "Acrylic Thin"), Value = AppBackdrop.AcrylicThin });
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialAcrylicBlur", "Acrylic Blur"), Value = AppBackdrop.AcrylicBlur });
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialAcrylicThinBlur", "Acrylic Thin Blur"), Value = AppBackdrop.AcrylicThinBlur });
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialBlur", "Blur"), Value = AppBackdrop.Blur });
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialTransparent", "Transparent"), Value = AppBackdrop.Transparent });
            AvailableMaterials.Add(new ComboBoxOption<AppBackdrop> { Name = GetStr("SettingsMaterialNone", "None"), Value = AppBackdrop.Solid });
        }

        private void LoadCloseActions()
        {
            AvailableCloseActions.Clear();
            AvailableCloseActions.Add(new ComboBoxOption<CloseAction> { Name = GetStr("SettingsCloseActionMinimize", "最小化到托盘"), Value = CloseAction.MinimizeToTray });
            AvailableCloseActions.Add(new ComboBoxOption<CloseAction> { Name = GetStr("SettingsCloseActionExit", "直接退出"), Value = CloseAction.Exit });
        }

        private void LoadThemes()
        {
            AvailableThemes.Clear();
            AvailableThemes.Add(new ComboBoxOption<ElementTheme> { Name = GetStr("SettingsThemeDefault", "跟随系统"), Value = ElementTheme.Default });
            AvailableThemes.Add(new ComboBoxOption<ElementTheme> { Name = GetStr("SettingsThemeLight", "浅色"), Value = ElementTheme.Light });
            AvailableThemes.Add(new ComboBoxOption<ElementTheme> { Name = GetStr("SettingsThemeDark", "深色"), Value = ElementTheme.Dark });
        }

        private void LoadAvailableFonts()
        {
            var rawList = new List<string>
            {
                "Segoe UI", "Microsoft YaHei UI", "Georgia", "Consolas", "Arial",
                "Arial Rounded MT Bold", "Bradley Hand ITC", "YouYuan", "Berlin Sans FB",
                "ms-appx:///Assets/Fonts/HarmonyOS_SansSC_Regular.ttf#鸿蒙黑体",
                "ms-appx:///Assets/Fonts/MiSans-Regular.ttf#MiSans",
                "ms-appx:///Assets/Fonts/OPPO Sans 4.0.ttf#OPPO Sans 4.0 Light",
                "ms-appx:///Assets/Fonts/vivoSans-Regular.ttf#vivo Sans",
                "ms-appx:///Assets/Fonts/ROGFonts-Regular.ttf#ROG Fonts"
            };

            var unsortedFonts = new List<FontOption>();
            foreach (var rawFontName in rawList)
            {
                string displayName = rawFontName;
                string fontValue = rawFontName;
                if (rawFontName.Contains("#"))
                {
                    var parts = rawFontName.Split('#');
                    if (parts.Length > 1) displayName = parts[1];
                }
                unsortedFonts.Add(new FontOption { Name = displayName, Value = fontValue });
            }

            var sortedFonts = unsortedFonts.OrderBy(f => f.Name).ToList();
            AvailableFonts.Clear();
            AvailableFonts.Add(new FontOption { Name = GetStr("SettingsFontDefault", "System Default"), Value = "Default" });
            foreach (var font in sortedFonts) AvailableFonts.Add(font);
        }

        private void LoadLayoutModes()
        {
            AvailableLayoutModes.Clear();
            AvailableLayoutModes.Add(new LayoutModeOption { Name = GetStr("LayoutStandard", "标准布局"), Value = MainWindowLayoutStyle.StandardGrid });
            AvailableLayoutModes.Add(new LayoutModeOption { Name = GetStr("LayoutNoTitle_OnlyLeft", "隐藏标题栏"), Value = MainWindowLayoutStyle.OnlyLeft, IsLab = true });
            AvailableLayoutModes.Add(new LayoutModeOption { Name = GetStr("LayoutNoNav_OnlyTop", "隐藏导航栏"), Value = MainWindowLayoutStyle.OnlyTop, IsLab = true });
        }

        private void LoadSettings()
        {
            _isInitializing = true;
            try
            {
                var currentStyle = SettingsManager.MainWindowLayoutStyle;
                SelectedLayoutMode = AvailableLayoutModes.FirstOrDefault(x => x.Value == currentStyle) ?? AvailableLayoutModes.First();

                var langIndex = SettingsManager.LanguageIndex;
                SelectedLanguage = (langIndex >= 0 && langIndex < AvailableLanguages.Count)
                    ? AvailableLanguages[langIndex]
                    : AvailableLanguages.FirstOrDefault();

                var savedFontName = SettingsManager.FontName;
                SelectedFont = AvailableFonts.FirstOrDefault(x => x.Value == savedFontName)
                               ?? AvailableFonts.FirstOrDefault();

                var savedBackdrop = SettingsManager.Backdrop;
                SelectedMaterial = AvailableMaterials.FirstOrDefault(x => x.Value == savedBackdrop) ?? AvailableMaterials.First();

                var savedCloseAction = SettingsManager.CloseAction;
                SelectedCloseAction = AvailableCloseActions.FirstOrDefault(x => x.Value == savedCloseAction) ?? AvailableCloseActions.First();

                var savedThemeIndex = SettingsManager.ThemeIndex;
                var themeEnum = Enum.IsDefined(typeof(ElementTheme), savedThemeIndex) ? (ElementTheme)savedThemeIndex : ElementTheme.Default;
                SelectedTheme = AvailableThemes.FirstOrDefault(x => x.Value == themeEnum) ?? AvailableThemes.First();

                IsStartOnBoot = SettingsManager.IsStartOnBoot;
                IsWindowTopMost = SettingsManager.IsWindowTopMost;
                ScanInterval = SettingsManager.ScanInterval;
                IsSelectionPillVisible = SettingsManager.ShowSelectionPill;

                var sc = SettingsManager.GetShortcut();
                ShortcutModifiers = sc.Modifiers;
                ShortcutKey = sc.Key;
                UpdateShortcutDisplay();
            }
            finally
            {
                _isInitializing = false;
            }
        }

        partial void OnSelectedLayoutModeChanged(LayoutModeOption value)
        {
            if (_isInitializing) return;
            if (value != null) SettingsManager.MainWindowLayoutStyle = value.Value;
        }

        async partial void OnSelectedLanguageChanged(LanguageOption value)
        {
            if (_isInitializing) return;
            if (_isRestoringLanguage) return;

            if (value != null)
            {
                var newIndex = AvailableLanguages.IndexOf(value);
                var oldIndex = SettingsManager.LanguageIndex;
                if (newIndex == oldIndex) return;

                if (RequestLanguageChangeConfirmation != null)
                {
                    var result = await RequestLanguageChangeConfirmation.Invoke();
                    if (result == LanguageChangeResult.Cancel)
                    {
                        _isRestoringLanguage = true;
                        if (oldIndex >= 0 && oldIndex < AvailableLanguages.Count) SelectedLanguage = AvailableLanguages[oldIndex];
                        _isRestoringLanguage = false;
                        return;
                    }

                    SettingsManager.LanguageIndex = newIndex;
                    await LanguageSelectorService.SetLanguageAsync(value.Tag);

                    if (result == LanguageChangeResult.RestartNow)
                    {
                        Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
                    }
                }
                else
                {
                    SettingsManager.LanguageIndex = newIndex;
                    await LanguageSelectorService.SetLanguageAsync(value.Tag);
                }
            }
        }

        async partial void OnSelectedFontChanged(FontOption value)
        {
            if (_isInitializing) return;
            if (value != null)
            {
                SettingsManager.FontName = value.Value;
                await ThemeSelectorService.SetFontAsync(value.Value);
            }
        }

        partial void OnSelectedMaterialChanged(ComboBoxOption<AppBackdrop> value)
        {
            if (_isInitializing) return;
            if (value != null)
            {
                SettingsManager.Backdrop = value.Value;
                ThemeSelectorService.SetBackdropAsync(value.Value);
            }
        }

        partial void OnSelectedCloseActionChanged(ComboBoxOption<CloseAction> value)
        {
            if (_isInitializing) return;
            if (value != null) SettingsManager.CloseAction = value.Value;
        }

        async partial void OnSelectedThemeChanged(ComboBoxOption<ElementTheme> value)
        {
            if (_isInitializing) return;
            if (value != null)
            {
                SettingsManager.ThemeIndex = (int)value.Value;
                await ThemeSelectorService.SetThemeAsync(value.Value);
            }
        }

        partial void OnIsStartOnBootChanged(bool value) => SettingsManager.IsStartOnBoot = value;
        partial void OnIsWindowTopMostChanged(bool value) => SettingsManager.IsWindowTopMost = value;

        // [修改] 更新扫描间隔逻辑，同时更新 StatusText
        partial void OnScanIntervalChanged(double value)
        {
            SettingsManager.ScanInterval = value;
            OnPropertyChanged(nameof(ScanIntervalStatusText));
        }

        partial void OnIsSelectionPillVisibleChanged(bool value) => SettingsManager.ShowSelectionPill = value;

        public void UpdateShortcut(VirtualKeyModifiers modifiers, VirtualKey key)
        {
            ShortcutModifiers = modifiers;
            ShortcutKey = key;
            UpdateShortcutDisplay();
            SettingsManager.SetShortcut(modifiers, key);
        }

        private void UpdateShortcutDisplay()
        {
            ShortcutDisplayKeys.Clear();
            if (ShortcutModifiers == VirtualKeyModifiers.None && ShortcutKey == VirtualKey.None) { ShortcutDisplayKeys.Add("None"); return; }
            if (ShortcutModifiers.HasFlag(VirtualKeyModifiers.Windows)) ShortcutDisplayKeys.Add("Win");
            if (ShortcutModifiers.HasFlag(VirtualKeyModifiers.Control)) ShortcutDisplayKeys.Add("Ctrl");
            if (ShortcutModifiers.HasFlag(VirtualKeyModifiers.Menu)) ShortcutDisplayKeys.Add("Alt");
            if (ShortcutModifiers.HasFlag(VirtualKeyModifiers.Shift)) ShortcutDisplayKeys.Add("Shift");
            if (ShortcutKey != VirtualKey.None) ShortcutDisplayKeys.Add(ShortcutKey.ToString());
        }

        public void RestoreDefaults()
        {
            SettingsManager.RestoreDefaults();
            LoadSettings();
            // 确保通知 UI 刷新文本
            OnPropertyChanged(nameof(ScanIntervalStatusText));

            if (SelectedTheme != null) ThemeSelectorService.SetThemeAsync(SelectedTheme.Value);
            if (AvailableFonts.Count > 0) ThemeSelectorService.SetFontAsync(AvailableFonts[0].Value);
            if (SelectedMaterial != null) ThemeSelectorService.SetBackdropAsync(SelectedMaterial.Value);
        }
    }
}