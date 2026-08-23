using System;

namespace Hackermes.App.Views;

/// <summary>
/// 五区域布局的纯数学策略。中央内容列必须保住最小宽度 —— 最小窗口(880 逻辑像素)
/// 下内容标签条要能完整容纳四个标签(约 572px),否则第 4 个标签只能靠滚动条到达。
/// 数字全部收敛在这里,便于单元测试,不依赖任何 Avalonia 控件。
/// </summary>
internal static class RegionLayout
{
    /// <summary>侧边面板最多占窗口宽度的这个比例,防止小窗口下把中间挤没。</summary>
    internal const double MaxSidePanelRatio = 0.30;

    /// <summary>两侧面板都展开时,中央内容区至少保留的逻辑像素宽度。</summary>
    internal const double MinContentWidth = 600;

    /// <summary>左右两条区域分隔线的合计宽度。</summary>
    internal const double SideSplittersWidth = 8;

    /// <summary>
    /// 单个侧边面板允许应用的最大宽度:取"用户期望值、窗口宽度比例预算、
    /// 中央区保底后的对半预算"三者最小。窗口越窄,两侧让位越多。
    /// </summary>
    internal static double ClampSidePanelWidth(double regionWidth, double desired)
    {
        var ratioBudget = Math.Max(0, regionWidth * MaxSidePanelRatio);
        var contentBudget = Math.Max(0, (regionWidth - SideSplittersWidth - MinContentWidth) / 2);
        return Math.Max(0, Math.Min(desired, Math.Min(ratioBudget, contentBudget)));
    }
}
