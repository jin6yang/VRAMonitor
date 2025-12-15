using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Windowing;
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
using VRAMonitor.Services;
using VRAMonitor.ViewModels.Windows;
using VRAMonitor.Views.Pages;
using VRAMonitor.Views.Interfaces; // [修改] 更新命名空间引用
using Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WinRT.Interop;

namespace VRAMonitor
{
    public sealed partial class MainWindow : Window
    {
        public MainWindowViewModel ViewModel { get; }
        private readonly ResourceLoader _resourceLoader;

        private readonly Dictionary<string, Type> _pageTypes = new()
        {
            { "ProcessManager", typeof(ProcessesPage) },
            { "Services", typeof(ProcessesPage) },
            { "Battery", typeof(BatteryPage) },
            { "About", typeof(AboutPage) },
            { "Settings", typeof(SettingsPage) }
        };

        private readonly Stack<string> _navigationHistory = new();
        private AppWindow _appWindow;
        private bool _isExplicitExit = false;

        public MainWindow()
        {
            ViewModel = new MainWindowViewModel();
            InitializeComponent();

            try { _resourceLoader = new ResourceLoader(); } catch { }

            SetupTitleBar();
            NavigateToPage("ProcessManager");
            SetInitialSelection();

            SettingsManager.MainWindowLayoutStyleChanged += OnMainWindowLayoutStyleChanged;

            // 搜索框事件订阅 (三个布局的搜索框)
            if (TitleBarSearchBox != null) TitleBarSearchBox.TextChanged += OnSearchBoxTextChanged;
            if (OnlyTopSearchBox != null) OnlyTopSearchBox.TextChanged += OnSearchBoxTextChanged;
            if (NavigationViewSearchBox != null) NavigationViewSearchBox.TextChanged += OnSearchBoxTextChanged;

            AppTitleBar.SizeChanged += OnTitleBarSizeChanged;
            OnlyTopAppTitleBar.SizeChanged += OnTitleBarSizeChanged;
            UpdateSearchBoxMargin();

            ViewModel.ShowWindowAction = () =>
            {
                try
                {
                    this.Show();
                    if (_appWindow != null && _appWindow.Presenter is OverlappedPresenter presenter)
                    {
                        if (presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
                        presenter.IsAlwaysOnTop = true;
                        presenter.IsAlwaysOnTop = false;
                    }
                    this.Activate();
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShowWindow Error: {ex.Message}"); }
            };

            ViewModel.OpenSettingsAction = () => { ViewModel.ShowWindowAction?.Invoke(); NavigateToPage("Settings"); UpdateNavigationViewSelection(); };
            ViewModel.ExitApplicationAction = () => { _isExplicitExit = true; Application.Current.Exit(); };

            if (_appWindow != null) _appWindow.Closing += AppWindow_Closing;
            this.Closed += Window_Closed;
        }

        private void OnSearchBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput || args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            {
                var currentPage = GetCurrentPage();
                if (currentPage is ISearchablePage searchablePage)
                {
                    searchablePage.OnSearch(sender.Text);
                }
            }
        }

        private object GetCurrentPage()
        {
            var currentStyle = SettingsManager.MainWindowLayoutStyle;
            Frame activeFrame;

            if (currentStyle == MainWindowLayoutStyle.StandardGrid) activeFrame = TitleBarContentFrame;
            else if (currentStyle == MainWindowLayoutStyle.OnlyTop) activeFrame = OnlyTopContentFrame;
            else activeFrame = ContentFrame;

            return activeFrame.Content;
        }

        private void UpdateSearchBoxState(object pageContent)
        {
            bool isSearchable = pageContent is ISearchablePage;
            string placeholder = "";
            bool isEnabled = false;
            string unavailableText = _resourceLoader?.GetString("SearchPlaceholder_Unavailable") ?? "Search unavailable on this page";

            if (isSearchable && pageContent is ISearchablePage sp)
            {
                placeholder = sp.SearchPlaceholderText;
                isEnabled = true;
            }
            else
            {
                placeholder = unavailableText;
                isEnabled = false;
            }

            UpdateOneSearchBox(TitleBarSearchBox, isEnabled, placeholder, isSearchable);
            UpdateOneSearchBox(OnlyTopSearchBox, isEnabled, placeholder, isSearchable);
            UpdateOneSearchBox(NavigationViewSearchBox, isEnabled, placeholder, isSearchable);
        }

        private void UpdateOneSearchBox(AutoSuggestBox box, bool isEnabled, string placeholder, bool isSearchable)
        {
            if (box == null) return;

            box.PlaceholderText = placeholder;
            box.IsEnabled = isEnabled;

            if (!isSearchable)
            {
                box.Text = "";
            }
        }

        private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSearchBoxConstraints();

        private void UpdateSearchBoxConstraints()
        {
            if (_appWindow == null) return;
            double rightInset = _appWindow.TitleBar.RightInset;
            if (rightInset == 0) rightInset = 150;

            if (StandardGridStyleLayout.Visibility == Visibility.Visible)
            {
                double leftContentWidth = StandardAppTitleBarLeftContent.ActualWidth + StandardAppTitleBarLeftContent.Margin.Left;
                double maxSideWidth = Math.Max(leftContentWidth, rightInset);
                double availableWidth = AppTitleBar.ActualWidth - (2 * maxSideWidth) - 20;
                TitleBarSearchBox.MaxWidth = Math.Max(0, availableWidth);
            }
            if (OnlyTopStyleLayout.Visibility == Visibility.Visible)
            {
                double leftContentWidth = OnlyTopAppTitleBarLeftContent.ActualWidth + OnlyTopAppTitleBarLeftContent.Margin.Left;
                double maxSideWidth = Math.Max(leftContentWidth, rightInset);
                double availableWidth = OnlyTopAppTitleBar.ActualWidth - (2 * maxSideWidth) - 20;
                OnlyTopSearchBox.MaxWidth = Math.Max(0, availableWidth);
            }
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_isExplicitExit) return;
            if (SettingsManager.CloseAction == CloseAction.MinimizeToTray) { args.Cancel = true; this.Hide(); }
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            SettingsManager.MainWindowLayoutStyleChanged -= OnMainWindowLayoutStyleChanged;
            ViewModel?.Cleanup();
            TrayIcon?.Dispose();
            Application.Current.Exit();
        }

