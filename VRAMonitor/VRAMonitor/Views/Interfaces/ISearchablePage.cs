using System;
using System.Collections.Generic;
using System.Text;

namespace VRAMonitor.Views.Interfaces
{
    /// <summary>
    /// 允许页面响应主窗口搜索框的接口
    /// </summary>
    public interface ISearchablePage
    {
        /// <summary>
        /// 当用户在主窗口搜索框输入时调用
        /// </summary>
        /// <param name="query">搜索关键词</param>
        void OnSearch(string query);

        /// <summary>
        /// 获取当前页面的搜索框占位符文本（用于 UI 提示用户搜什么）
        /// </summary>
        string SearchPlaceholderText { get; }
    }
}
