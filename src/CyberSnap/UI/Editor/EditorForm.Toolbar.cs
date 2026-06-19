using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI.Controls;

namespace CyberSnap.UI.Editor;

public sealed partial class EditorForm
{
    private Panel _topBarPanel = null!;
    private Panel _statusBarPanel = null!;
    private Panel _toolbarPanel = null!;
    private Bitmap? _brandBitmap;
    private Label _coordsLabel = null!;
    private Label _dimensionsLabel = null!;
    private Label _fileNameLabel = null!;
    private Label _titleFileNameLabel = null!;
    private Label _zoomLabel = null!;
    private EditorZoomSlider _zoomSlider = null!;
    private bool _suppressZoomSliderChange;
    private EditorCommandButton _undoButton = null!;
    private EditorCommandButton _redoButton = null!;
    private EditorCommandButton _saveButton = null!;
    private EditorCommandButton _copyButton = null!;
    private EditorCommandButton _pasteButton = null!;
    private EditorChromeButton _windowStateButton = null!;
    private EditorZoomBarButton _fitZoomBtn = null!;
    private EditorZoomBarButton _resetZoomBtn = null!;
    private EditorToggleSwitch _toggleFrameSwitch = null!;
    private EditorToggleSwitch _toggleFitSwitch = null!;
    private readonly Dictionary<AnnotationCanvas.CanvasTool, EditorToolButton> _toolButtons = new();
    private EmojiPickerPopup? _emojiPicker;
    private readonly Dictionary<Color, EditorColorButton> _colorButtons = new();
    private readonly List<EditorStrokeWidthButton> _strokeWidthButtons = new();

    private static readonly Color[] PaletteColors =
    {
        Color.FromArgb(0, 255, 255),
        Color.FromArgb(0, 136, 255),
        Color.FromArgb(168, 85, 247),
        Color.FromArgb(255, 45, 85),
        Color.FromArgb(245, 158, 11),
        Color.FromArgb(234, 179, 8),
        Color.FromArgb(34, 197, 94),
        Color.White,
        Color.FromArgb(15, 23, 42),
        Color.FromArgb(236, 72, 153),
        Color.FromArgb(148, 163, 184),
        Color.Black,
    };

    private void BuildToolbar()
    {
        _topBarPanel = BuildTopBar();

        _toolbarPanel = new DoubleBufferedPanel
        {
            Dock = DockStyle.Left,
            Width = 332,
            BackColor = EditorColors.BgSecondary,
            Padding = new Padding(20, 18, 20, 18),
        };
        _toolbarPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(EditorColors.BorderSubtle);
            e.Graphics.DrawLine(pen, _toolbarPanel.Width - 1, 0, _toolbarPanel.Width - 1, _toolbarPanel.Height);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 2,
        };
        EnableDoubleBuffering(layout);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(BuildToolSection(), 0, 0);
        layout.Controls.Add(BuildColorSection(), 0, 1);

