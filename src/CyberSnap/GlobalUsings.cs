// Resolve ambiguity between WPF and WinForms types.
// Default to WPF types since this is a WPF app.
global using Application = System.Windows.Application;
global using ContextMenu = System.Windows.Controls.ContextMenu;
global using MenuItem = System.Windows.Controls.MenuItem;
global using MessageBox = System.Windows.MessageBox;
