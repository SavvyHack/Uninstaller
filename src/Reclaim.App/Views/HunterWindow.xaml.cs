using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Reclaim.App.Services;
using Reclaim.App.ViewModels;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.SystemTools;
using Reclaim.Core.Uninstall;

namespace Reclaim.App.Views;

/// <summary>Floating Hunter target. Drag it over anything on screen and release to act on the program behind it.</summary>
public partial class HunterWindow : Window
{
    private readonly CatalogService _catalog;
    private readonly UiService _ui;
    private bool _hunting;
    private DateTime _lastProbe;

    public HunterWindow(CatalogService catalog, UiService ui)
    {
        _catalog = catalog;
        _ui = ui;
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Top + 24;
    }

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Target_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _hunting = true;
        Target.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Cross;
        HoverLabel.Text = "Release over a target";
    }

    private void Target_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_hunting || (DateTime.Now - _lastProbe).TotalMilliseconds < 250) return;
        _lastProbe = DateTime.Now;
        GetCursorPos(out var p);
        var hit = HunterService.Identify(p.X, p.Y, _catalog.Models.ToList());
        HoverLabel.Text = hit?.App?.Name ?? (hit?.ExePath is { } exe ? Path.GetFileName(exe) : hit?.Title ?? "…");
    }

    private void Target_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_hunting) return;
        _hunting = false;
        Target.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        HoverLabel.Text = "Drag to aim";
        GetCursorPos(out var p);
        var hit = HunterService.Identify(p.X, p.Y, _catalog.Models.ToList());
        if (hit is null || (hit.ExePath is null && hit.App is null))
        {
            HoverLabel.Text = "Nothing recognised";
            return;
        }
        ShowActions(hit);
    }

    private void ShowActions(HuntResult hit)
    {
        var title = hit.App?.Name ?? (hit.ExePath is null ? hit.Title : Path.GetFileName(hit.ExePath));
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"{title}  ·  {hit.Source}", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());
        Add(menu, "Uninstall", () => Uninstall(hit, forced: false));
        Add(menu, "Stop starting with Windows", () => StopAutostart(hit), hit.ExePath is not null);
        Add(menu, "Close the program", () => Kill(hit), hit.ExePath is not null);
        Add(menu, "Close and remove it", () =>
        {
            Kill(hit);
            Uninstall(hit, forced: true);
        }, hit.ExePath is not null);
        menu.Items.Add(new Separator());
        Add(menu, "Open containing folder", () => ProcessRunner.RevealInExplorer(hit.ExePath!), hit.ExePath is not null);
        Add(menu, "Search online", () => ProcessRunner.SearchOnline(hit.App?.Name ?? title));
        Add(menu, "Cancel", () => { });
        menu.PlacementTarget = Target;
        menu.IsOpen = true;
    }

    private static void Add(ContextMenu menu, string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void Uninstall(HuntResult hit, bool forced)
    {
        var app = forced || hit.App is null ? ForcedUninstall.Create(hit.ExePath ?? hit.Title, _catalog.Models) : hit.App;
        OpenMain();
        _ui.ShowSheet(new UninstallWizardViewModel([app], forced || hit.App is null ? WizardMode.Forced : WizardMode.Normal, _catalog, _ui));
    }

    private void StopAutostart(HuntResult hit)
    {
        var entries = AutorunManager.List().Where(e => PathUtil.SamePath(e.ExePath, hit.ExePath) && e.Enabled).ToList();
        foreach (var e in entries)
        {
            try { AutorunManager.SetEnabled(e, false); } catch { }
        }
        HoverLabel.Text = entries.Count == 0 ? "Doesn't auto-start" : "Auto-start disabled";
    }

    private void Kill(HuntResult hit)
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == hit.ProcessId || PathUtil.SamePath(Native_ImagePath(p), hit.ExePath)) p.Kill(true);
            }
            catch { }
        }
    }

    private static string? Native_ImagePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    private void OpenMain()
    {
        if (Application.Current.MainWindow is { } w)
        {
            w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        }
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        OpenMain();
        Close();
    }

    private void Topmost_Click(object sender, RoutedEventArgs e) => Topmost = TopmostItem.IsChecked;

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        OpenMain();
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);
}
