using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using VRAMonitor.Models;
using WinRT.Interop;
using WinUIEx;
using DevWinUI;

using XamlSystemBackdrop = Microsoft.UI.Xaml.Media.SystemBackdrop;

namespace VRAMonitor.Services
{
    public static class ThemeSelectorService
    {
        private static Window MainWindow => ((App)Application.Current).MainWindow;

        private static ThemeService _devWinUIThemeService;

        public static void Initialize()
        {
            SetThemeAsync((ElementTheme)SettingsManager.ThemeIndex);

            string fontName = SettingsManager.FontName;
            if (string.IsNullOrEmpty(fontName)) fontName = "Default";
            SetFontAsync(fontName);

            SetBackdropAsync(SettingsManager.Backdrop);
        }

        public static async Task SetThemeAsync(ElementTheme theme)
        {
            if (MainWindow?.Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = theme;
                UpdateTitleBarButtonColors(theme);
                SettingsManager.ThemeIndex = (int)theme;

                // 强制通知自定义材质更新主题
                if (MainWindow.SystemBackdrop is CustomAcrylicBackdrop customBackdrop)
                {
                    customBackdrop.ResolveTheme(theme);
                }
                else if (MainWindow.SystemBackdrop is CustomMicaBackdrop customMicaBackdrop)
                {
                    customMicaBackdrop.ResolveTheme(theme);
                }
            }
            await Task.CompletedTask;
        }

        public static async Task SetFontAsync(string fontName)
        {
            if (MainWindow?.Content is FrameworkElement rootElement)
            {
                try
                {
                    if (fontName == "Default")
                    {
                        ApplyFontToElement(rootElement, null);
                    }
                    else
                    {
                        var fontFamily = new FontFamily(fontName);
                        ApplyFontToElement(rootElement, fontFamily);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"设置字体失败: {ex.Message}");
                }
            }
            await Task.CompletedTask;
        }