        _toolbarPanel.Controls.Add(layout);
    }

    private void BuildStatusBar()
    {
        _statusBarPanel = new DoubleBufferedPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            BackColor = EditorColors.TitleBar,
            Padding = new Padding(18, 8, 18, 8),
        };
        _statusBarPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(EditorColors.BorderSubtle);
            e.Graphics.DrawLine(pen, 0, 0, _statusBarPanel.Width, 0);
        };

        // Create the flow layout for the left side status items (matching the mockup)
        var leftStatusFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };

        // BORDER switch (off-screen, instantiated for burger menu synchronization)
        _toggleFrameSwitch = new EditorToggleSwitch
        {
            LabelText = LocalizationService.Translate("Border"),
            Checked = _canvas.ShowCaptureFrame,
            Height = 42,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(0),
        };
        _toggleFrameSwitch.CheckedChanged += (_, _) =>
        {
            _canvas.ShowCaptureFrame = _toggleFrameSwitch.Checked;
            _canvas.Invalidate();
        };

        // Coords: Icon + Label
        var coordsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        var coordsIcon = new Panel { Width = 20, Height = 20, Margin = new Padding(0, 11, 6, 11) };
        coordsIcon.Paint += (s, e) => StreamlineIcons.DrawIcon(e.Graphics, "select", new RectangleF(0, 0, 20, 20), EditorColors.Accent, 0f, false);
        _coordsLabel = new DoubleBufferedLabel
        {
            AutoSize = true,
            MinimumSize = new Size(85, 0),
            Text = "0, 0",
            ForeColor = EditorColors.TextPrimary,
            Font = new Font("Consolas", 10.5f, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 12),
        };
        coordsPanel.Controls.Add(coordsIcon);
        coordsPanel.Controls.Add(_coordsLabel);
        leftStatusFlow.Controls.Add(coordsPanel);

        // Separator 2
        var sep2 = new Panel { Width = 24, Height = 42, BackColor = Color.Transparent, Margin = new Padding(0) };
        sep2.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
            int y1 = (sep2.Height - 16) / 2;
            e.Graphics.DrawLine(pen, 11, y1, 11, y1 + 16);
        };
        leftStatusFlow.Controls.Add(sep2);

        // Dimensions: Icon + Label + px
        var dimsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        var dimsIcon = new Panel { Width = 20, Height = 20, Margin = new Padding(0, 11, 6, 11) };
        dimsIcon.Paint += (s, e) => StreamlineIcons.DrawIcon(e.Graphics, "rect", new RectangleF(0, 0, 20, 20), EditorColors.Accent, 0f, false);
        _dimensionsLabel = new DoubleBufferedLabel
        {
            AutoSize = true,
            Text = "0 x 0",
            ForeColor = EditorColors.TextPrimary,
            Font = new Font("Consolas", 10.5f, FontStyle.Bold),
            Margin = new Padding(0, 12, 2, 12),
        };
        var pxLabel = new DoubleBufferedLabel
        {
            AutoSize = true,
            Text = "px",
            ForeColor = EditorColors.Accent,
            Font = new Font("Consolas", 10.5f, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 12),
        };
        dimsPanel.Controls.Add(dimsIcon);
        dimsPanel.Controls.Add(_dimensionsLabel);
        dimsPanel.Controls.Add(pxLabel);
        leftStatusFlow.Controls.Add(dimsPanel);

        // Separator 3
        var sep3 = new Panel { Width = 24, Height = 42, BackColor = Color.Transparent, Margin = new Padding(0) };
        sep3.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
            int y1 = (sep3.Height - 16) / 2;
            e.Graphics.DrawLine(pen, 11, y1, 11, y1 + 16);
        };
        leftStatusFlow.Controls.Add(sep3);

        // Filename: Icon + Label
        var filePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        var fileIcon = new Panel { Width = 20, Height = 20, Margin = new Padding(0, 11, 6, 11) };
        fileIcon.Paint += (s, e) => StreamlineIcons.DrawIcon(e.Graphics, "camera", new RectangleF(0, 0, 20, 20), EditorColors.Accent, 0f, false);
        _fileNameLabel = new DoubleBufferedLabel
        {
            AutoSize = true,
            MaximumSize = new Size(200, 0),
            Text = "Unsaved capture",
            ForeColor = EditorColors.TextSecondary,
            Font = new Font("Consolas", 10.5f, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 12),
        };
        filePanel.Controls.Add(fileIcon);
        filePanel.Controls.Add(_fileNameLabel);
        leftStatusFlow.Controls.Add(filePanel);

        // Right-side zoom controls
        _resetZoomBtn = new EditorZoomBarButton
        {
            IconId = "fullscreen",
            Text = "",
            Width = 42,
            Height = 42,
            Margin = new Padding(8, 0, 0, 0),
        };
        _resetZoomBtn.Click += (_, _) => _canvas.ZoomReset();

        // AUTO-FIT switch: fit the image to the canvas on load, or show it at real 100% size.
        _toggleFitSwitch = new EditorToggleSwitch
        {
            LabelText = LocalizationService.Translate("Auto-fit"),
            Checked = _canvas.FitToWindowOnLoad,
            Height = 42,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(12, 0, 0, 0),
        };
        _toggleFitSwitch.CheckedChanged += (_, _) =>
        {
            _canvas.FitToWindowOnLoad = _toggleFitSwitch.Checked;
            _canvas.ApplyInitialView(); // re-frame the current image immediately for instant feedback
            if (System.Windows.Application.Current is CyberSnap.App app)
                app.PersistEditorFitPreference(_toggleFitSwitch.Checked);
        };

        _fitZoomBtn = new EditorZoomBarButton
        {
            IconId = "zoomFit",
            Text = "",
            Width = 42,
            Height = 42,
            Margin = new Padding(8, 0, 0, 0),
        };
        _fitZoomBtn.Click += (_, _) => _canvas.ZoomFit();

        var zoomHost = new EditorZoomHostPanel
        {
            Width = 325,
            Height = 42,
            BackColor = EditorColors.TitleBar,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(0),
        };
        zoomHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
        zoomHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        zoomHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));

        _zoomLabel = new DoubleBufferedLabel
        {
            Dock = DockStyle.Right,
            Width = 65,
            BackColor = EditorColors.TitleBar,
            Text = "100%",
            ForeColor = EditorColors.Accent,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight,
        };

        var zoomText = new DoubleBufferedLabel
        {
            Dock = DockStyle.Left,
            Width = 65,
            BackColor = EditorColors.TitleBar,
            Text = "Zoom",
            ForeColor = EditorColors.TextSecondary,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _zoomSlider = new EditorZoomSlider
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.TitleBar,
            Minimum = AnnotationCanvas.MinZoomPercent,
            Maximum = AnnotationCanvas.MaxZoomPercent,
            Value = 100,
            Margin = new Padding(8, 0, 8, 0),
        };
        _zoomSlider.ValueChanged += (_, _) =>
        {
            if (_suppressZoomSliderChange) return;
            _canvas.ZoomToPercent(_zoomSlider.Value);
        };

        zoomHost.Controls.Add(zoomText, 0, 0);
        zoomHost.Controls.Add(_zoomSlider, 1, 0);
        zoomHost.Controls.Add(_zoomLabel, 2, 0);

        // Right-side controls grouped in a single FlowLayoutPanel
        var rightControlsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        rightControlsFlow.Controls.Add(zoomHost);
        rightControlsFlow.Controls.Add(_resetZoomBtn);
        rightControlsFlow.Controls.Add(_fitZoomBtn);
        rightControlsFlow.Controls.Add(_toggleFitSwitch);

        // Two-column layout: left info | right zoom controls
        var statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        EnableDoubleBuffering(statusLayout);
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        statusLayout.Controls.Add(leftStatusFlow, 0, 0);
        statusLayout.Controls.Add(rightControlsFlow, 1, 0);
        _statusBarPanel.Controls.Add(statusLayout);

        // CyberSnap-styled hover hints for the bottom bar, matching the capture toolbar.
        // The bar sits at the bottom of the window, so the bubbles open upward (above: true).
        // RegisterHoverTooltip(_toggleFrameSwitch, "Show a frame around the capture");
        RegisterHoverTooltip(_toggleFitSwitch, "Fit the image to the window when the editor opens");
        RegisterHoverTooltip(coordsPanel, "Cursor position over the image (X, Y)");
        RegisterHoverTooltip(dimsPanel, "Image size in pixels");
        RegisterHoverTooltip(filePanel, "Current file name");
        RegisterHoverTooltip(_resetZoomBtn, () => WithShortcut("Reset zoom to 100%", LocalizationService.Translate("key 0")));
        RegisterHoverTooltip(_fitZoomBtn, () => WithShortcut("Zoom to fit the image in the window", "F2"));
        RegisterHoverTooltip(_zoomSlider, () => WithShortcut("Drag to zoom in or out", "+ / -"));
    }

    private Panel BuildTopBar()
    {
        // Main container panel
        _topBarPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 108,
            BackColor = EditorColors.TitleBar,
        };
        _topBarPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(EditorColors.BorderSubtle);
            e.Graphics.DrawLine(pen, 0, _topBarPanel.Height - 1, _topBarPanel.Width, _topBarPanel.Height - 1);
        };
        _topBarPanel.MouseDown += BeginWindowDrag;

        // Row 1: Title Bar Panel
        var titleBarPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.Transparent,
            Padding = new Padding(22, 0, 18, 0),
        };
        titleBarPanel.MouseDown += BeginWindowDrag;

        // Brand area: logo + "CyberSnap" + "Annotations Editor"
        var brandPanel = new Panel
        {
            Dock = DockStyle.Left,
            Width = 380,
            BackColor = Color.Transparent,
        };
        brandPanel.MouseDown += BeginWindowDrag;

        // Load logo bitmap
        if (_brandBitmap == null)
        {
            try
            {
                var logoUri = new Uri("pack://application:,,,/Assets/CyberSnap_square.png", UriKind.Absolute);
                var streamInfo = System.Windows.Application.GetResourceStream(logoUri);
                if (streamInfo != null)
                {
                    using (var s = streamInfo.Stream)
                        _brandBitmap = new Bitmap(s);
                }
            }
            catch { }
        }

        brandPanel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int cy = brandPanel.Height / 2;

            if (_brandBitmap != null)
            {
                g.DrawImage(_brandBitmap, new Rectangle(0, cy - 10, 20, 20));
            }

            using var font1 = new Font("Segoe UI Variable Display", 11f, FontStyle.Bold, GraphicsUnit.Point);
            var size1 = TextRenderer.MeasureText("CyberSnap", font1);
            TextRenderer.DrawText(g, "CyberSnap", font1,
                new Rectangle(26, 0, size1.Width, brandPanel.Height),
                EditorColors.Accent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            using var font2 = new Font("Consolas", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
            TextRenderer.DrawText(g, LocalizationService.Translate("Annotations Editor"), font2,
                new Rectangle(26 + size1.Width + 12, 0, 280, brandPanel.Height),
                EditorColors.TextPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        // Window controls FlowLayoutPanel (Dock Right)
        var windowActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        windowActions.MouseDown += BeginWindowDrag;

        var closeButton = MakeChromeButton("close", LocalizationService.Translate("Close"));
        closeButton.Click += (_, _) => Close();
        windowActions.Controls.Add(closeButton);

        _windowStateButton = MakeChromeButton("maximize", LocalizationService.Translate("Maximize"));
        _windowStateButton.Click += (_, _) => ToggleWindowState();
        windowActions.Controls.Add(_windowStateButton);

        var minimizeButton = MakeChromeButton("minimize", LocalizationService.Translate("Minimize"));
        minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        windowActions.Controls.Add(minimizeButton);

        var menuButton = MakeChromeButton("menu", LocalizationService.Translate("Menu"));
        menuButton.Click += (s, _) =>
        {
            _burgerMenu ??= BuildBurgerMenu();
            _burgerMenu.Show(menuButton, new Point(0, menuButton.Height));
        };
        windowActions.Controls.Add(menuButton);

        // Filename Label in the middle
        _titleFileNameLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = EditorColors.TextSecondary,
            Font = new Font("Consolas", 10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Unsaved capture",
        };
        _titleFileNameLabel.MouseDown += BeginWindowDrag;

        titleBarPanel.Controls.Add(_titleFileNameLabel);
        titleBarPanel.Controls.Add(brandPanel);
        titleBarPanel.Controls.Add(windowActions);

        // Row 2: Command Bar Panel
        var commandBarPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        EnableDoubleBuffering(commandBarPanel);
        commandBarPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        commandBarPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        commandBarPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        commandBarPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        commandBarPanel.MouseDown += BeginWindowDrag;

        var commandActions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0),
            Margin = new Padding(0),
            Anchor = AnchorStyles.None, // centers horizontally and vertically in the middle column
        };
        commandActions.MouseDown += BeginWindowDrag;

        // Undo & Redo
        _undoButton = MakeCommandButton("undo", LocalizationService.Translate("Undo"), false);
        _undoButton.Click += (_, _) => _canvas.Undo();
        RegisterHoverTooltip(_undoButton, () => WithShortcut("Undo the last change", "Ctrl+Z"), above: false);
        commandActions.Controls.Add(_undoButton);

        _redoButton = MakeCommandButton("redo", LocalizationService.Translate("Redo"), false);
        _redoButton.Click += (_, _) => _canvas.Redo();
        RegisterHoverTooltip(_redoButton, () => WithShortcut("Redo the last undone change", "Ctrl+Y"), above: false);
        commandActions.Controls.Add(_redoButton);

        // Spacer between undo/redo and gallery
        commandActions.Controls.Add(MakeSeparator());

        // Gallery
        var galleryButton = MakeCommandButton("history", LocalizationService.Translate("Gallery"), false);
        galleryButton.Click += (_, _) => OpenHistoryWindow();
        RegisterHoverTooltip(galleryButton, "Open Capture Gallery", above: false);
        commandActions.Controls.Add(galleryButton);

        // Spacer between gallery and file actions
        commandActions.Controls.Add(MakeSeparator());

        // New & Open
        var newButton = MakeCommandButton("document", LocalizationService.Translate("New"), false);
        newButton.Click += (_, _) => DoNew();
        RegisterHoverTooltip(newButton, () => WithShortcut("Create a blank canvas", "Ctrl+N"), above: false);
        commandActions.Controls.Add(newButton);

        var openButton = MakeCommandButton("folder", LocalizationService.Translate("Open"), false);
        openButton.Click += (_, _) => DoOpen();
        RegisterHoverTooltip(openButton, () => WithShortcut("Open an image file", "Ctrl+O"), above: false);
        commandActions.Controls.Add(openButton);

        // Clipboard actions: Paste & Copy
        _pasteButton = MakeCommandButton("paste", LocalizationService.Translate("Paste"), false);
        _pasteButton.Click += (_, _) => DoPaste();
        RegisterHoverTooltip(_pasteButton, () => WithShortcut("Paste image from clipboard", "Ctrl+V"), above: false);
        commandActions.Controls.Add(_pasteButton);

        _copyButton = MakeCommandButton("copy", LocalizationService.Translate("Copy"), false);
        _copyButton.Click += (_, _) => DoCopy();
        RegisterHoverTooltip(_copyButton, () => WithShortcut("Copy the image to the clipboard", "Ctrl+C"), above: false);
        commandActions.Controls.Add(_copyButton);

        // Save actions: Save & Export
        _saveButton = MakeCommandButton("save", LocalizationService.Translate("Save"), false);
        _saveButton.Click += (_, _) => DoSave();
        RegisterHoverTooltip(_saveButton, () => WithShortcut("Save the image", "Ctrl+S"), above: false);
        commandActions.Controls.Add(_saveButton);

        var exportButton = MakeCommandButton("export", LocalizationService.Translate("Export"), false);
        exportButton.Click += (_, _) => DoSaveAs();
        RegisterHoverTooltip(exportButton, () => WithShortcut(LocalizationService.Translate("Export the image"), "Ctrl+Shift+S"), above: false);
        commandActions.Controls.Add(exportButton);

        commandBarPanel.Controls.Add(commandActions, 1, 0);

        _topBarPanel.Controls.Add(commandBarPanel);
        _topBarPanel.Controls.Add(titleBarPanel);

        UpdateWindowStateButton();

        return _topBarPanel;
    }

    private Control BuildToolSection()
    {
        var section = MakeSectionPanel(null, 530);

        // The Tools section keeps two visually distinct groups at the SAME total height
        // (so the editor window layout stays pixel-identical after a capture):
        //   • Navigation/utility tools — 2 columns, wider buttons (2 of 6 rows worth)
        //   • Drawing/annotation tools — 3 columns, denser palette (4 of 6 rows worth)
        // The density change reinforces the group boundary the divider already draws.
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 2, 0, 0),
        };
        EnableDoubleBuffering(host);
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34f)); // 2/6
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 66.66f)); // 4/6

        var nav = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 2,
        };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        nav.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        nav.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        AddToolButton(nav, 0, 0, AnnotationCanvas.CanvasTool.Pan, "pan", "Pan");
        AddToolButton(nav, 1, 0, AnnotationCanvas.CanvasTool.Move, "select", "Move");
        AddToolButton(nav, 0, 1, AnnotationCanvas.CanvasTool.Crop, "rect", "Crop");
        AddToolButton(nav, 1, 1, AnnotationCanvas.CanvasTool.Eraser, "eraser", "Eraser");

        var draw = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 3,
            RowCount = 4,
        };
        for (int c = 0; c < 3; c++)
            draw.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        for (int r = 0; r < 4; r++)
            draw.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));

        AddToolButton(draw, 0, 0, AnnotationCanvas.CanvasTool.Draw, "draw", "Draw");
        AddToolButton(draw, 1, 0, AnnotationCanvas.CanvasTool.Arrow, "arrow", "Arrow");
        AddToolButton(draw, 2, 0, AnnotationCanvas.CanvasTool.CurvedArrow, "curvedArrow", "Curved");
        AddToolButton(draw, 0, 1, AnnotationCanvas.CanvasTool.Line, "line", "Line");
        AddToolButton(draw, 1, 1, AnnotationCanvas.CanvasTool.Rect, "rectShape", "Rectangle");
        AddToolButton(draw, 2, 1, AnnotationCanvas.CanvasTool.Circle, "circleShape", "Circle");
        AddToolButton(draw, 0, 2, AnnotationCanvas.CanvasTool.Text, "text", "Text");
        AddToolButton(draw, 1, 2, AnnotationCanvas.CanvasTool.Highlight, "highlight", "Highlight");
        AddToolButton(draw, 2, 2, AnnotationCanvas.CanvasTool.Blur, "blur", "Blur");
        AddToolButton(draw, 0, 3, AnnotationCanvas.CanvasTool.StepNumber, "step", "Step");
        AddToolButton(draw, 1, 3, AnnotationCanvas.CanvasTool.Magnifier, "magnifier", "Magnifier");
        AddToolButton(draw, 2, 3, AnnotationCanvas.CanvasTool.Emoji, "emoji", "Emoji");

        // Second divider inside the drawing group: between the line/shape row (1) and the
        // text/highlight/blur row (2). Painted in the existing button-margin gap, so it
        // costs no vertical layout space — same technique as the group divider.
        draw.Paint += (_, e) =>
        {
            var heights = draw.GetRowHeights();
            if (heights.Length < 2) return;
            int y = draw.Padding.Top + heights[0] + heights[1];
            DrawToolGroupDivider(e.Graphics, draw.ClientRectangle.Width, y);
        };

        // Divider painted in the gap between the two groups (between host rows 0 and 1),
        // so it costs no vertical layout space — same technique as before.
        host.Paint += (_, e) =>
        {
            var heights = host.GetRowHeights();
            if (heights.Length < 1) return;
            int y = host.Padding.Top + heights[0];
            DrawToolGroupDivider(e.Graphics, host.ClientRectangle.Width, y);
        };

        host.Controls.Add(nav, 0, 0);
        host.Controls.Add(draw, 0, 1);

        section.Controls.Add(host, 0, 1);
        return section;
    }

    // Cyan hairline with a soft glow, fading out toward both ends so it reads as an
    // elegant group separator rather than a hard rule. Matches the accent glow used
    // on the status bar and toolbar borders.
    private static void DrawToolGroupDivider(Graphics g, int width, int y)
    {
        if (width <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, y - 2, width, 4);
        DrawFadedAccentLine(g, area, y, width, 26, 3f);   // soft glow underneath
        DrawFadedAccentLine(g, area, y, width, 80, 1f);   // crisp hairline on top
    }

    private static void DrawFadedAccentLine(Graphics g, Rectangle area, int y, int width, int peakAlpha, float thickness)
    {
        using var brush = new LinearGradientBrush(area, Color.Empty, Color.Empty, LinearGradientMode.Horizontal)
        {
            InterpolationColors = new ColorBlend(3)
            {
                Colors = new[]
                {
                    Color.FromArgb(0, EditorColors.Accent),
                    Color.FromArgb(peakAlpha, EditorColors.Accent),
                    Color.FromArgb(0, EditorColors.Accent),
                },
                Positions = new[] { 0f, 0.5f, 1f },
            },
        };
        using var pen = new Pen(brush, thickness);
        g.DrawLine(pen, 0, y, width, y);
    }



    private Control BuildColorSection()
    {
        var section = MakeSectionPanel(null, 240);

        int swatchSize = (int)Math.Round(28 * 1.35);
        int strokeSize = (int)Math.Round(32 * 1.35);

        // Palette (6 columns) stacked over the stroke-width row (5 columns). Both are grids that
        // stretch to the full toolbar content width, so their right edge lines up with the Tools
        // grid above and the swatches stay evenly distributed.
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 2,
        };
        EnableDoubleBuffering(outer);
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, (swatchSize + 8) * 2));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, strokeSize + 8));

        const int paletteColumns = 6;
        var palette = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = paletteColumns,
            RowCount = 2,
        };
        EnableDoubleBuffering(palette);
        for (int c = 0; c < paletteColumns; c++)
            palette.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / paletteColumns));
        palette.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        palette.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        string[] colorNames =
        {
            "Cyan",
            "Blue",
            "Purple",
            "Red",
            "Orange",
            "Yellow",
            "Green",
            "White",
            "Dark Slate",
            "Pink",
            "Gray",
            "Black"
        };

        int index = 0;
        foreach (var color in PaletteColors)
        {
            var swatch = new EditorColorButton
            {
                SwatchColor = color,
                Width = swatchSize,
                Height = swatchSize,
                Anchor = AnchorStyles.None, // center within the cell
                Margin = new Padding(0),
            };
            swatch.Click += (_, _) =>
            {
                _canvas.ToolColor = color;
                UpdateColorSwatch();
                if (System.Windows.Application.Current is CyberSnap.App app)
                    app.PersistEditorToolColor(color.ToArgb());
            };
            _colorButtons[color] = swatch;
            RegisterHoverTooltip(swatch, colorNames[index]);
            palette.Controls.Add(swatch, index % paletteColumns, index / paletteColumns);
            index++;
        }

        var strokeWidths = new[] { 2f, 3f, 4f, 6f, 10f };
        var widthRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = strokeWidths.Length,
            RowCount = 1,
        };
        EnableDoubleBuffering(widthRow);
        for (int c = 0; c < strokeWidths.Length; c++)
            widthRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / strokeWidths.Length));

        int wIndex = 0;
        foreach (var w in strokeWidths)
        {
            var btn = new EditorStrokeWidthButton
            {
                StrokeWidth = w,
                Width = strokeSize,
                Height = strokeSize,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0),
            };
            btn.Click += (_, _) =>
            {
                _canvas.StrokeWidth = w;
                UpdateStrokeWidthButtons();
                if (System.Windows.Application.Current is CyberSnap.App app)
                    app.PersistEditorStrokeWidth(w);
            };
            _strokeWidthButtons.Add(btn);
            float currentWidth = w;
            RegisterHoverTooltip(btn, () => string.Format(LocalizationService.Translate("Width: {0}px"), currentWidth));
            widthRow.Controls.Add(btn, wIndex, 0);
            wIndex++;
        }

        outer.Controls.Add(palette, 0, 0);
        outer.Controls.Add(widthRow, 0, 1);
        section.Controls.Add(outer, 0, 1);
        return section;
    }

    private TableLayoutPanel MakeSectionPanel(string? title, int height)
    {
        bool showTitle = !string.IsNullOrEmpty(title);
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = showTitle ? height : height - 24,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 0, 0, 12),
            ColumnCount = 1,
            RowCount = 2,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, showTitle ? 24 : 0));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        if (showTitle)
        {
            var label = new DoubleBufferedLabel
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = title!,
                ForeColor = EditorColors.Accent,
                Font = new Font("Segoe UI Variable Text", 9f, FontStyle.Bold, GraphicsUnit.Point),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            panel.Controls.Add(label, 0, 0);
        }
        return panel;
    }

    private void AddToolButton(
        TableLayoutPanel parent,
        int column,
        int row,
        AnnotationCanvas.CanvasTool tool,
        string iconId,
        string labelKey)
    {
        var label = LocalizationService.Translate(labelKey);
        // Even gutters between buttons; outer edges flush so the grid fills the column.
        int cols = parent.ColumnCount;
        int left = column == 0 ? 0 : 4;
        int right = column == cols - 1 ? 0 : 4;
        var button = new EditorToolButton
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(left, 4, right, 4),
            IconId = iconId,
            Text = label,
        };
        button.Click += (_, _) =>
        {
            _canvas.ActiveTool = tool;
            _canvas.Focus();
            if (tool == AnnotationCanvas.CanvasTool.Emoji)
                OpenEmojiPicker(button);
        };
        _toolButtons[tool] = button;

        // The 3-column drawing grid truncates the longest names; surface the full
        // label on hover for those so nothing is lost.
        if (tool is AnnotationCanvas.CanvasTool.Magnifier
                or AnnotationCanvas.CanvasTool.Rect
                or AnnotationCanvas.CanvasTool.Highlight)
            RegisterHoverTooltip(button, labelKey);
        else if (tool is AnnotationCanvas.CanvasTool.Move)
            RegisterHoverTooltip(button, "Move & Resize");
        else if (tool is AnnotationCanvas.CanvasTool.Pan)
            RegisterHoverTooltip(button, "Pan (Hold Space)");
        else if (tool is AnnotationCanvas.CanvasTool.Emoji)
            RegisterHoverTooltip(button, "Emoji (Scroll Wheel to Resize)");

        parent.Controls.Add(button, column, row);
    }

    private EditorCommandButton MakeCommandButton(string iconId, string text, bool primary)
    {
        int width = 74;
        using (var g = CreateGraphics())
        using (var font = new Font("Segoe UI Variable Text", 8.5f, FontStyle.Bold, GraphicsUnit.Point))
        {
            var size = TextRenderer.MeasureText(g, text, font);
            width = Math.Max(74, size.Width + 16);
        }
        return new EditorCommandButton
        {
            IconId = iconId,
            Text = text,
            Primary = primary,
            Width = width,
            Height = 62,
            Margin = new Padding(2, 0, 2, 0),
        };
    }

    private EditorChromeButton MakeChromeButton(string iconId, string tooltip)
    {
        var button = new EditorChromeButton
        {
            IconId = iconId,
            Width = 42,
            Height = 44,
            Margin = new Padding(4, 0, 0, 0),
            AccessibleName = tooltip,
        };
        // Top-bar chrome: open the CyberSnap-styled hint downward (below: above = false).
        // Reading AccessibleName keeps the text in sync for buttons whose label changes
        // at runtime (e.g. Maximize <-> Restore).
        RegisterHoverTooltip(button, () => button.AccessibleName ?? tooltip, above: false);
        return button;
    }

    private Control MakeSeparator()
    {
        var separator = new Panel
        {
            Width = 17,
            Height = 32,
            Margin = new Padding(2, 6, 2, 6),
            BackColor = Color.Transparent,
        };
        separator.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
            e.Graphics.DrawLine(pen, 8, 4, 8, separator.Height - 4);
        };
        return separator;
    }

    private void ToggleWindowState()
    {
        if (_isManualMaximized)
            RestoreManualMaximize();
        else
            ApplyManualMaximize();

        UpdateWindowStateButton();
    }

    private void UpdateWindowStateButton()
    {
        if (_windowStateButton is null)
            return;

        _windowStateButton.IconId = _isManualMaximized ? "restore" : "maximize";
        _windowStateButton.AccessibleName =
            LocalizationService.Translate(_isManualMaximized ? "Restore" : "Maximize");
    }

    private EditorToolButton? GetEmojiToolButton() =>
        _toolButtons.TryGetValue(AnnotationCanvas.CanvasTool.Emoji, out var b) ? b : null;

    private void OpenEmojiPicker(EditorToolButton? anchor)
    {
        // Clicking Emoji while the picker is open toggles it shut. The deferred close in
        // EmojiPickerPopup's Deactivate already dismisses it on outside clicks; this keeps
        // the Emoji button itself a clean one-click toggle without a timing guard.
        if (_emojiPicker is { IsDisposed: false })
        {
            _emojiPicker.Close();
            return;
        }

        _emojiPicker = new EmojiPickerPopup(_canvas);
        _emojiPicker.EmojiChosen += emoji =>
        {
            _canvas.SelectedEmoji = emoji;
            _canvas.ActiveTool = AnnotationCanvas.CanvasTool.Emoji;
            _canvas.Focus();
        };
        _emojiPicker.FormClosed += (_, _) =>
        {
            _emojiPicker = null;
        };

        var anchorRect = anchor is not null
            ? anchor.RectangleToScreen(anchor.ClientRectangle)
            : new Rectangle(Cursor.Position, Size.Empty);
        _emojiPicker.ShowNear(anchorRect);
    }

    private static void OpenSettingsWindow()
    {
        if (System.Windows.Application.Current is not CyberSnap.App app)
            return;

        if (app.Dispatcher.CheckAccess())
        {
            app.ShowSettings();
            return;
        }

        _ = app.Dispatcher.BeginInvoke(app.ShowSettings);
    }

    private static void OpenHistoryWindow()
    {
        if (System.Windows.Application.Current is not CyberSnap.App app)
            return;

        if (app.Dispatcher.CheckAccess())
        {
            app.ShowHistory();
            return;
        }

        _ = app.Dispatcher.BeginInvoke(app.ShowHistory);
    }

    private void UpdateToolButtonState()
    {
        foreach (var kv in _toolButtons)
            kv.Value.Checked = kv.Key == _canvas.ActiveTool;

        _undoButton.Enabled = _canvas.CanUndo;
        _undoButton.Primary = _canvas.CanUndo;
        _redoButton.Enabled = _canvas.CanRedo;
        _redoButton.Primary = _canvas.CanRedo;
        _saveButton.Enabled = _canvas.IsDirty && !_canvas.IsDefaultBlank;
        _saveButton.Primary = _saveButton.Enabled;
        _pasteButton.Enabled = Clipboard.ContainsImage();
        UpdateColorSwatch();
        UpdateStrokeWidthButtons();
    }

    // Turns on double buffering for panels that get repainted every animation frame
    // (TableLayoutPanel ignores the public API, so it must be set via the protected member).
    private static void EnableDoubleBuffering(Control control)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(control, true);
    }

    private void UpdateZoomStatus()
    {
        var percent = (int)Math.Round(_canvas.Zoom * 100);
        var zoomText = $"{percent}%";
        if (_zoomLabel.Text != zoomText)
            _zoomLabel.Text = zoomText;

        _resetZoomBtn.Enabled = percent != 100;
        _fitZoomBtn.Enabled = !IsZoomFitted();

        if (_zoomSlider is null) return;

        var sliderValue = Math.Clamp(
            percent,
            AnnotationCanvas.MinZoomPercent,
            AnnotationCanvas.MaxZoomPercent);

        if (_zoomSlider.Value == sliderValue) return;
        _suppressZoomSliderChange = true;
        try
        {
            _zoomSlider.Value = sliderValue;
        }
        finally
        {
            _suppressZoomSliderChange = false;
        }
    }

    private bool IsZoomFitted()
    {
        var canvas = _canvas;
        var clientW = canvas.ClientSize.Width;
        var clientH = canvas.ClientSize.Height;
        if (clientW <= 0 || clientH <= 0) return false;
        var bmp = canvas.BaseBitmap;
        double sx = (double)clientW / bmp.Width;
        double sy = (double)clientH / bmp.Height;
        double fitZoom = Math.Clamp(Math.Min(sx, sy) * 0.95, 0.1, 8.0);
        return Math.Abs(canvas.Zoom - fitZoom) < 0.01;
    }

    private void UpdateColorSwatch()
    {
        foreach (var kv in _colorButtons)
            kv.Value.Checked = kv.Key.ToArgb() == _canvas.ToolColor.ToArgb();
    }

    private void UpdateStrokeWidthButtons()
    {
        foreach (var btn in _strokeWidthButtons)
            btn.Checked = Math.Abs(btn.StrokeWidth - _canvas.StrokeWidth) < 0.01f;
    }

    private void UpdateCaptureCaption()
    {
        if (_dimensionsLabel is null || _fileNameLabel is null) return;

        var bitmap = _canvas.BaseBitmap;
        var fileName = string.IsNullOrWhiteSpace(_savedFilePath)
            ? "Unsaved capture"
            : Path.GetFileName(_savedFilePath);
        
        var dimsText = $"{bitmap.Width} x {bitmap.Height}";
        if (_dimensionsLabel.Text != dimsText)
            _dimensionsLabel.Text = dimsText;
            
        if (_fileNameLabel.Text != fileName)
            _fileNameLabel.Text = fileName;

        if (_titleFileNameLabel != null && _titleFileNameLabel.Text != fileName)
            _titleFileNameLabel.Text = fileName;
    }

    private string GetCurrentHint()
    {
        return _canvas.ActiveTool switch
        {
            AnnotationCanvas.CanvasTool.Pan => "Pan",
            AnnotationCanvas.CanvasTool.Move => "Move & Resize",
            AnnotationCanvas.CanvasTool.Crop => "Crop",
            AnnotationCanvas.CanvasTool.Text => "Text",
            AnnotationCanvas.CanvasTool.Draw => "Draw",
            AnnotationCanvas.CanvasTool.Arrow => "Arrow",
            AnnotationCanvas.CanvasTool.CurvedArrow => "Curved arrow",
            AnnotationCanvas.CanvasTool.Line => "Line",
            AnnotationCanvas.CanvasTool.Rect => "Rectangle",
            AnnotationCanvas.CanvasTool.Circle => "Circle",
            AnnotationCanvas.CanvasTool.Eraser => "Eraser",
            AnnotationCanvas.CanvasTool.Highlight => "Highlight",
            AnnotationCanvas.CanvasTool.Blur => "Blur",
            AnnotationCanvas.CanvasTool.StepNumber => "Step Number",
            AnnotationCanvas.CanvasTool.Magnifier => "Magnifier",
            AnnotationCanvas.CanvasTool.Emoji => "Emoji",
            _ => "Ready",
        };
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        return EditorPaint.RoundedRect(rect, radius);
    }

    private ContextMenuStrip BuildBurgerMenu()
    {
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 260);

        var borderItem = WindowsMenuRenderer.Item("Border", iconId: null);
        var fitItem = WindowsMenuRenderer.Item("Auto-fit", iconId: null);
        var cropHandlesItem = WindowsMenuRenderer.Item("Crop handles", iconId: null);
        var bannersItem = WindowsMenuRenderer.Item("Show banners", iconId: null);
        var rulersItem = WindowsMenuRenderer.Item("Show rulers", iconId: null);
        var settingsItem = WindowsMenuRenderer.Item("Configuration", iconId: "gear");

        borderItem.Click += (_, _) =>
        {
            _toggleFrameSwitch.Checked = !_toggleFrameSwitch.Checked;
        };

        fitItem.Click += (_, _) =>
        {
            _toggleFitSwitch.Checked = !_toggleFitSwitch.Checked;
        };

        cropHandlesItem.Click += (_, _) =>
        {
            _canvas.EditorAutoCropControls = !_canvas.EditorAutoCropControls;
            if (System.Windows.Application.Current is CyberSnap.App app)
            {
                app.PersistEditorAutoCropControls(_canvas.EditorAutoCropControls);
            }
        };

        bannersItem.Click += (_, _) =>
        {
            _canvas.ShowBanners = !_canvas.ShowBanners;
            if (System.Windows.Application.Current is CyberSnap.App app)
            {
                app.PersistEditorShowBanners(_canvas.ShowBanners);
            }
        };

        rulersItem.Click += (_, _) =>
        {
            bool nextState = !(_topRulerContainer != null && _topRulerContainer.Visible);
            ToggleRulers(nextState);
            if (System.Windows.Application.Current is CyberSnap.App app)
            {
                app.PersistEditorShowRulers(nextState);
            }
        };

        settingsItem.Click += (_, _) => OpenSettingsWindow();

        menu.Items.Add(borderItem);
        menu.Items.Add(fitItem);
        menu.Items.Add(cropHandlesItem);
        menu.Items.Add(bannersItem);
        menu.Items.Add(rulersItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);

        menu.Opened += (_, _) =>
        {
            UpdateBurgerCheckmarks(borderItem, fitItem, cropHandlesItem, bannersItem, rulersItem);
        };

        WindowsMenuRenderer.NormalizeItemWidths(menu);
        return menu;
    }

    private void UpdateBurgerCheckmarks(ToolStripMenuItem borderItem, ToolStripMenuItem fitItem, ToolStripMenuItem cropHandlesItem, ToolStripMenuItem bannersItem, ToolStripMenuItem rulersItem)
    {
        var activeColor = Color.FromArgb(255, UiChrome.SurfaceTextPrimary.R, UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B);
        borderItem.Image = _toggleFrameSwitch.Checked ? FluentIcons.RenderBitmap("check", activeColor, 20, true) : null;
        fitItem.Image = _toggleFitSwitch.Checked ? FluentIcons.RenderBitmap("check", activeColor, 20, true) : null;
        cropHandlesItem.Image = _canvas.EditorAutoCropControls ? FluentIcons.RenderBitmap("check", activeColor, 20, true) : null;
        bannersItem.Image = _canvas.ShowBanners ? FluentIcons.RenderBitmap("check", activeColor, 20, true) : null;
        rulersItem.Image = (_topRulerContainer != null && _topRulerContainer.Visible) ? FluentIcons.RenderBitmap("check", activeColor, 20, true) : null;
    }
}

