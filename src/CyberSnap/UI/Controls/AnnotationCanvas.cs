using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Models.Commands;
using CyberSnap.Services;

namespace CyberSnap.UI.Controls;

/// <summary>
/// Interactive WinForms canvas for the post-capture editor. Hosts a base bitmap,
/// renders vector annotations on top, exposes zoom/pan and a command-based undo
/// stack. Used by EditorForm.
/// </summary>
public sealed partial class AnnotationCanvas : UserControl, IEditorContext
{
    public enum CanvasTool
    {
        Pan,
        Select,
        Draw,
        Arrow,
        CurvedArrow,
        Line,
        Rect,
        Circle,
        Text,
        Crop,
        Eraser,
        Highlight,
    }

    private const int UndoStackLimit = 200;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    public const int MinZoomPercent = 10;
    public const int MaxZoomPercent = 800;

    private Bitmap _baseBitmap;
    private readonly List<Annotation> _annotations = new();
    private readonly List<IEditCommand> _undoStack = new();
    private readonly List<IEditCommand> _redoStack = new();

    private double _zoom = 1.0;
    private PointF _pan; // pixel offset of image-space origin relative to control client
    private bool _isPanning;
    private Point _panStart;
    private PointF _panStartOffset;

    // Selection state (Select tool)
    private int _selectedAnnotationIndex = -1;
    private Annotation? _selectOriginalAnnotation;
    private Point _selectDragStartImg;

    // Eraser hover highlight
    private int _eraserHoverIndex = -1;

    public AnnotationCanvas(Bitmap baseBitmap)
    {
        _baseBitmap = baseBitmap ?? throw new ArgumentNullException(nameof(baseBitmap));

        DoubleBuffered = true;
        BackColor = Color.FromArgb(30, 30, 30);
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        TabStop = true;
    }

    // ── Public surface ─────────────────────────────────────────────────────

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Bitmap BaseBitmap
    {
        get => _baseBitmap;
        set
        {
            if (ReferenceEquals(_baseBitmap, value)) return;
            var old = _baseBitmap;
            _baseBitmap = value ?? throw new ArgumentNullException(nameof(value));
            old?.Dispose();
            Invalidate();
            OnStateChanged();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<Annotation> Annotations => _annotations;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ToolColor { get; set; } = Color.FromArgb(0, 136, 255);

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowCaptureFrame { get; set; } = false;

    private float _bannerOpacity = 0f;
    private string _bannerText = "";
    private System.Windows.Forms.Timer? _bannerTimer;
    private int _bannerHoldTicks = 0;
    private enum BannerState { FadeIn, Hold, FadeOut }
    private BannerState _bannerState = BannerState.FadeIn;

    private void ShowToolBanner(string text)
    {
        _bannerText = text;
        _bannerState = BannerState.FadeIn;
        _bannerOpacity = 0f;
        if (_bannerTimer == null)
        {
            _bannerTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _bannerTimer.Tick += BannerTimer_Tick;
        }
        _bannerTimer.Stop();
        _bannerTimer.Start();
        Invalidate();
    }

    private void BannerTimer_Tick(object? sender, EventArgs e)
    {
        switch (_bannerState)
        {
            case BannerState.FadeIn:
                _bannerOpacity += 0.12f;
                if (_bannerOpacity >= 1.0f)
                {
                    _bannerOpacity = 1.0f;
                    _bannerState = BannerState.Hold;
                    _bannerHoldTicks = 0;
                }
                Invalidate();
                break;

            case BannerState.Hold:
                _bannerHoldTicks++;
                if (_bannerHoldTicks >= 45)
                {
                    _bannerState = BannerState.FadeOut;
                }
                break;

            case BannerState.FadeOut:
                _bannerOpacity -= 0.08f;
                if (_bannerOpacity <= 0.0f)
                {
                    _bannerOpacity = 0.0f;
                    _bannerTimer?.Stop();
                }
                Invalidate();
                break;
        }
    }

    private string GetToolName(CanvasTool tool)
    {
        var key = tool switch
        {
            CanvasTool.Pan => "Pan",
            CanvasTool.Select => "Select",
            CanvasTool.Crop => "Crop",
            CanvasTool.Text => "Text",
            CanvasTool.Draw => "Draw",
            CanvasTool.Arrow => "Arrow",
            CanvasTool.CurvedArrow => "Curved arrow",
            CanvasTool.Line => "Line",
            CanvasTool.Rect => "Rectangle",
            CanvasTool.Circle => "Circle",
            CanvasTool.Eraser => "Eraser",
            CanvasTool.Highlight => "Highlight",
            _ => tool.ToString()
        };
        return LocalizationService.Translate(key);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CanvasTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == value) return;
            CancelInProgressTool();
            _activeTool = value;
            UpdateCursor();
            ShowToolBanner(GetToolName(value));
            Invalidate();
            OnStateChanged();
        }
    }
    private CanvasTool _activeTool = CanvasTool.Pan;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AnnotationStrokeShadow { get; set; } = true;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float StrokeWidth { get; set; } = 6f;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanUndo => _undoStack.Count > 0;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanRedo => _redoStack.Count > 0;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsDirty => _undoStack.Count > 0;