        public static async Task SetBackdropAsync(AppBackdrop backdrop)
        {
            if (MainWindow == null) return;

            Panel rootPanel = MainWindow.Content as Panel;

            if (rootPanel != null)
            {
                if (backdrop == AppBackdrop.Solid)
                    SetSolidBackdrop(rootPanel);
                else
                    rootPanel.Background = new SolidColorBrush(Colors.Transparent);
            }

            try
            {
                bool isDevWinUIHandle = IsDevWinUIBackdrop(backdrop);

                if (isDevWinUIHandle)
                {
                    if (MainWindow.SystemBackdrop != null) MainWindow.SystemBackdrop = null;

                    switch (backdrop)
                    {
                        case AppBackdrop.Mica: await ApplyDevWinUIBackdrop(BackdropType.Mica); break;
                        case AppBackdrop.MicaAlt: await ApplyDevWinUIBackdrop(BackdropType.MicaAlt); break;
                        case AppBackdrop.Acrylic: await ApplyDevWinUIBackdrop(BackdropType.Acrylic); break;
                        case AppBackdrop.AcrylicThin: await ApplyDevWinUIBackdrop(BackdropType.AcrylicThin); break;
                        case AppBackdrop.Solid: await ApplyDevWinUIBackdrop(BackdropType.None); break;
                    }
                }
                else
                {
                    await DisableDevWinUI();

                    switch (backdrop)
                    {
                        case AppBackdrop.Blur:
                            MainWindow.SystemBackdrop = new BlurredBackdrop();
                            break;
                        case AppBackdrop.Transparent:
                            MainWindow.SystemBackdrop = new TransparentTintBackdrop();
                            break;

                        // =================================================================
                        // [macOS 风格 v2.0]
                        // =================================================================

                        case AppBackdrop.AcrylicBlur:
                            if (DesktopAcrylicController.IsSupported())
                            {
                                var acrylic = new CustomAcrylicBackdrop(DesktopAcrylicKind.Base);
                                acrylic.LightTintOpacity = 0.0f;
                                acrylic.LightLuminosityOpacity = 0.40f;
                                acrylic.DarkTintOpacity = 0.0f;
                                acrylic.DarkLuminosityOpacity = 0.85f;
                                MainWindow.SystemBackdrop = acrylic;
                                acrylic.ResolveTheme((ElementTheme)SettingsManager.ThemeIndex);
                            }
                            break;

                        case AppBackdrop.AcrylicThinBlur:
                            if (DesktopAcrylicController.IsSupported())
                            {
                                var acrylic = new CustomAcrylicBackdrop(DesktopAcrylicKind.Thin);
                                acrylic.LightTintOpacity = 0.0f;
                                acrylic.LightLuminosityOpacity = 0.20f;
                                acrylic.DarkTintOpacity = 0.0f;
                                acrylic.DarkLuminosityOpacity = 0.60f;
                                MainWindow.SystemBackdrop = acrylic;
                                acrylic.ResolveTheme((ElementTheme)SettingsManager.ThemeIndex);
                            }
                            break;

                        // [修复] Mica Solid 实现：显式自定义深色和浅色的所有参数
                        case AppBackdrop.MicaSolid:
                            if (MicaController.IsSupported())
                            {
                                var mica = new CustomMicaBackdrop(MicaKind.Base);

                                // --- 浅色模式 ---
                                // Luminosity 1.0 = 不透明 (Solid)
                                // Tint 0.5 = 标准 Mica 浅色浓度
                                mica.LightLuminosityOpacity = 1.0f;
                                mica.LightTintOpacity = 0.5f;

                                // --- 深色模式 ---
                                // Luminosity 1.0 = 不透明 (Solid)
                                // Tint 0.8 = 标准 Mica 深色浓度
                                mica.DarkLuminosityOpacity = 1.0f;
                                mica.DarkTintOpacity = 0.8f;

                                MainWindow.SystemBackdrop = mica;
                                mica.ResolveTheme((ElementTheme)SettingsManager.ThemeIndex);
                            }
                            break;

                        default:
                            MainWindow.SystemBackdrop = null;
                            SetSolidBackdrop(rootPanel);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"材质设置失败: {ex.Message}");
                SetSolidBackdrop(rootPanel);
            }

            SettingsManager.Backdrop = backdrop;
        }

        private static bool IsDevWinUIBackdrop(AppBackdrop backdrop)
        {
            return backdrop == AppBackdrop.Mica ||
                   backdrop == AppBackdrop.MicaAlt ||
                   backdrop == AppBackdrop.Acrylic ||
                   backdrop == AppBackdrop.AcrylicThin ||
                   backdrop == AppBackdrop.Solid;
        }

        private static async Task ApplyDevWinUIBackdrop(BackdropType type)
        {
            if (_devWinUIThemeService == null)
            {
                _devWinUIThemeService = new ThemeService();
                _devWinUIThemeService.ConfigureAutoSave(false);
                _devWinUIThemeService.Initialize(MainWindow);
            }
            await _devWinUIThemeService.SetBackdropTypeAsync(type);
        }

        private static async Task DisableDevWinUI()
        {
            if (_devWinUIThemeService != null)
            {
                await _devWinUIThemeService.SetBackdropTypeAsync(BackdropType.None);
            }
        }

        private static void SetSolidBackdrop(Panel rootPanel)
        {
            if (rootPanel != null)
            {
                rootPanel.ClearValue(Panel.BackgroundProperty);
                if (MainWindow != null && MainWindow.SystemBackdrop != null)
                {
                    if (_devWinUIThemeService == null || MainWindow.SystemBackdrop.GetType().Name != "MicaBackdrop")
                    {
                        MainWindow.SystemBackdrop = null;
                    }
                }
            }
        }

        private static void ApplyFontToElement(DependencyObject element, FontFamily font)
        {
            if (element == null) return;

            if (element is Control control)
            {
                if (font == null) control.ClearValue(Control.FontFamilyProperty);
                else control.FontFamily = font;
            }
            else if (element is TextBlock textBlock)
            {
                if (font == null) textBlock.ClearValue(TextBlock.FontFamilyProperty);
                else textBlock.FontFamily = font;
            }

            if (element is Panel panel)
            {
                foreach (var child in panel.Children) ApplyFontToElement(child, font);
            }
            else if (element is Border border)
            {
                ApplyFontToElement(border.Child, font);
            }
            else if (element is ContentControl contentControl)
            {
                if (contentControl.Content is DependencyObject content)
                {
                    ApplyFontToElement(content, font);
                }
            }
            else if (element is ItemsControl itemsControl)
            {
                foreach (var item in itemsControl.Items)
                {
                    if (item is DependencyObject depItem) ApplyFontToElement(depItem, font);
                }
            }
        }

        private static void UpdateTitleBarButtonColors(ElementTheme theme)
        {
            if (MainWindow == null) return;
            try
            {
                var hWnd = WindowNative.GetWindowHandle(MainWindow);
                var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);

                if (appWindow == null) return;

                var isDark = theme == ElementTheme.Dark;
                if (theme == ElementTheme.Default)
                {
                    isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
                }

                if (isDark)
                {
                    appWindow.TitleBar.ButtonForegroundColor = Colors.White;
                    appWindow.TitleBar.ButtonHoverForegroundColor = Colors.LightGray;
                    appWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(64, 255, 255, 255);
                }
                else
                {
                    appWindow.TitleBar.ButtonForegroundColor = Colors.Black;
                    appWindow.TitleBar.ButtonHoverForegroundColor = Colors.DarkGray;
                    appWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(64, 0, 0, 0);
                }
                appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
            catch { }
        }
    }

    public class BlurredBackdrop : WinUIEx.CompositionBrushBackdrop
    {
        protected override Windows.UI.Composition.CompositionBrush CreateBrush(Windows.UI.Composition.Compositor compositor)
        {
            return compositor.CreateHostBackdropBrush();
        }
    }

    public class CustomAcrylicBackdrop : XamlSystemBackdrop
    {
        private DesktopAcrylicController _controller;
        private readonly DesktopAcrylicKind _kind;
        private SystemBackdropConfiguration _configuration;

        public float LightTintOpacity { get; set; } = 0.0f;
        public float LightLuminosityOpacity { get; set; } = 0.4f;

