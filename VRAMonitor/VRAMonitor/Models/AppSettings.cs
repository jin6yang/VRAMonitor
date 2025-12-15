using System;
using VRAMonitor.Services;
using Windows.System;

namespace VRAMonitor.Models
{
    public class AppSettings
    {
        public MainWindowLayoutStyle MainWindowLayoutStyle { get; set; } = MainWindowLayoutStyle.StandardGrid;

        public CloseAction CloseAction { get; set; } = CloseAction.MinimizeToTray;
        public int LanguageIndex { get; set; } = 0;
        public bool IsStartOnBoot { get; set; } = false;
        public bool IsWindowTopMost { get; set; } = false;

        public double ScanInterval { get; set; } = 1.0;

        // [新增] 是否显示列表选中项的胶囊指示器 (默认关闭)
        public bool ShowSelectionPill { get; set; } = false;

        public int ThemeIndex { get; set; } = 0;
        public int WindowMaterialIndex { get; set; } = 0;

        public string FontName { get; set; } = "Segoe UI";

        public int ShortcutModifiers { get; set; } = (int)(VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu);
        public int ShortcutKey { get; set; } = (int)VirtualKey.V;
    }
}