internal static class EditorColors
{
    public static readonly Color BgPrimary = Color.FromArgb(13, 15, 23);
    public static readonly Color BgSecondary = Color.FromArgb(18, 20, 31);
    public static readonly Color BgCard = Color.FromArgb(23, 26, 40);
    public static readonly Color BgHover = Color.FromArgb(33, 38, 58);
    public static readonly Color CanvasBg = Color.FromArgb(8, 10, 16);
    public static readonly Color TitleBar = Color.FromArgb(6, 12, 20);
    public static readonly Color TextPrimary = Color.FromArgb(230, 240, 255);
    public static readonly Color TextSecondary = Color.FromArgb(160, 180, 210);
    public static readonly Color TextMuted = Color.FromArgb(110, 130, 160);
    public static readonly Color Accent = Color.FromArgb(0, 255, 255);
    public static readonly Color AccentPressed = Color.FromArgb(0, 210, 230);
    public static readonly Color Border = Color.FromArgb(76, 0, 255, 255);
    public static readonly Color BorderSubtle = Color.FromArgb(34, 0, 255, 255);
    public static readonly Color WindowBorder = Color.FromArgb(75, 0, 255, 255);
}

internal sealed class EditorWindowFrame : DoubleBufferedPanel
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Inset uniformly so the cyan frame stays concentric with the radius-12 window clip
        // (inner radius = outer radius - inset), running parallel to the rounded edge.
        int inset = EditorPaint.WindowFrameInset;
        var rect = new Rectangle(inset, inset, Width - 2 * inset, Height - 2 * inset);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        using var border = new Pen(EditorColors.WindowBorder, 1.0f);
        using var path = EditorPaint.RoundedRect(rect, EditorPaint.WindowCornerRadius - inset);
        e.Graphics.DrawPath(border, path);
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    protected override void WndProc(ref Message m)
    {
        // This panel (Dock.Fill, Padding = ResizeHitSize) owns the outer resize ring, so no inner
        // child sits there. Hand any hit-test inside that ring back to the parent EditorForm by
        // returning HTTRANSPARENT; the form's WndProc then maps it to HTLEFT/HTTOP/... and Windows
        // shows the resize cursors and runs the native resize loop.
        if (m.Msg == WM_NCHITTEST)
        {
            var p = PointToClient(Cursor.Position);
            int edge = EditorPaint.ResizeHitSize;
            bool inResizeRing = p.X < edge || p.X >= Width - edge || p.Y < edge || p.Y >= Height - edge;
            if (inResizeRing)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
        }

        base.WndProc(ref m);
    }
}

