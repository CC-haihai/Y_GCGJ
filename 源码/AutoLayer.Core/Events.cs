using System;

namespace AutoLayer.Core
{
    /// <summary>
    /// 配置状态变了就喊一声，UI（状态条 / 面板）听到后自己刷新。
    /// Core 不认识 WPF，所以只留一个纯 .NET 事件；没有 UI 时订阅者为空，什么都不会发生。
    /// 所有回调都 try/catch —— 界面出错绝不能连累画图。
    /// </summary>
    public static class LayerEvents
    {
        /// <summary>开关、配置、当前图层、池里的选择，任意一项变了都会触发。</summary>
        public static event Action Changed;

        public static void Raise()
        {
            Action h = Changed;
            if (h == null) return;
            try { h(); } catch { }
        }
    }
}