        private void SetupTitleBar()
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow != null)
            {
                var currentStyle = SettingsManager.MainWindowLayoutStyle;
                _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                if (currentStyle == MainWindowLayoutStyle.StandardGrid) SetTitleBar(AppTitleBar);
                else if (currentStyle == MainWindowLayoutStyle.OnlyTop) SetTitleBar(OnlyTopAppTitleBar);
                else SetTitleBar(CustomTitleBar);
                UpdateSearchBoxConstraints();
            }
        }

        private void OnMainWindowLayoutStyleChanged(object sender, MainWindowLayoutStyle newStyle)
        {
            SetupTitleBar();
            SetInitialSelection();
        }

        private void StandardGridNavigationView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) => UpdateSearchBoxMargin();

        private void UpdateSearchBoxMargin()
        {
            if (TitleBarSearchBox != null) TitleBarSearchBox.Visibility = Visibility.Visible;
            if (OnlyTopSearchBox != null) OnlyTopSearchBox.Visibility = Visibility.Visible;
        }

        private void SetInitialSelection()
        {
            var currentStyle = SettingsManager.MainWindowLayoutStyle;
            NavigationView activeNavView = currentStyle switch
            {
                MainWindowLayoutStyle.StandardGrid => StandardGridNavigationView,
                MainWindowLayoutStyle.OnlyTop => OnlyTopNavigationView,
                _ => OnlyLeftNavigationView
            };

            if (activeNavView != null && activeNavView.MenuItems.Count > 0)
                activeNavView.SelectedItem = activeNavView.MenuItems[0];
        }

        private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                NavigateToPage("Settings");
                ViewModel.PageTitle = "设置";
            }
            else if (args.SelectedItem is NavigationViewItem selectedItem)
            {
                string tag = selectedItem.Tag?.ToString();
                if (!string.IsNullOrEmpty(tag))
                {
                    NavigateToPage(tag);
                    ViewModel.PageTitle = selectedItem.Content?.ToString() ?? "";
                }
            }
        }

        private void NavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

        private void NavigateToPage(string pageTag)
        {
            if (_pageTypes.TryGetValue(pageTag, out Type pageType))
            {
                var currentStyle = SettingsManager.MainWindowLayoutStyle;
                Frame activeFrame = currentStyle switch
                {
                    MainWindowLayoutStyle.StandardGrid => TitleBarContentFrame,
                    MainWindowLayoutStyle.OnlyTop => OnlyTopContentFrame,
                    _ => ContentFrame
                };

                var currentTag = GetCurrentPageTag();
                if (activeFrame.Content != null && !_navigationHistory.Contains(currentTag))
                    _navigationHistory.Push(currentTag);

                activeFrame.Navigate(pageType);

                if (activeFrame.Content != null)
                {
                    UpdateSearchBoxState(activeFrame.Content);
                }

                UpdateBackButtonVisibility();
            }
        }

        private void GoBack()
        {
            var currentStyle = SettingsManager.MainWindowLayoutStyle;
            Frame activeFrame = currentStyle switch
            {
                MainWindowLayoutStyle.StandardGrid => TitleBarContentFrame,
                MainWindowLayoutStyle.OnlyTop => OnlyTopContentFrame,
                _ => ContentFrame
            };

            if (activeFrame.CanGoBack)
            {
                activeFrame.GoBack();
                if (activeFrame.Content != null) UpdateSearchBoxState(activeFrame.Content);
                UpdateBackButtonVisibility();
                UpdateNavigationViewSelection();
            }
            else if (_navigationHistory.Count > 0)
            {
                var previousPage = _navigationHistory.Pop();
                NavigateToPage(previousPage);
                UpdateNavigationViewSelection();
            }
        }

        private void UpdateBackButtonVisibility()
        {
            var currentStyle = SettingsManager.MainWindowLayoutStyle;
            Frame activeFrame = currentStyle switch
            {
                MainWindowLayoutStyle.StandardGrid => TitleBarContentFrame,
                MainWindowLayoutStyle.OnlyTop => OnlyTopContentFrame,
                _ => ContentFrame
            };
            ViewModel.IsBackButtonVisible = activeFrame.CanGoBack || _navigationHistory.Count > 0;
        }

        private void UpdateNavigationViewSelection()
        {
            var currentPageTag = GetCurrentPageTag();
            var currentStyle = SettingsManager.MainWindowLayoutStyle;
            NavigationView activeNavView = currentStyle switch
            {
                MainWindowLayoutStyle.StandardGrid => StandardGridNavigationView,
                MainWindowLayoutStyle.OnlyTop => OnlyTopNavigationView,
                _ => OnlyLeftNavigationView
            };

            if (currentPageTag == "Settings")
            {
                activeNavView.SelectedItem = activeNavView.SettingsItem;
                ViewModel.PageTitle = "设置";
            }
            else
            {
                var menuItem = activeNavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag?.ToString() == currentPageTag);
                if (menuItem != null)
                {
                    activeNavView.SelectedItem = menuItem;
                    ViewModel.PageTitle = menuItem.Content?.ToString() ?? "";
                }
                else activeNavView.SelectedItem = null;
            }
        }

        private string GetCurrentPageTag()
        {
            var currentStyle = SettingsManager.MainWindowLayoutStyle;
            Frame activeFrame = currentStyle switch
            {
                MainWindowLayoutStyle.StandardGrid => TitleBarContentFrame,
                MainWindowLayoutStyle.OnlyTop => OnlyTopContentFrame,
                _ => ContentFrame
            };
            if (activeFrame.Content == null) return "";
            var contentType = activeFrame.Content.GetType();
            return _pageTypes.FirstOrDefault(kvp => kvp.Value == contentType).Key ?? "";
        }

        public NavigationViewBackButtonVisible BooleanToBackButtonVisibility(bool isVisible) => isVisible ? NavigationViewBackButtonVisible.Visible : NavigationViewBackButtonVisible.Collapsed;
    }
}