using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Windows.System.Power;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml; // 需要引用 Xaml 命名空间以使用 Visibility

namespace VRAMonitor.ViewModels.Pages
{
    public class BatteryPageViewModel : INotifyPropertyChanged
    {
        private readonly DispatcherQueue _dispatcherQueue;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void RaiseAll()
        {
            Raise(nameof(Level));
            Raise(nameof(LevelRatio));
            Raise(nameof(HasBattery));
            Raise(nameof(IsPluggedIn));
            Raise(nameof(IsCharging));
            Raise(nameof(StatusText));

            // 触发新的可见性属性
            Raise(nameof(ChargingVisibility));
            Raise(nameof(NormalVisibility));

            // 状态触发器属性 (保留以备不时之需)
            Raise(nameof(State_OnAC_NoBattery));
            Raise(nameof(State_OnBattery));
            Raise(nameof(State_Charging));
            Raise(nameof(State_FullOrNotCharging));

            Raise(nameof(DebugSummary));
        }

        public BatteryPageViewModel()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            Refresh();

            PowerManager.RemainingChargePercentChanged += (_, __) => EnqueueRefresh();
            PowerManager.BatteryStatusChanged += (_, __) => EnqueueRefresh();
            PowerManager.PowerSupplyStatusChanged += (_, __) => EnqueueRefresh();
        }

        private void EnqueueRefresh()
        {
            _dispatcherQueue.TryEnqueue(() => Refresh());
        }

        private int _level;
        private BatteryStatus _batteryStatus;
        private PowerSupplyStatus _supplyStatus;

        public void Refresh()
        {
            _level = PowerManager.RemainingChargePercent;
            _batteryStatus = PowerManager.BatteryStatus;
            _supplyStatus = PowerManager.PowerSupplyStatus;
            RaiseAll();
        }

        public int Level => Math.Clamp(_level, 0, 100);
        public double LevelRatio => Level / 100.0;

        public bool HasBattery => _batteryStatus != BatteryStatus.NotPresent;
        public bool IsPluggedIn => _supplyStatus == PowerSupplyStatus.Adequate;
        public bool IsCharging => _batteryStatus == BatteryStatus.Charging;

        // === 核心修复：直接提供 Visibility 属性，避免 XAML 触发器失效 ===

        // 只有在充电时显示 (Visible)
        public Visibility ChargingVisibility => IsCharging ? Visibility.Visible : Visibility.Collapsed;

        // 不充电时显示 (Visible) - 包括纯电池使用 或 插电已充满
        public Visibility NormalVisibility => !IsCharging ? Visibility.Visible : Visibility.Collapsed;


        // === 状态逻辑 (用于 VSM 动画) ===
        public bool State_OnAC_NoBattery => !HasBattery && IsPluggedIn;
        public bool State_OnBattery => HasBattery && !IsPluggedIn;
        public bool State_Charging => HasBattery && IsPluggedIn && IsCharging;
        public bool State_FullOrNotCharging => HasBattery && IsPluggedIn && !IsCharging;

        public string StatusText
        {
            get
            {
                if (State_OnAC_NoBattery) return "当前已接入电源";
                if (State_OnBattery) return "当前正在使用电池";
                return IsCharging ? "当前已接入电源，正在充电" : "当前已接入电源，已经充满";
            }
        }

        public string DebugSummary => $"[{_batteryStatus}, {_supplyStatus}] {Level}%";
    }
}