internal sealed class EditorCanvasFrame : Panel
{
    public EditorCanvasFrame()
    {
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(
            Math.Max(6, Padding.Left - 8),
            Math.Max(6, Padding.Top - 8),
            Math.Max(1, Width - Padding.Horizontal + 16),
            Math.Max(1, Height - Padding.Vertical + 16));

        using var glow = new Pen(Color.FromArgb(34, EditorColors.Accent), 5f);
        using var border = new Pen(EditorColors.Border);
        using var path = EditorPaint.RoundedRect(rect, 8);
        e.Graphics.DrawPath(glow, path);
        e.Graphics.DrawPath(border, path);
    }
}

internal class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }
}

internal sealed class DoubleBufferedLabel : Label
{
    public DoubleBufferedLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var flags = TextFormatFlags.NoPrefix;
        if (AutoSize)
        {
            flags |= TextFormatFlags.SingleLine;
        }
        else
        {
            flags |= TextFormatFlags.WordBreak;
        }

        var size = TextRenderer.MeasureText(
            Text,
            Font,
            proposedSize,
            flags);

        size.Width += Padding.Horizontal;
        size.Height += Padding.Vertical;
        return size;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var backColor = BackColor;
        if (backColor == Color.Transparent)
        {
            Control? p = Parent;
            while (p != null && p.BackColor == Color.Transparent)
            {
                p = p.Parent;
            }
            if (p != null)
            {
                backColor = p.BackColor;
            }
        }
        if (backColor == Color.Transparent)
        {
            backColor = EditorColors.BgSecondary;
        }