        public float DarkTintOpacity { get; set; } = 0.0f;
        public float DarkLuminosityOpacity { get; set; } = 0.64f;

        public CustomAcrylicBackdrop(DesktopAcrylicKind kind)
        {
            _kind = kind;
        }

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            if (_controller != null)
            {
                _controller.Dispose();
                _controller = null;
            }

            _controller = new DesktopAcrylicController();
            _controller.Kind = _kind;
            _controller.AddSystemBackdropTarget(connectedTarget);

            _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);

            if (_configuration == null)
            {
                _configuration = new SystemBackdropConfiguration();
            }

            ResolveTheme((ElementTheme)SettingsManager.ThemeIndex);

            try
            {
                _controller.SetSystemBackdropConfiguration(_configuration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CustomAcrylicBackdrop] SetConfiguration failed: {ex.Message}");
            }
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop connectedTarget)
        {
            _controller?.Dispose();
            _controller = null;
            _configuration = null;
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
        {
            if (_controller != null)
            {
                try
                {
                    var defaultConfig = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
                    if (defaultConfig != null)
                    {
                        if (_configuration == null) _configuration = new SystemBackdropConfiguration();
                        _configuration.IsInputActive = defaultConfig.IsInputActive;
                        ResolveTheme((ElementTheme)SettingsManager.ThemeIndex);
                        _controller.SetSystemBackdropConfiguration(_configuration);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CustomAcrylicBackdrop] Update failed: {ex.Message}");
                }
            }
        }

        public void ResolveTheme(ElementTheme appTheme)
        {
            if (_controller == null) return;

            bool isDark = false;
            if (appTheme == ElementTheme.Default)
            {
                isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
            else
            {
                isDark = appTheme == ElementTheme.Dark;
            }

            if (_configuration != null)
            {
                _configuration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
            }

            if (isDark)
            {
                _controller.TintOpacity = DarkTintOpacity;
                _controller.LuminosityOpacity = DarkLuminosityOpacity;
                _controller.TintColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
            }
            else
            {
                _controller.TintOpacity = LightTintOpacity;
                _controller.LuminosityOpacity = LightLuminosityOpacity;
                _controller.TintColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            }
        }
    }

    // [升级] 自定义 Mica SystemBackdrop
    public class CustomMicaBackdrop : XamlSystemBackdrop
    {
        private MicaController _controller;
        private readonly MicaKind _kind;
        private SystemBackdropConfiguration _configuration;

        public float? LightTintOpacity { get; set; }
        public float? LightLuminosityOpacity { get; set; }

        public float? DarkTintOpacity { get; set; }
        public float? DarkLuminosityOpacity { get; set; }

        public CustomMicaBackdrop(MicaKind kind)
        {
            _kind = kind;
        }

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            if (_controller != null)
            {
                _controller.Dispose();
                _controller = null;
            }

            _controller = new MicaController();
            _controller.Kind = _kind;
            _controller.AddSystemBackdropTarget(connectedTarget);

            _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);

            if (_configuration == null)
            {
                _configuration = new SystemBackdropConfiguration();
            }

            ResolveTheme((ElementTheme)SettingsManager.ThemeIndex);

            try
            {
                _controller.SetSystemBackdropConfiguration(_configuration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CustomMicaBackdrop] SetConfiguration failed: {ex.Message}");
            }
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop connectedTarget)
        {
            _controller?.Dispose();
            _controller = null;
            _configuration = null;
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
        {
            if (_controller != null)
            {
                try
                {
                    var defaultConfig = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
                    if (defaultConfig != null)
                    {
                        if (_configuration == null) _configuration = new SystemBackdropConfiguration();
                        _configuration.IsInputActive = defaultConfig.IsInputActive;
                        ResolveTheme((ElementTheme)SettingsManager.ThemeIndex);
                        _controller.SetSystemBackdropConfiguration(_configuration);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CustomMicaBackdrop] Update failed: {ex.Message}");
                }
            }
        }

        // [关键] 强制应用 TintColor，确保深浅色模式显示正确
        public void ResolveTheme(ElementTheme appTheme)
        {
            if (_controller == null) return;

            bool isDark = false;
            if (appTheme == ElementTheme.Default)
            {
                isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
            else
            {
                isDark = appTheme == ElementTheme.Dark;
            }

            if (_configuration != null)
            {
                _configuration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
            }

            if (isDark)
            {
                // [强制] 深色模式下显式设置黑色 TintColor
                _controller.TintColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                if (DarkTintOpacity.HasValue) _controller.TintOpacity = DarkTintOpacity.Value;
                if (DarkLuminosityOpacity.HasValue) _controller.LuminosityOpacity = DarkLuminosityOpacity.Value;
            }
            else
            {
                // [强制] 浅色模式下显式设置白色 TintColor
                _controller.TintColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                if (LightTintOpacity.HasValue) _controller.TintOpacity = LightTintOpacity.Value;
                if (LightLuminosityOpacity.HasValue) _controller.LuminosityOpacity = LightLuminosityOpacity.Value;
            }
        }
    }
}