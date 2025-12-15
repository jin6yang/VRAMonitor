using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using VRAMonitor.ViewModels.Pages;
using VRAMonitor.Views.Dialogs;
using Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace VRAMonitor.Views.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPageViewModel ViewModel { get; }
        private readonly ResourceLoader _resourceLoader;

        public SettingsPage()
        {
            ViewModel = new SettingsPageViewModel();
            InitializeComponent();

            try { _resourceLoader = new ResourceLoader(); } catch { }

            // [新增] 绑定 ViewModel 的确认请求到 UI 实现
            ViewModel.RequestLanguageChangeConfirmation = ShowLanguageRestartDialogAsync;
        }

        // [新增] 显示语言更改重启确认弹窗
        private async Task<LanguageChangeResult> ShowLanguageRestartDialogAsync()
        {
            var title = _resourceLoader?.GetString("SettingsLanguageRestartTitle") ?? "Restart Required";
            var content = _resourceLoader?.GetString("SettingsLanguageRestartContent") ?? "Language changes will take effect after restarting the application.";
            var btnRestart = _resourceLoader?.GetString("SettingsLanguageRestartNow") ?? "Restart Now";
            var btnLater = _resourceLoader?.GetString("SettingsLanguageRestartLater") ?? "Restart Later";
            var btnCancel = _resourceLoader?.GetString("SettingsLanguageRestartCancel") ?? "Cancel";

            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = title,
                Content = content,
                PrimaryButtonText = btnRestart,
                SecondaryButtonText = btnLater,
                CloseButtonText = btnCancel,
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();

            return result switch
            {
                ContentDialogResult.Primary => LanguageChangeResult.RestartNow,
                ContentDialogResult.Secondary => LanguageChangeResult.RestartLater,
                _ => LanguageChangeResult.Cancel
            };
        }

        private async void OnWindowTopMostCardClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("https://learn.microsoft.com/windows/powertoys/always-on-top"));
            }
            catch { }
        }

        private async void OnOpenPowerToysClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var uri = new Uri("powertoys://");
                var success = await Windows.System.Launcher.LaunchUriAsync(uri);

                if (!success)
                {
                    await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/microsoft/PowerToys/releases"));
                }
            }
            catch { }
        }

        private async void OnEditShortcutClicked(object sender, RoutedEventArgs e)
        {
            var defaultModifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu;
            var defaultKey = VirtualKey.V;

            var dialog = new ShortcutEditorDialog(
                ViewModel.ShortcutModifiers,
                ViewModel.ShortcutKey,
                defaultModifiers,
                defaultKey);

            if (this.XamlRoot != null)
            {
                dialog.XamlRoot = this.XamlRoot;
            }

            // [新增] 本地化快捷键弹窗标题和按钮
            // 假设 ShortcutEditorDialog 继承自 ContentDialog，我们可以直接设置这些属性覆盖默认值
            dialog.Title = _resourceLoader?.GetString("ShortcutEditorTitle") ?? "Edit Shortcut";
            dialog.PrimaryButtonText = _resourceLoader?.GetString("ShortcutEditorSave") ?? "Save";
            dialog.CloseButtonText = _resourceLoader?.GetString("ShortcutEditorCancel") ?? "Cancel";

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                ViewModel.UpdateShortcut(dialog.ResultModifiers, dialog.ResultKey);
            }
        }

        private async void OnOpenSettingsFolderClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                await Launcher.LaunchFolderAsync(folder);
            }
            catch { }
        }

        private async void OnExportSettingsClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var sourceFile = await ApplicationData.Current.LocalFolder.GetFileAsync("settings.json");
                var picker = new FileSavePicker();

                var window = ((App)Application.Current).MainWindow;
                if (window != null)
                {
                    var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
                }

                picker.SuggestedStartLocation = PickerLocationId.Desktop;
                picker.FileTypeChoices.Add("JSON Configuration", new List<string>() { ".json" });
                picker.SuggestedFileName = "VRAMonitor_Settings_Backup";

                var destinationFile = await picker.PickSaveFileAsync();
                if (destinationFile != null)
                {
                    await sourceFile.CopyAndReplaceAsync(destinationFile);
                }
            }
            catch (FileNotFoundException)
            {
                // 这里也可以选择本地化，根据需要添加
                ContentDialog dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "Export Failed",
                    Content = "Configuration file not found. Please change some settings first.",
                    CloseButtonText = "OK",
                    DefaultButton = ContentDialogButton.Close
                };
                await dialog.ShowAsync();
            }
            catch (Exception) { }
        }

        private async void OnResetSettingsClicked(object sender, RoutedEventArgs e)
        {
            // [修改] 本地化重置确认弹窗
            var title = _resourceLoader?.GetString("SettingsResetConfirmTitle") ?? "Restore Defaults?";
            var content = _resourceLoader?.GetString("SettingsResetConfirmContent") ?? "This will reset all settings to default state. This action cannot be undone.";
            var btnReset = _resourceLoader?.GetString("SettingsResetConfirmPrimary") ?? "Reset";
            var btnCancel = _resourceLoader?.GetString("SettingsResetConfirmCancel") ?? "Cancel";

            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = title,
                Content = content,
                PrimaryButtonText = btnReset,
                CloseButtonText = btnCancel,
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                ViewModel.RestoreDefaults();
            }
        }
    }

    public class StringFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null) return null;
            if (parameter == null) return value;
            return string.Format((string)parameter, value);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}