        using (var brush = new SolidBrush(backColor))
        {
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        switch (TextAlign)
        {
            case ContentAlignment.TopLeft: flags |= TextFormatFlags.Top | TextFormatFlags.Left; break;
            case ContentAlignment.TopCenter: flags |= TextFormatFlags.Top | TextFormatFlags.HorizontalCenter; break;
            case ContentAlignment.TopRight: flags |= TextFormatFlags.Top | TextFormatFlags.Right; break;
            case ContentAlignment.MiddleLeft: flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.Left; break;
            case ContentAlignment.MiddleCenter: flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter; break;
            case ContentAlignment.MiddleRight: flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.Right; break;
            case ContentAlignment.BottomLeft: flags |= TextFormatFlags.Bottom | TextFormatFlags.Left; break;
            case ContentAlignment.BottomCenter: flags |= TextFormatFlags.Bottom | TextFormatFlags.HorizontalCenter; break;
            case ContentAlignment.BottomRight: flags |= TextFormatFlags.Bottom | TextFormatFlags.Right; break;
        }

        if (AutoSize)
        {
            flags |= TextFormatFlags.SingleLine;
        }
        else
        {
            flags |= TextFormatFlags.WordBreak;
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            flags);
    }
}

internal sealed class EditorZoomHostPanel : TableLayoutPanel
{
    public EditorZoomHostPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var fill = new SolidBrush(EditorColors.TitleBar);
        e.Graphics.FillRectangle(fill, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        using var path = EditorPaint.RoundedRect(rect, 8);
        using var glow = new Pen(Color.FromArgb(42, EditorColors.Accent), 3f);
        using var border = new Pen(EditorColors.Border);
        e.Graphics.DrawPath(glow, path);
        e.Graphics.DrawPath(border, path);
    }
}

