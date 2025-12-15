using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VRAMonitor.Models;
using Windows.Storage;
using Windows.System;

namespace VRAMonitor.Services
{
    public enum MainWindowLayoutStyle
    {
        StandardGrid,
        OnlyTop,
        OnlyLeft
    }

    public enum CloseAction { MinimizeToTray, Exit }

    public enum AppBackdrop
    {
        Mica,
        MicaAlt,
        Acrylic,
        AcrylicThin,
        Blur,
        Transparent,
        Solid,
        AcrylicBlur,
        AcrylicThinBlur,
        MicaSolid
    }

    public static class SettingsManager
    {
        private static AppSettings _currentSettings;
        private static readonly string _jsonFilePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "settings.json");
        private static readonly ApplicationDataContainer _legacySettings = ApplicationData.Current.LocalSettings;

        public static event EventHandler<bool> ShowSelectionPillChanged;
        // [新增] 扫描间隔变更事件
        public static event EventHandler<double> ScanIntervalChanged;

        static SettingsManager()
        {
            LoadSettings();
        }

        private static void LoadSettings()
        {
            if (File.Exists(_jsonFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_jsonFilePath);
                    _currentSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                catch
                {
                    _currentSettings = new AppSettings();
                }
            }
            else
            {
                _currentSettings = MigrateFromLocalSettings();
                SaveSettings();
            }
        }

        private static AppSettings MigrateFromLocalSettings()
        {
            var settings = new AppSettings();

            if (_legacySettings.Values.TryGetValue("TitleBarStyle", out var styleVal) && styleVal is string styleStr)
            {
                if (Enum.TryParse<MainWindowLayoutStyle>(styleStr, out var style))
                    settings.MainWindowLayoutStyle = style;
            }

            if (_legacySettings.Values.TryGetValue("CloseAction", out var actionVal) && actionVal is int actionInt)
            {
                settings.CloseAction = (CloseAction)actionInt;
            }

            return settings;
        }

        public static void SaveSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_jsonFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
            }
        }

        // === 公开属性访问器 ===

        public static MainWindowLayoutStyle MainWindowLayoutStyle
        {
            get => _currentSettings.MainWindowLayoutStyle;
            set
            {
                if (_currentSettings.MainWindowLayoutStyle != value)
                {
                    _currentSettings.MainWindowLayoutStyle = value;
                    SaveSettings();
                    MainWindowLayoutStyleChanged?.Invoke(null, value);
                }
            }
        }

        public static CloseAction CloseAction
        {
            get => _currentSettings.CloseAction;
            set { _currentSettings.CloseAction = value; SaveSettings(); }
        }

        public static bool IsStartOnBoot
        {
            get => _currentSettings.IsStartOnBoot;
            set { _currentSettings.IsStartOnBoot = value; SaveSettings(); }
        }

        public static int LanguageIndex
        {
            get => _currentSettings.LanguageIndex;
            set { _currentSettings.LanguageIndex = value; SaveSettings(); }
        }

        public static bool IsWindowTopMost
        {
            get => _currentSettings.IsWindowTopMost;
            set { _currentSettings.IsWindowTopMost = value; SaveSettings(); }
        }

        public static double ScanInterval
        {
            get => _currentSettings.ScanInterval;
            set
            {
                // 简单的防抖判断 (double 比较)
                if (Math.Abs(_currentSettings.ScanInterval - value) > 0.01)
                {
                    _currentSettings.ScanInterval = value;
                    SaveSettings();
                    // [新增] 触发事件
                    ScanIntervalChanged?.Invoke(null, value);
                }
            }
        }

        public static bool ShowSelectionPill
        {
            get => _currentSettings.ShowSelectionPill;
            set
            {
                if (_currentSettings.ShowSelectionPill != value)
                {
                    _currentSettings.ShowSelectionPill = value;
                    SaveSettings();
                    ShowSelectionPillChanged?.Invoke(null, value);
                }
            }
        }

        public static int ThemeIndex
        {
            get => _currentSettings.ThemeIndex;
            set { _currentSettings.ThemeIndex = value; SaveSettings(); }
        }

        public static AppBackdrop Backdrop
        {
            get => (AppBackdrop)_currentSettings.WindowMaterialIndex;
            set { _currentSettings.WindowMaterialIndex = (int)value; SaveSettings(); }
        }

        public static string FontName
        {
            get => _currentSettings.FontName;
            set { _currentSettings.FontName = value; SaveSettings(); }
        }

        public static void SetShortcut(VirtualKeyModifiers modifiers, VirtualKey key)
        {
            _currentSettings.ShortcutModifiers = (int)modifiers;
            _currentSettings.ShortcutKey = (int)key;
            SaveSettings();
        }

        public static (VirtualKeyModifiers Modifiers, VirtualKey Key) GetShortcut()
        {
            return ((VirtualKeyModifiers)_currentSettings.ShortcutModifiers, (VirtualKey)_currentSettings.ShortcutKey);
        }

        public static void RestoreDefaults()
        {
            _currentSettings = new AppSettings();
            SaveSettings();
            MainWindowLayoutStyleChanged?.Invoke(null, _currentSettings.MainWindowLayoutStyle);
            ShowSelectionPillChanged?.Invoke(null, _currentSettings.ShowSelectionPill);
            ScanIntervalChanged?.Invoke(null, _currentSettings.ScanInterval);
        }

        public static event EventHandler<MainWindowLayoutStyle> MainWindowLayoutStyleChanged;
    }
}