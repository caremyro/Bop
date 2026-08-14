using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bop;

public class DarkModeContextMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly bool _isDarkMode;

    public DarkModeContextMenuRenderer(bool isDarkMode) 
        : base(new DarkModeColorTable(isDarkMode))
    {
        _isDarkMode = isDarkMode;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (!e.Item.Enabled)
        {
            e.TextColor = _isDarkMode ? Color.FromArgb(130, 130, 140) : Color.FromArgb(140, 140, 150);
        }
        else
        {
            e.TextColor = _isDarkMode ? Color.FromArgb(240, 240, 245) : Color.FromArgb(40, 40, 50);
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        Graphics g = e.Graphics;
        Color lineColor = _isDarkMode ? Color.FromArgb(50, 50, 60) : Color.FromArgb(220, 220, 225);
        
        using Pen p = new Pen(lineColor);
        int y = e.Item.Height / 2;
        g.DrawLine(p, 10, y, e.Item.Width - 10, y);
    }
}

public class DarkModeColorTable : ProfessionalColorTable
{
    private readonly bool _isDarkMode;

    public DarkModeColorTable(bool isDarkMode)
    {
        _isDarkMode = isDarkMode;
        UseSystemColors = false;
    }

    public override Color ToolStripDropDownBackground => 
        _isDarkMode ? Color.FromArgb(32, 32, 38) : Color.White;

    public override Color MenuBorder => 
        _isDarkMode ? Color.FromArgb(55, 55, 65) : Color.FromArgb(210, 210, 215);

    public override Color MenuItemSelected => 
        _isDarkMode ? Color.FromArgb(50, 50, 65) : Color.FromArgb(235, 238, 245);

    public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
    public override Color MenuItemSelectedGradientEnd => MenuItemSelected;

    public override Color MenuItemBorder => Color.Transparent;

    public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
    public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
    public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
}