internal sealed class EditorToolButton : EditorButtonBase
{
    // Slightly-elevated graphite resting fill so the tool buttons lift off the darker
    // panel instead of blending into it, and read well against their cyan borders.
    protected override Color IdleFill => Color.FromArgb(0x1C, 0x20, 0x30);

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => IsSelected;
        set => IsSelected = value;
    }

    protected override void PaintContent(Graphics g, Rectangle rect, Color contentColor, bool active)
    {
        var iconSize = Math.Min(52, Math.Max(42, rect.Height - 42));
        var iconRect = new RectangleF(
            rect.Left + (rect.Width - iconSize) / 2f,
            rect.Top + 10,
            iconSize,
            iconSize);
        StreamlineIcons.DrawIcon(g, IconId, iconRect, contentColor, 0f, active);

        var textRect = new Rectangle(rect.Left + 8, rect.Bottom - 28, rect.Width - 16, 24);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            textRect,
            contentColor,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }
}


internal sealed class EditorCommandButton : Button
{
    private bool _hover;
    private bool _pressed;
    private bool _primary;
    private string _iconId = "";

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary
    {
        get => _primary;
        set
        {
            if (_primary == value) return;
            _primary = value;
            Invalidate();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconId
    {
        get => _iconId;
        set
        {
            if (_iconId == value) return;
            _iconId = value;
            Invalidate();
        }
    }

    public EditorCommandButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Variable Text", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var parentBackColor = Parent?.BackColor ?? EditorColors.TitleBar;
        if (parentBackColor == Color.Transparent)
        {
            Control? p = Parent;
            while (p != null && p.BackColor == Color.Transparent)
            {
                p = p.Parent;
            }
            if (p != null)
            {
                parentBackColor = p.BackColor;
            }
        }
        if (parentBackColor == Color.Transparent)
        {
            parentBackColor = EditorColors.TitleBar;
        }
        g.Clear(parentBackColor);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        Color fill = Color.Transparent;
        Color contentColor = Enabled ? EditorColors.TextPrimary : Color.FromArgb(88, 105, 128);

        if (Enabled)
        {
            if (_pressed)
            {
                fill = Color.FromArgb(36, EditorColors.Accent);
                contentColor = EditorColors.Accent;
            }
            else if (_hover)
            {
                fill = Color.FromArgb(20, EditorColors.Accent);
                contentColor = EditorColors.Accent;
            }
            else if (Primary)
            {
                contentColor = EditorColors.Accent;
            }
        }

        if (fill != Color.Transparent)
        {
            using (var path = EditorPaint.RoundedRect(rect, 6))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }
        }

        if (Enabled && (_hover || _pressed || Primary))
        {
            using var underlineBrush = new SolidBrush(EditorColors.Accent);
            g.FillRectangle(underlineBrush, rect.Left + 12, rect.Bottom - 1, rect.Width - 24, 2);
        }

        var iconSize = 34;
        var iconRect = new RectangleF(
            rect.Left + (rect.Width - iconSize) / 2f,
            rect.Top + 3,
            iconSize,
            iconSize);
        StreamlineIcons.DrawIcon(g, IconId, iconRect, contentColor, 0f, Enabled && (_hover || _pressed || Primary));

        var textRect = new Rectangle(rect.Left + 2, rect.Top + 40, rect.Width - 4, 20);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            textRect,
            contentColor,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }
}


internal sealed class EditorChromeButton : EditorButtonBase
{
    protected override Color DefaultTransparentBackColor => EditorColors.TitleBar;

    protected override Color IdleFill => Color.Transparent;

    protected override Color ResolveBorder(bool active)
    {
        if (active || _hover)
            return base.ResolveBorder(active);
        return Color.Transparent;
    }

