using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRAMonitor.Models;
using VRAMonitor.Nvidia;
using VRAMonitor.Services;
using Windows.ApplicationModel;

namespace VRAMonitor.ViewModels.Windows
{
    public partial class MainWindowViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _pageTitle = "进程管理";

        [ObservableProperty]
        private string _appVersion;

        [ObservableProperty]
        private string _appChannel;

        [ObservableProperty]
        private bool _isPreview;

        [ObservableProperty]
        private bool _isBackButtonVisible = false;

        [ObservableProperty]
        private Visibility _standardGridStyleVisibility;

        [ObservableProperty]
        private Visibility _onlyTopStyleVisibility;

        [ObservableProperty]
        private Visibility _onlyLeftStyleVisibility;

        // [重命名]
        private MainWindowLayoutStyle _currentLayoutStyle;

        public Action ShowWindowAction { get; set; }
        public Action OpenSettingsAction { get; set; }
        public Action ExitApplicationAction { get; set; }

        public MainWindowViewModel()
        {
            InitializeAppVersion();

            // [更新] 使用 MainWindowLayoutStyle
            _currentLayoutStyle = SettingsManager.MainWindowLayoutStyle;
            UpdateLayoutStyleVisibility();

            // [更新] 事件监听
            SettingsManager.MainWindowLayoutStyleChanged += OnMainWindowLayoutStyleChanged;
        }

        [RelayCommand]
        private void ShowWindow() => ShowWindowAction?.Invoke();

        [RelayCommand]
        private void OpenSettings() => OpenSettingsAction?.Invoke();

        [RelayCommand]
        private void ExitApplication() => ExitApplicationAction?.Invoke();

        // [重命名] 事件处理方法
        private void OnMainWindowLayoutStyleChanged(object sender, MainWindowLayoutStyle newStyle)
        {
            _currentLayoutStyle = newStyle;
            UpdateLayoutStyleVisibility();
        }

        // [重命名] 更新可见性逻辑
        private void UpdateLayoutStyleVisibility()
        {
            switch (_currentLayoutStyle)
            {
                case MainWindowLayoutStyle.StandardGrid:
                    StandardGridStyleVisibility = Visibility.Visible;
                    OnlyTopStyleVisibility = Visibility.Collapsed;
                    OnlyLeftStyleVisibility = Visibility.Collapsed;
                    break;

                case MainWindowLayoutStyle.OnlyTop:
                    StandardGridStyleVisibility = Visibility.Collapsed;
                    OnlyTopStyleVisibility = Visibility.Visible;
                    OnlyLeftStyleVisibility = Visibility.Collapsed;
                    break;

                case MainWindowLayoutStyle.OnlyLeft:
                    StandardGridStyleVisibility = Visibility.Collapsed;
                    OnlyTopStyleVisibility = Visibility.Collapsed;
                    OnlyLeftStyleVisibility = Visibility.Visible;
                    break;
            }
        }

        private void InitializeAppVersion()
        {
            try
            {
                PackageVersion version = Package.Current.Id.Version;
                AppVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

#if DEBUG
                AppChannel = "DEBUG";
                IsPreview = true;
#else
                if (version.Revision > 0)
                {
                    AppChannel = "PREVIEW";
                    IsPreview = true;
                }
                else
                {
                    AppChannel = "RELEASE";
                    IsPreview = false;
                }
#endif
            }
            catch (Exception)
            {
                AppVersion = "Dev Build";
                AppChannel = "Unpackaged";
                IsPreview = true;
            }
        }

        public void Cleanup()
        {
            SettingsManager.MainWindowLayoutStyleChanged -= OnMainWindowLayoutStyleChanged;
        }
    }
}