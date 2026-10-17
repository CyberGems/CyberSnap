using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI;

/// <summary>Shared result-window tab navigation. Each window owns its result state.</summary>
internal sealed class ResultTabHost<T> where T : class
{
    private readonly Window _window;
    private readonly PreviewTabStrip _strip;
    private readonly Func<T, string> _title;
    private readonly Action<T> _capture;
    private readonly Action<T> _apply;
    private readonly Action<T> _release;
    private readonly List<T> _sessions = new();
    private T? _lastClosed;

    public T Active { get; private set; }
    public bool IsClosing { get; private set; }

    public ResultTabHost(Window window, PreviewTabStrip strip, T initial,
        Func<T, string> title, Action<T> capture, Action<T> apply, Action<T> release)
    {
        _window = window;
        _strip = strip;
        _title = title;
        _capture = capture;
        _apply = apply;
        _release = release;
        Active = initial;
        _sessions.Add(initial);
        strip.TabSelected += (_, index) => ActivateAt(index);
        strip.TabCloseRequested += (_, index) => CloseAt(index);
        strip.TabContextMenuRequested += (_, index) => ShowMenu(index);
        strip.BarContextMenuRequested += (_, _) => ShowMenu(_sessions.IndexOf(Active));
        window.Closing += OnClosing;
        window.Closed += (_, _) =>
        {
            IsClosing = true;
            foreach (var session in _sessions)
                _release(session);
            _sessions.Clear();
            if (_lastClosed is not null)
                _release(_lastClosed);
            _lastClosed = null;
        };
        Refresh();
    }

    public void Add(T session)
    {
        if (IsClosing)
            throw new InvalidOperationException("The result window is closing.");
        _capture(Active);
        _sessions.Add(session);
        Active = session;
        _apply(session);
        Refresh();
    }

    private void ActivateAt(int index)
    {
        if (IsClosing || index < 0 || index >= _sessions.Count || ReferenceEquals(Active, _sessions[index]))
            return;
        _capture(Active);
        Active = _sessions[index];
        _apply(Active);
        Refresh();
    }

    public void CloseActive() => Close(Active);

    public void Close(T session) => CloseAt(_sessions.IndexOf(session));

    private void CloseAt(int index)
    {
        if (IsClosing || index < 0 || index >= _sessions.Count)
            return;
        if (_sessions.Count == 1)
        {
            _window.Close();
            return;
        }

        var session = _sessions[index];
        bool wasActive = ReferenceEquals(session, Active);
        if (wasActive)
            _capture(session);
        _sessions.RemoveAt(index);
        if (_lastClosed is not null)
            _release(_lastClosed);
        _lastClosed = session;
        // Cancel background work while retaining one result for reopening.
        _release(session);
        if (wasActive)
        {
            Active = _sessions[Math.Min(index, _sessions.Count - 1)];
            _apply(Active);
        }
        Refresh();
    }

    private void Reopen()
    {
        if (_lastClosed is null || IsClosing)
            return;
        var session = _lastClosed;
        _lastClosed = null;
        Add(session);
    }

    public void Refresh()
    {
        _strip.Visibility = _sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _strip.SetTabs(_sessions.Select(session =>
            new PreviewTabInfo(_title(session), ReferenceEquals(session, Active))).ToArray());
        _strip.RevealActiveTab();
    }

    public bool HandleHotkey(System.Windows.Input.KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.W)
            CloseActive();
        else if ((mods == ModifierKeys.Control || mods == (ModifierKeys.Control | ModifierKeys.Shift)) && e.Key == Key.Tab)
        {
            int delta = mods.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            ActivateAt((_sessions.IndexOf(Active) + delta + _sessions.Count) % _sessions.Count);
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.T)
            Reopen();
        else
            return false;
        e.Handled = true;
        return true;
    }

    private void ShowMenu(int index)
    {
        if (IsClosing || index < 0 || index >= _sessions.Count)
            return;
        var session = _sessions[index];
        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            Background = Theme.Brush(Theme.BgElevated),
            Foreground = Theme.Brush(Theme.TextPrimary),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };
        AddItem("Close tab", "Ctrl+W", () => Close(session));
        AddItem("Close all", "", () => _window.Close());
        if (_lastClosed is not null)
            AddItem("Reopen last closed tab", "Ctrl+Shift+T", Reopen);
        menu.IsOpen = true;

        void AddItem(string key, string gesture, Action action)
        {
            var item = new MenuItem { Header = LocalizationService.Translate(key), InputGestureText = gesture };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel)
            return;
        if (_sessions.Count > 1 && !ThemedConfirmDialog.Confirm(
            _window,
            LocalizationService.Translate("Close all results?"),
            string.Format(LocalizationService.Translate("This window contains {0} results. Close all tabs?"), _sessions.Count),
            LocalizationService.Translate("Close all"),
            LocalizationService.Translate("Cancel"),
            danger: false))
        {
            e.Cancel = true;
            return;
        }
        IsClosing = true;
    }
}