    protected override void PaintContent(Graphics g, Rectangle rect, Color contentColor, bool active)
    {
        var iconSize = Math.Min(24, Math.Max(20, rect.Height - 18));
        var iconRect = new RectangleF(
            rect.Left + (rect.Width - iconSize) / 2f,
            rect.Top + (rect.Height - iconSize) / 2f,
            iconSize,
            iconSize);
        StreamlineIcons.DrawIcon(g, IconId, iconRect, contentColor, 0f, active);
    }
}

internal abstract class EditorButtonBase : Button
{
    protected bool _hover;
    private bool _pressed;
    private bool _selected;
    private string _iconId = "";

    protected EditorButtonBase()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = EditorColors.TextPrimary;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Variable Text", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
        TabStop = true;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconId
    {
        get => _iconId;
        set
        {
            if (_iconId == value) return;
            _iconId = value;
            Invalidate();
        }
    }

    protected bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    protected virtual bool UsePrimaryFill => false;

    protected virtual Color DefaultTransparentBackColor => EditorColors.BgSecondary;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var parentBackColor = Parent?.BackColor ?? EditorColors.BgSecondary;
        g.Clear(parentBackColor == Color.Transparent ? DefaultTransparentBackColor : parentBackColor);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        bool active = Enabled && (IsSelected || UsePrimaryFill);
        var fill = ResolveFill(active);
        var border = ResolveBorder(active);
        var content = ResolveContent(active);

        using (var path = EditorPaint.RoundedRect(rect, 8))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border, active ? 1.4f : 1f))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        if (IsSelected && Enabled)
        {
            using var strip = new SolidBrush(EditorColors.Accent);
            using var stripPath = EditorPaint.RoundedRect(new Rectangle(5, 8, 3, Height - 16), 2);
            g.FillPath(strip, stripPath);
        }

        PaintContent(g, rect, content, active);
    }

    protected abstract void PaintContent(Graphics g, Rectangle rect, Color contentColor, bool active);

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    private Color ResolveFill(bool active)
    {
        if (!Enabled)
            return Color.FromArgb(16, 18, 28);
        if (UsePrimaryFill)
            return _pressed ? EditorColors.AccentPressed : EditorColors.Accent;
        if (IsSelected)
            return _pressed
                ? Color.FromArgb(42, 0, 255, 255)
                : Color.FromArgb(30, 0, 255, 255);
        if (_pressed)
            return Color.FromArgb(44, 50, 74);
        if (_hover)
            return EditorColors.BgHover;
        return IdleFill;
    }

    // Background fill in the resting state (enabled, not selected/hover/pressed).
    // Exposed as virtual so individual button kinds can override just their idle look.
    protected virtual Color IdleFill =>
        ResolveEffectiveParentBackColor() == EditorColors.TitleBar
            ? Color.FromArgb(18, 0, 255, 255)
            : EditorColors.BgCard;

    private Color ResolveEffectiveParentBackColor()
    {
        var parentBackColor = Parent?.BackColor ?? EditorColors.BgSecondary;
        return parentBackColor == Color.Transparent ? DefaultTransparentBackColor : parentBackColor;
    }

    protected virtual Color ResolveBorder(bool active)
    {
        if (!Enabled)
            return Color.FromArgb(26, 255, 255, 255);
        if (active || _hover)
            return Color.FromArgb(150, EditorColors.Accent);
        return EditorColors.BorderSubtle;
    }

    private Color ResolveContent(bool active)
    {
        if (!Enabled)
            return Color.FromArgb(88, 105, 128);
        if (UsePrimaryFill)
            return Color.FromArgb(4, 20, 26);
        if (active || _hover)
            return EditorColors.Accent;
        return EditorColors.TextPrimary;
    }
}

internal sealed class EditorColorButton : Button
{
    private bool _hover;
    private bool _checked;

    public EditorColorButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SwatchColor { get; set; } = EditorColors.Accent;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var backColor = Parent?.BackColor ?? EditorColors.BgSecondary;
        if (backColor == Color.Transparent)
        {
            Control? p = Parent;
            while (p != null && p.BackColor == Color.Transparent)
            {
                p = p.Parent;
            }
            if (p != null)
            {
                backColor = p.BackColor;
            }
        }
        if (backColor == Color.Transparent)
        {
            backColor = EditorColors.BgSecondary;
        }
        g.Clear(backColor);

        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var outerPath = EditorPaint.RoundedRect(outer, 7))
        using (var outerFill = new SolidBrush(_hover || Checked ? EditorColors.BgHover : EditorColors.BgCard))
        using (var outerPen = new Pen(Checked ? EditorColors.Accent : EditorColors.BorderSubtle, Checked ? 1.6f : 1f))
        {
            g.FillPath(outerFill, outerPath);
            g.DrawPath(outerPen, outerPath);
        }

        var inner = Rectangle.Inflate(outer, -6, -6);
        using var innerPath = EditorPaint.RoundedRect(inner, 5);
        using var swatch = new SolidBrush(SwatchColor);
        using var swatchPen = new Pen(Color.FromArgb(140, 255, 255, 255));
        g.FillPath(swatch, innerPath);
        g.DrawPath(swatchPen, innerPath);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }
}

internal sealed class EditorStrokeWidthButton : Button
{
    private bool _hover;
    private bool _checked;

    public EditorStrokeWidthButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float StrokeWidth { get; set; } = 4f;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var backColor = Parent?.BackColor ?? EditorColors.BgSecondary;
        if (backColor == Color.Transparent)
        {
            Control? p = Parent;
            while (p != null && p.BackColor == Color.Transparent)
            {
                p = p.Parent;
            }
            if (p != null)
            {
                backColor = p.BackColor;
            }
        }
        if (backColor == Color.Transparent)
        {
            backColor = EditorColors.BgSecondary;
        }
        g.Clear(backColor);

        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var outerPath = EditorPaint.RoundedRect(outer, 7))
        using (var outerFill = new SolidBrush(_hover || Checked ? EditorColors.BgHover : EditorColors.BgCard))
        using (var outerPen = new Pen(Checked ? EditorColors.Accent : EditorColors.BorderSubtle, Checked ? 1.6f : 1f))
        {
            g.FillPath(outerFill, outerPath);
            g.DrawPath(outerPen, outerPath);
        }

        float lineY = outer.Y + outer.Height / 2f;
        float margin = 8f;
        float lineX1 = outer.X + margin;
        float lineX2 = outer.Right - margin;
        using (var pen = new Pen(EditorColors.TextPrimary, StrokeWidth))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(pen, lineX1, lineY, lineX2, lineY);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }
}

internal sealed class EditorZoomSlider : Control
{
    private bool _hover;
    private bool _dragging;
    private int _minimum = 10;
    private int _maximum = 800;
    private int _value = 100;

    public EditorZoomSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        Height = 34;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_maximum < _minimum) _maximum = _minimum;
            Value = Math.Clamp(_value, _minimum, _maximum);
            Invalidate();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            if (_minimum > _maximum) _minimum = _maximum;
            Value = Math.Clamp(_value, _minimum, _maximum);
            Invalidate();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (_value == clamped) return;
            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(EditorColors.TitleBar);

        var track = GetTrackRect();
        using (var trackPath = EditorPaint.RoundedRect(track, 4))
        using (var trackFill = new SolidBrush(Color.FromArgb(18, 0, 255, 255)))
        using (var trackGlow = new Pen(Color.FromArgb(42, EditorColors.Accent), 3f))
        using (var trackPen = new Pen(EditorColors.Border))
        {
            g.FillPath(trackFill, trackPath);
            g.DrawPath(trackGlow, trackPath);
            g.DrawPath(trackPen, trackPath);
        }

        var thumbX = GetThumbX();
        var fillRect = Rectangle.FromLTRB(track.Left, track.Top, thumbX, track.Bottom);
        if (fillRect.Width > 0)
        {
            using var fillPath = EditorPaint.RoundedRect(fillRect, 4);
            using var accent = new SolidBrush(Color.FromArgb(120, EditorColors.Accent));
            g.FillPath(accent, fillPath);
        }

        using var glow = new SolidBrush(Color.FromArgb(_hover || _dragging ? 80 : 44, EditorColors.Accent));
        using var thumbFill = new SolidBrush(EditorColors.Accent);
        using var thumbStroke = new Pen(Color.FromArgb(230, 4, 20, 26), 1.4f);
        var thumb = new Rectangle(thumbX - 8, track.Top - 5, 16, 16);
        g.FillEllipse(glow, thumb.X - 5, thumb.Y - 5, thumb.Width + 10, thumb.Height + 10);
        g.FillEllipse(thumbFill, thumb);
        g.DrawEllipse(thumbStroke, thumb);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        if (!_dragging)
            Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _dragging = true;
            Capture = true;
            SetValueFromX(e.X);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
            SetValueFromX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
            Capture = false;
            SetValueFromX(e.X);
            Invalidate();
        }
        base.OnMouseUp(e);
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left:
                Value -= e.Shift ? 25 : 5;
                e.Handled = true;
                break;
            case Keys.Right:
                Value += e.Shift ? 25 : 5;
                e.Handled = true;
                break;
            case Keys.Home:
                Value = Minimum;
                e.Handled = true;
                break;
            case Keys.End:
                Value = Maximum;
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    private Rectangle GetTrackRect()
        => new(10, (Height - 6) / 2, Math.Max(1, Width - 20), 6);

    // Zoom is perceived multiplicatively (the wheel steps by a constant factor), so the
    // thumb is positioned on a logarithmic scale. A linear mapping crammed the whole
    // fit-to-100% range into a sliver near the left edge for large images, making the
    // thumb appear stuck at the side while zooming. Log mapping spreads equal zoom
    // factors over equal track distance and centers 100% regardless of image size.
    private double ValueToFraction(int value)
    {
        double lmin = Math.Log(Math.Max(1, Minimum));
        double lmax = Math.Log(Math.Max(1, Maximum));
        if (lmax - lmin < 1e-9) return 0.0;
        double lval = Math.Log(Math.Clamp(value, Math.Max(1, Minimum), Math.Max(1, Maximum)));
        return (lval - lmin) / (lmax - lmin);
    }

    private int GetThumbX()
    {
        var track = GetTrackRect();
        return track.Left + (int)Math.Round(track.Width * ValueToFraction(Value));
    }

    private void SetValueFromX(int x)
    {
        var track = GetTrackRect();
        var t = Math.Clamp((x - track.Left) / (double)Math.Max(1, track.Width), 0.0, 1.0);
        double lmin = Math.Log(Math.Max(1, Minimum));
        double lmax = Math.Log(Math.Max(1, Maximum));
        Value = (int)Math.Round(Math.Exp(lmin + (lmax - lmin) * t));
    }
}

