// GpuStatusInfo的定义在IGpuTelemetryProvider.cs内

using CommunityToolkit.Mvvm.ComponentModel;

namespace VRAMonitor.ViewModels.Shared
{
    /// <summary>
    /// 用于 UI 绑定的 GPU 状态卡片模型。
    /// 此模型可被所有 Page 包括未来的详细信息页面(DashboardPage)复用。
    /// </summary>
    public partial class GpuStatusViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _header;

        [ObservableProperty]
        private string _details;
    }
}