    public event EventHandler? StateChanged;

    /// <summary>Raised after any modification (push/undo/redo, tool change, base bitmap change).</summary>
    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Clears annotations, undo/redo, resets zoom/pan and switches to a new base image.</summary>
    public void ResetState(Bitmap newBaseBitmap)
    {
        if (newBaseBitmap is null) throw new ArgumentNullException(nameof(newBaseBitmap));

        var oldBaseBitmap = _baseBitmap;
        _baseBitmap = newBaseBitmap;
        _annotations.Clear();
        ClearEditHistory();
        _zoom = 1.0;
        _pan = PointF.Empty;
        _isPanning = false;
        _selectedAnnotationIndex = -1;
        _selectOriginalAnnotation = null;
        _selectDragStartImg = Point.Empty;
        _eraserHoverIndex = -1;
        CancelInProgressTool();
        ActiveTool = CanvasTool.Pan;
        oldBaseBitmap?.Dispose();
        ClearCropPending();
        Invalidate();
        OnStateChanged();
    }

    /// <summary>Bakes the saved image into the canvas and treats it as the new clean baseline.</summary>
    public void AcceptSavedBaseline(Bitmap renderedBitmap)
    {
        if (renderedBitmap is null) throw new ArgumentNullException(nameof(renderedBitmap));

        var oldBaseBitmap = _baseBitmap;
        _baseBitmap = new Bitmap(renderedBitmap);
        oldBaseBitmap?.Dispose();
        _annotations.Clear();
        ClearEditHistory();
        _isPanning = false;
        _selectedAnnotationIndex = -1;
        _selectOriginalAnnotation = null;
        _selectDragStartImg = Point.Empty;
        _eraserHoverIndex = -1;
        CancelInProgressTool();
        ClearCropPending();
        Invalidate();
        OnStateChanged();
    }

    void IEditorContext.Invalidate()
    {
        Invalidate();
        OnStateChanged();
    }

    // ── Undo / Redo ────────────────────────────────────────────────────────

    public void Push(IEditCommand command)
    {
        command.Apply(this);
        _undoStack.Add(command);
        if (_undoStack.Count > UndoStackLimit)
        {
            var dropped = _undoStack[0];
            _undoStack.RemoveAt(0);
            dropped.Dispose();
        }
        ClearRedo();
        OnStateChanged();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var cmd = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        cmd.Revert(this);
        _redoStack.Add(cmd);
        OnStateChanged();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var cmd = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        cmd.Apply(this);
        _undoStack.Add(cmd);
        OnStateChanged();
    }

    private void ClearRedo()
    {
        foreach (var c in _redoStack) c.Dispose();
        _redoStack.Clear();
    }

    private void ClearEditHistory()
    {
        foreach (var c in _undoStack) c.Dispose();
        foreach (var c in _redoStack) c.Dispose();
        _undoStack.Clear();
        _redoStack.Clear();
    }

    // ── Zoom / Pan ─────────────────────────────────────────────────────────

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Zoom => _zoom;

    public void ZoomReset()
    {
        _zoom = 1.0;
        CenterImage();
        Invalidate();
        OnStateChanged();
    }

    public void ZoomFit()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        double sx = (double)ClientSize.Width / _baseBitmap.Width;
        double sy = (double)ClientSize.Height / _baseBitmap.Height;
        _zoom = Math.Clamp(Math.Min(sx, sy) * 0.95, MinZoom, MaxZoom);
        CenterImage();
        Invalidate();
        OnStateChanged();
    }

    public void ZoomBy(double factor, Point screenAnchor)
        => ZoomTo(_zoom * factor, screenAnchor);

    public void ZoomTo(double zoom, Point screenAnchor)
    {
        var oldZoom = _zoom;
        var newZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 1e-6) return;

        var imageAnchor = ScreenToImage(screenAnchor);
        _zoom = newZoom;
        _pan = new PointF(
            screenAnchor.X - (float)(imageAnchor.X * _zoom),
            screenAnchor.Y - (float)(imageAnchor.Y * _zoom));
        Invalidate();
        OnStateChanged();
    }

    public void ZoomToPercent(int percent)
        => ZoomTo(percent / 100.0, new Point(ClientSize.Width / 2, ClientSize.Height / 2));

    private void CenterImage()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        float scaledW = (float)(_baseBitmap.Width * _zoom);
        float scaledH = (float)(_baseBitmap.Height * _zoom);
        _pan = new PointF(
            (ClientSize.Width - scaledW) / 2f,
            (ClientSize.Height - scaledH) / 2f);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ZoomFit();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_pan == PointF.Empty)
            CenterImage();
        Invalidate();
    }

    /// <summary>Returns a fresh bitmap with all current annotations baked in.</summary>
    public Bitmap RenderFinal()
    {
        var output = new Bitmap(_baseBitmap.Width, _baseBitmap.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(output);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(_baseBitmap, 0, 0);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        RenderAnnotations(g);
        return output;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bannerTimer?.Stop();
            _bannerTimer?.Dispose();
            ClearEditHistory();
            _baseBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }
}
