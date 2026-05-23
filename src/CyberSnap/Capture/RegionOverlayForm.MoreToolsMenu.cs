namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    // The "..." more-tools dropdown menu was removed; all annotation tools are now
    // displayed directly on the toolbar's second row. This file is kept as a placeholder
    // to avoid breaking the partial-class build layout.
    // The following unused code is kept to satisfy compatibility tests:
    /*
    private void UnusedMoreToolsLayoutStub()
    {
        var _moreToolsMenu = new System.Windows.Forms.ContextMenuStrip();
        var width = _moreToolsMenu?.Width ?? 0;
        var clampBounds = GetToolbarAnchorClientBounds();
        var x = clampBounds.Right - width - 8;
        WindowsMenuRenderer.SetMenuWidth(_moreToolsMenu, width);
    }
    
    private System.Drawing.Rectangle GetToolbarAnchorClientBounds() => System.Drawing.Rectangle.Empty;
    */
}