internal static class EditorPaint
{
    public const int WindowCornerRadius = 12;   // matches WPF Settings/Gallery root Border
    public const int WindowFrameInset = 2;      // uniform inset of the cyan accent frame
    public const int ResizeHitSize = 8;         // edge thickness that triggers window resize

    public static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(1, radius * 2);
        if (rect.Width <= diameter || rect.Height <= diameter)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class EditorZoomBarButton : Button
{
    private bool _hover;
    private bool _pressed;
    private string _iconId = "";

    public EditorZoomBarButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = EditorColors.Accent;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Variable Text", 10f, FontStyle.Bold, GraphicsUnit.Point);
        TabStop = true;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconId
    {
        get => _iconId;
        set
        {
            if (_iconId == value) return;
            _iconId = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var parentBackColor = Parent?.BackColor ?? EditorColors.TitleBar;
        g.Clear(parentBackColor == Color.Transparent ? EditorColors.TitleBar : parentBackColor);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Ghost-button styling that mirrors the top-bar command buttons: no chrome at rest,
        // a soft accent wash + underline on hover/press. Label stays inline (horizontal) to
        // respect the tighter vertical space of the status bar.
        Color fill = Color.Transparent;
        Color contentColor = Enabled ? EditorColors.TextPrimary : Color.FromArgb(88, 105, 128);

        if (Enabled)
        {
            if (_pressed)
            {
                fill = Color.FromArgb(36, EditorColors.Accent);
                contentColor = EditorColors.Accent;
            }
            else if (_hover)
            {
                fill = Color.FromArgb(20, EditorColors.Accent);
                contentColor = EditorColors.Accent;
            }
        }

        if (fill != Color.Transparent)
        {
            using var path = EditorPaint.RoundedRect(rect, 6);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        if (Enabled && (_hover || _pressed))
        {
            using var underlineBrush = new SolidBrush(EditorColors.Accent);
            g.FillRectangle(underlineBrush, rect.Left + 12, rect.Bottom - 1, rect.Width - 24, 2);
        }

        var iconSize = 22;
        bool iconOnly = string.IsNullOrEmpty(Text);
        float iconX = iconOnly
            ? rect.Left + (rect.Width - iconSize) / 2f
            : rect.Left + 12;
        var iconRect = new RectangleF(iconX, rect.Top + (rect.Height - iconSize) / 2f, iconSize, iconSize);
        StreamlineIcons.DrawIcon(g, IconId, iconRect, contentColor, 0f, Enabled && (_hover || _pressed));

        if (!iconOnly)
        {
            var textRect = new Rectangle(rect.Left + 40, rect.Top, rect.Width - 46, rect.Height);
            TextRenderer.DrawText(
                g,
                Text,
                Font,
                textRect,
                contentColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }
}

internal sealed class EditorToggleSwitch : Control
{
    private bool _checked;
    private float _animPercent = 0f;
    private System.Windows.Forms.Timer? _animTimer;

    public event EventHandler? CheckedChanged;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            StartAnimation();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string LabelText { get; set; } = string.Empty;

    // Pill geometry, shared by layout (GetPreferredSize) and painting.
    private const int PillWidth = 42;
    private const int PillHeight = 22;
    private const int LabelPillGap = 12;
    private const TextFormatFlags LabelFlags =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    public EditorToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        AutoSize = true;
    }

    private Font LabelFont => Font ?? new Font("Segoe UI Variable Text", 9f, FontStyle.Bold);

    public override Size GetPreferredSize(Size proposedSize)
    {
        int labelWidth = TextRenderer.MeasureText(LabelText, LabelFont, new Size(int.MaxValue, PillHeight), LabelFlags).Width;
        return new Size(labelWidth + LabelPillGap + PillWidth + 2, 42);
    }

    private void StartAnimation()
    {
        _animTimer?.Stop();
        _animTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _animTimer.Tick += (s, e) =>
        {
            float target = _checked ? 1f : 0f;
            if (Math.Abs(_animPercent - target) < 0.1f)
            {
                _animPercent = target;
                _animTimer?.Stop();
                _animTimer?.Dispose();
                _animTimer = null;
            }
            else
            {
                _animPercent += (_checked ? 0.15f : -0.15f);
                _animPercent = Math.Clamp(_animPercent, 0f, 1f);
            }
            Invalidate();
        };
        _animTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Label on the left, pill switch immediately after it (the control auto-sizes to fit both).
        var font = LabelFont;
        int labelWidth = TextRenderer.MeasureText(e.Graphics, LabelText, font, new Size(int.MaxValue, Height), LabelFlags).Width;
        TextRenderer.DrawText(e.Graphics, LabelText, font, new Rectangle(0, 0, labelWidth + 2, Height), EditorColors.TextPrimary, LabelFlags);

        const int swWidth = PillWidth;
        const int swHeight = PillHeight;
        int swX = labelWidth + LabelPillGap;
        int swY = (Height - swHeight) / 2;
        var swRect = new Rectangle(swX, swY, swWidth, swHeight);
        // Pill track: transparent (off) -> accent (on), with a muted -> accent outline.
        // Mirrors the rounded toggle switches used in the Settings window.
        using var path = EditorPaint.RoundedRect(swRect, swHeight / 2);

        var accent = EditorColors.Accent;
        using (var bgBrush = new SolidBrush(Color.FromArgb((int)(_animPercent * 255), accent)))
        {
            e.Graphics.FillPath(bgBrush, path);
        }

        Color borderColor = Lerp(Color.FromArgb(150, 132, 150, 178), accent, _animPercent);
        using (var borderPen = new Pen(borderColor, 1.4f))
        {
            e.Graphics.DrawPath(borderPen, path);
        }

        // Thumb: muted grey (off) -> dark (on), sliding with margins like the config toggle.
        const int knobSize = swHeight - 8; // 14px leaves breathing room around the thumb
        const float knobMargin = 4f;
        float knobMinX = swX + knobMargin;
        float knobMaxX = swX + swWidth - knobSize - knobMargin;
        float knobX = knobMinX + _animPercent * (knobMaxX - knobMinX);
        float knobY = swY + (swHeight - knobSize) / 2f;

        var knobRect = new RectangleF(knobX, knobY, knobSize, knobSize);
        using (var knobBrush = new SolidBrush(Lerp(EditorColors.TextSecondary, EditorColors.BgPrimary, _animPercent)))
        {
            e.Graphics.FillEllipse(knobBrush, knobRect);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + t * (b.A - a.A)),
            (int)(a.R + t * (b.R - a.R)),
            (int)(a.G + t * (b.G - a.G)),
            (int)(a.B + t * (b.B - a.B)));
    }
}

