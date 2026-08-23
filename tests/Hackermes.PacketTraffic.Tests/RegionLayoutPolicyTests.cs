using Hackermes.App.Views;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

/// <summary>
/// Side panels must yield to the center content column as the window narrows so the
/// content tab strip keeps all tabs visible. The numbers mirror the acceptance matrix:
/// wide 1492, medium 1250, minimum 880 logical pixels.
/// </summary>
public sealed class RegionLayoutPolicyTests
{
    [Theory]
    [InlineData(1492, 240.0, 240)]
    [InlineData(1250, 300.0, 300)]
    public void Wide_and_medium_windows_keep_the_configured_panel_widths(double regionWidth, double desired, double expected) =>
        Assert.Equal(expected, RegionLayout.ClampSidePanelWidth(regionWidth, desired));

    [Fact]
    public void Minimum_window_compresses_both_sides_to_fit_four_content_tabs()
    {
        var left = RegionLayout.ClampSidePanelWidth(880, 240);
        var right = RegionLayout.ClampSidePanelWidth(880, 380);

        Assert.Equal(136, left);
        Assert.Equal(136, right);

        var contentWidth = 880 - left - right - RegionLayout.SideSplittersWidth;
        Assert.True(contentWidth >= RegionLayout.MinContentWidth);
        Assert.True(contentWidth >= 572, "four content tabs (~572px) must fit the strip");
    }

    [Fact]
    public void Oversized_desired_width_is_capped_by_the_window_ratio_on_wide_windows()
    {
        // ratio budget: 1492 * 0.30 = 447.6; content budget: (1492-8-600)/2 = 442 → content wins.
        Assert.Equal(442, RegionLayout.ClampSidePanelWidth(1492, 800));
        // medium: ratio 375 vs content budget 321 → content budget wins.
        Assert.Equal(321, RegionLayout.ClampSidePanelWidth(1250, 800));
    }

    [Fact]
    public void Modest_desired_width_is_never_expanded()
    {
        Assert.Equal(150, RegionLayout.ClampSidePanelWidth(2000, 150));
        Assert.Equal(120, RegionLayout.ClampSidePanelWidth(1250, 120));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void Unknown_region_width_collapses_panels(double regionWidth) =>
        Assert.Equal(0, RegionLayout.ClampSidePanelWidth(regionWidth, 240));

    [Fact]
    public void Windows_narrower_than_the_content_floor_collapse_side_panels()
    {
        // 400 < splitters + min content width: no side panel can be afforded at all.
        Assert.Equal(0, RegionLayout.ClampSidePanelWidth(400, 240));
    }
}
