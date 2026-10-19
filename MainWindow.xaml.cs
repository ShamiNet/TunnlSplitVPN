using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TunnlSplitVPN.Models;
using TunnlSplitVPN.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Forms = System.Windows.Forms;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;

namespace TunnlSplitVPN;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LogEntry> _logs = new();
    private readonly ObservableCollection<string> _includeApps = new();
    private readonly ObservableCollection<string> _excludeApps = new();
    private readonly ObservableCollection<TunnelProfile> _tunnels = new();
    private readonly WireSockController _controller = new();
    private readonly TunnelStore _store = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly HashSet<Process> _requestedStopProcesses = [];
    private readonly Dictionary<Process, string> _runtimeConfigs = [];
    private readonly DispatcherTimer _dnsSaveTimer;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Forms.NotifyIcon _trayIcon;

    private AppSettings _settings = new();
    private Process? _vpnProcess;
    private string? _activeTunnelName;
    private bool _isReady;
    private bool _suspendRuleEvents;
    private bool _closeAfterStop;
    private bool _elevationRelaunch;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        _trayMenu = new Forms.ContextMenuStrip();
        var openMenuItem = new Forms.ToolStripMenuItem("Open TunnlSplitVPN");
        openMenuItem.Click += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
        _trayMenu.Items.Add(openMenuItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitApplication();
        _trayMenu.Items.Add(exitMenuItem);
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "TunnlSplitVPN",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);

        DataContext = this;
        TunnelCombo.ItemsSource = _tunnels;
        IncludeApps = _includeApps;
        ExcludeApps = _excludeApps;
        Tunnels = _tunnels;
        LogItems.ItemsSource = _logs;

        _includeApps.CollectionChanged += Rules_Changed;
        _excludeApps.CollectionChanged += Rules_Changed;
        _controller.OutputReceived += Controller_OutputReceived;
        _controller.ProcessExited += Controller_ProcessExited;

        _dnsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _dnsSaveTimer.Tick += DnsSaveTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    public ObservableCollection<string> IncludeApps { get; }
    public ObservableCollection<string> ExcludeApps { get; }
    public ObservableCollection<TunnelProfile> Tunnels { get; }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = _store.LoadSettings();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AddLog("Settings", $"Could not load settings: {exception.Message}", "red");
            MessageBox.Show(this, $"Settings could not be loaded:\n{exception.Message}",
                "Settings error", MessageBoxButton.OK, MessageBoxImage.Error);
            _settings = new AppSettings();
        }

        _settings.TunnelRules ??= new Dictionary<string, TunnelRules>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<TunnelProfile> profiles;
        IReadOnlyList<string> loadErrors;
        try
        {
            profiles = _store.LoadTunnels(out loadErrors);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AddLog("Tunnel", $"Could not load saved tunnels: {exception.Message}", "red");
            MessageBox.Show(this, $"Saved tunnels could not be loaded:\n{exception.Message}",
                "Tunnel storage error", MessageBoxButton.OK, MessageBoxImage.Error);
            profiles = [];
            loadErrors = [];
        }

        foreach (var profile in profiles)
            _tunnels.Add(profile);

        foreach (var error in loadErrors)
            AddLog("Tunnel", $"Skipped invalid saved tunnel: {error}", "orange");

        _isReady = true;
        TunnelCombo.SelectedItem = _tunnels.FirstOrDefault(tunnel =>
            string.Equals(tunnel.Name, _settings.SelectedTunnel, StringComparison.OrdinalIgnoreCase));

        var preferred = _settings.WireSockPath;
        var client = _controller.FindClientBinary(preferred);
        ClientPathText.Text = client is null
            ? "WireSock client: not detected — use Locate WireSock"
            : $"WireSock client: {client}";
        AdminStatus.Text = IsAdministrator()
            ? "Administrator"
            : "Standard user — elevation is required to start a tunnel";
        AdminStatus.Foreground = IsAdministrator()
            ? new SolidColorBrush(Color.FromRgb(21, 128, 61))
            : new SolidColorBrush(Color.FromRgb(180, 83, 9));

        if (profiles.Count == 0)
        {
            SetStatus("Disconnected", "Import a valid WireGuard .conf file to begin.", "#8290A3");
            AddLog("Service", "No valid saved tunnels. Import a WireGuard configuration to begin.", "orange");
        }
        else if (TunnelCombo.SelectedItem is null)
        {
            TunnelCombo.SelectedIndex = 0;
        }

        if (client is null)
            AddLog("Service", "WireSock client was not found. Choose its executable before starting.", "orange");
    }

    private void TunnelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isReady || TunnelCombo.SelectedItem is not TunnelProfile profile)
            return;

        if (_activeTunnelName is not null)
            SaveRules(_activeTunnelName);
        _settings.SelectedTunnel = profile.Name;
        var rules = GetRules(profile.Name);

        _suspendRuleEvents = true;
        try
        {
            _includeApps.Clear();
            _excludeApps.Clear();
            foreach (var app in rules.IncludeApps)
                _includeApps.Add(app);
            foreach (var app in rules.ExcludeApps)
                _excludeApps.Add(app);

            UseTunnelDnsCheck.IsChecked = rules.UseTunnelDns;
            CustomDnsText.Text = rules.CustomDns;
            CustomDnsText.IsEnabled = rules.UseTunnelDns;
        }
        finally
        {
            _suspendRuleEvents = false;
        }

        AddressText.Text = profile.Addresses;
        var previousTunnel = _activeTunnelName;
        _activeTunnelName = profile.Name;
        SaveSettings();
        if (_vpnProcess is not null && previousTunnel is not null &&
            !previousTunnel.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            _ = RestartActiveTunnelAsync();
        }
    }

    private void ImportTunnel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a WireGuard configuration",
            Filter = "WireGuard configuration (*.conf)|*.conf",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var profile = _store.Import(dialog.FileName);
            var existing = _tunnels.FirstOrDefault(tunnel =>
                tunnel.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                _tunnels.Remove(existing);
            _tunnels.Add(profile);
            TunnelCombo.SelectedItem = profile;
            AddLog("Tunnel", $"Imported and validated tunnel '{profile.Name}'.", "green");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            AddLog("Service", $"Tunnel import failed: {exception.Message}", "red");
            MessageBox.Show(this, exception.Message, "Tunnel import failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LocateWireSock_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate wiresock-client.exe",
            Filter = "WireSock client (wiresock-client.exe)|wiresock-client.exe|Executable (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        _settings.WireSockPath = dialog.FileName;
        SaveSettings();
        ClientPathText.Text = $"WireSock client: {dialog.FileName}";
        AddLog("Service", "Updated the selected WireSock executable path.", "gray");
    }

    private void AddRunningInclude_Click(object sender, RoutedEventArgs e) =>
        AddSelectedRunningApps(toInclude: true);

    private void AddRunningExclude_Click(object sender, RoutedEventArgs e) =>
        AddSelectedRunningApps(toInclude: false);

    private void AddSelectedRunningApps(bool toInclude)
    {
        var choices = GetRunningApplications(out var unresolvedPaths);
        if (unresolvedPaths > 0)
        {
            AddLog("Applications",
                $"Windows did not expose the executable path for {unresolvedPaths} process(es); process names are listed instead.",
                "orange");
        }

        if (choices.Count == 0)
        {
            MessageBox.Show(this, "No running applications could be listed.", "Running applications",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new Window
        {
            Title = "Select running applications",
            Owner = this,
            Width = 650,
            Height = 520,
            MinWidth = 500,
            MinHeight = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(16, 16, 20)),
            Foreground = Brushes.White
        };
        var layout = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var addButton = new Button { Content = "Add selected", IsDefault = true, Padding = new Thickness(14, 7, 14, 7) };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 7, 14, 7) };
        buttons.Children.Add(addButton);
        buttons.Children.Add(cancelButton);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);

        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(RunningApplication.Display),
            SelectionMode = SelectionMode.Extended,
            Background = new SolidColorBrush(Color.FromRgb(13, 13, 17)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(41, 41, 51))
        };
        layout.Children.Add(list);
        addButton.Click += (_, _) => picker.DialogResult = true;
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is not null)
                picker.DialogResult = true;
        };
        picker.Content = layout;

        if (picker.ShowDialog() != true)
            return;

        var selectedApps = list.SelectedItems.Cast<RunningApplication>()
            .Select(application => application.Path)
            .ToList();
        AddApps(selectedApps, toInclude);
    }

    private static List<RunningApplication> GetRunningApplications(out int unresolvedPaths)
    {
        unresolvedPaths = 0;
        var byPath = new Dictionary<string, RunningApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    unresolvedPaths++;
                    continue;
                }

                string? executablePath = null;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    unresolvedPaths++;
                }
                catch (InvalidOperationException)
                {
                    unresolvedPaths++;
                }
                catch (NotSupportedException)
                {
                    unresolvedPaths++;
                }

                if (string.IsNullOrWhiteSpace(executablePath))
                    executablePath = processName + ".exe";
                if (executablePath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                byPath.TryAdd(executablePath, new RunningApplication(processName, executablePath));
            }
        }

        return byPath.Values.OrderBy(application => application.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void BrowseInclude_Click(object sender, RoutedEventArgs e) => BrowseExecutable(toInclude: true);
    private void BrowseExclude_Click(object sender, RoutedEventArgs e) => BrowseExecutable(toInclude: false);

    private void BrowseExecutable(bool toInclude)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an application",
            Filter = "Windows application (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
            AddApps(dialog.FileNames, toInclude);
    }

    private void AddApps(IEnumerable<string> apps, bool toInclude)
    {
        var target = toInclude ? _includeApps : _excludeApps;
        var opposite = toInclude ? _excludeApps : _includeApps;
        _suspendRuleEvents = true;
        try
        {
            foreach (var app in apps)
            {
                var normalized = app.Trim().Trim('"', '\'');
                if (normalized.Length == 0)
                    continue;

                var duplicate = opposite.FirstOrDefault(existing =>
                    existing.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (duplicate is not null)
                    opposite.Remove(duplicate);
                if (!target.Any(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                    target.Add(normalized);
            }
        }
        finally
        {
            _suspendRuleEvents = false;
        }

        Rules_Changed(this, EventArgs.Empty);
    }

    private void RemoveInclude_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedApp(IncludeListBox, _includeApps);

    private void RemoveExclude_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedApp(ExcludeListBox, _excludeApps);

    private void RemoveSelectedApp(System.Windows.Controls.ListBox list, ObservableCollection<string> apps)
    {
        if (list.SelectedItem is string app)
            apps.Remove(app);
    }

    private void Rules_Changed(object? sender, EventArgs e)
    {
        if (!_isReady || _suspendRuleEvents)
            return;

        SaveCurrentRules();
        SaveSettings();
        if (_vpnProcess is not null)
            _ = RestartActiveTunnelAsync();
    }

    private void DnsSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _suspendRuleEvents)
            return;

        CustomDnsText.IsEnabled = UseTunnelDnsCheck.IsChecked == true;
        SaveCurrentRules();
        SaveSettings();
        if (_vpnProcess is not null)
            _ = RestartActiveTunnelAsync();
    }

    private void CustomDns_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isReady || _suspendRuleEvents)
            return;

        _dnsSaveTimer.Stop();
        _dnsSaveTimer.Start();
    }

    private void DnsSaveTimer_Tick(object? sender, EventArgs e)
    {
        _dnsSaveTimer.Stop();
        SaveCurrentRules();
        SaveSettings();
        if (_vpnProcess is not null)
            _ = RestartActiveTunnelAsync();
    }

    private async void ToggleConnection_Click(object sender, RoutedEventArgs e)
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_vpnProcess is null)
                await StartTunnelCoreAsync();
            else
                await StopTunnelCoreAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RestartActiveTunnelAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_vpnProcess is null)
                return;

            await StopTunnelCoreAsync();
            await StartTunnelCoreAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task StartTunnelCoreAsync()
    {
        if (TunnelCombo.SelectedItem is not TunnelProfile profile)
        {
            ShowOperationError("Select or import a valid tunnel before starting.");
            return;
        }

        if (!IsAdministrator())
        {
            RequestAdministratorRelaunch();
            return;
        }

        var executablePath = _controller.FindClientBinary(_settings.WireSockPath);
        if (executablePath is null)
        {
            ShowOperationError("WireSock was not found. Use 'Locate WireSock' to select wiresock-client.exe.");
            return;
        }

        var rules = GetRules(profile.Name);
        string runtimeConfig;
        string runtimePath;
        try
        {
            runtimeConfig = WireSockConfigBuilder.Build(
                profile.ConfigText, rules.IncludeApps, rules.ExcludeApps,
                rules.UseTunnelDns, rules.CustomDns);
            runtimePath = _store.WriteRuntimeConfig(profile.Name, runtimeConfig);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowOperationError(exception.Message);
            return;
        }

        try
        {
            var process = _controller.Start(executablePath, runtimePath);
            _runtimeConfigs.Add(process, runtimePath);
            _vpnProcess = process;
            _activeTunnelName = profile.Name;
            _settings.WireSockPath = executablePath;
            _settings.SelectedTunnel = profile.Name;
            SaveSettings();
            ClientPathText.Text = $"WireSock client: {executablePath}";
            ConnectionButton.Content = "Stop tunnel";
            SetStatus("Starting", "Waiting for WireSock output; process creation is not a connection confirmation.", "#B45309");
            AddLog("Service", $"Started WireSock process (PID {process.Id}) for '{profile.Name}'.", "orange");

            await Task.Delay(1400);
            if (ReferenceEquals(_vpnProcess, process) && !process.HasExited)
            {
                SetStatus("Process running — tunnel unverified",
                    "WireSock remains active, but this application cannot verify a successful handshake.", "#B45309");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            DeleteRuntimeConfig(runtimePath);
            _vpnProcess = null;
            ConnectionButton.Content = "Start tunnel";
            SetStatus("Start failed", exception.Message, "#B42318");
            AddLog("Service", $"Could not start WireSock: {exception.Message}", "red");
            MessageBox.Show(this, exception.Message, "WireSock start failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StopTunnelCoreAsync()
    {
        var process = _vpnProcess;
        if (process is null)
            return;

        _requestedStopProcesses.Add(process);
        SetStatus("Stopping", "Stopping only the WireSock process started by this application.", "#B45309");
        try
        {
            await Task.Run(() => WireSockController.Stop(process));
            if (ReferenceEquals(_vpnProcess, process))
                _vpnProcess = null;
            process.Dispose();
            DeleteRuntimeConfig(process);
            ConnectionButton.Content = "Start tunnel";
            SetStatus("Disconnected", "The application-owned WireSock process has stopped.", "#8290A3");
            AddLog("Service", "Stopped the WireSock process started by this application.", "gray");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or TimeoutException)
        {
            AddLog("Service", $"Could not stop the owned WireSock process: {exception.Message}", "red");
            MessageBox.Show(this, exception.Message, "WireSock stop failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_closeAfterStop && _vpnProcess is null)
        {
            _closeAfterStop = false;
            Close();
        }
    }

    private void RequestAdministratorRelaunch()
    {
        var result = MessageBox.Show(this,
            "WireSock needs administrator privileges to configure tunnel routing. Restart this application as administrator?",
            "Administrator privileges required", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            AddLog("Service", "Tunnel start cancelled because administrator privileges were not granted.", "orange");
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            ShowOperationError("The application executable could not be located for elevation.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas"
            });
            _elevationRelaunch = true;
            Close();
        }
        catch (Win32Exception exception)
        {
            if (exception.NativeErrorCode == 1223)
            {
                AddLog("Service", "Administrator elevation was cancelled.", "orange");
                return;
            }

            ShowOperationError($"Could not restart as administrator: {exception.Message}");
        }
    }

    private void Controller_OutputReceived(string line)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            var level = line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                ? "red"
                : "gray";
            AddLog("WireSock", line, level);
        });
    }

    private void Controller_ProcessExited(Process process, int? exitCode)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var wasRequested = _requestedStopProcesses.Remove(process);
            var wasCurrentProcess = ReferenceEquals(_vpnProcess, process);
            if (wasCurrentProcess)
                _vpnProcess = null;

            if (wasRequested)
            {
                if (wasCurrentProcess)
                    SetStatus("Disconnected", "The application-owned WireSock process has stopped.", "#8290A3");
            }
            else
            {
                var exitText = exitCode?.ToString() ?? "unavailable";
                if (wasCurrentProcess)
                    SetStatus("WireSock exited", $"WireSock exited with code {exitText}.", "#B42318");
                AddLog("Service", $"WireSock process exited with code {exitText}.", "red");
                if (wasCurrentProcess)
                {
                    MessageBox.Show(this, $"WireSock exited with code {exitText}. Check the log for details.",
                        "Tunnel stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                process.Dispose();
            }

            DeleteRuntimeConfig(process);
            if (wasCurrentProcess)
                ConnectionButton.Content = "Start tunnel";
            if (_closeAfterStop)
            {
                _closeAfterStop = false;
                Close();
            }
        });
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_elevationRelaunch || _isExiting)
        {
            if (_vpnProcess is not null)
            {
                e.Cancel = true;
                _closeAfterStop = true;
                _ = StopForWindowCloseAsync();
                return;
            }

            DisposeTrayIcon();
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void ShowMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void DisposeTrayIcon()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
    }

    private async Task StopForWindowCloseAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            await StopTunnelCoreAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void SaveCurrentRules()
    {
        if (TunnelCombo.SelectedItem is not TunnelProfile profile)
            return;

        SaveRules(profile.Name);
    }

    private void DeleteRuntimeConfig(Process process)
    {
        if (!_runtimeConfigs.Remove(process, out var runtimePath))
            return;

        DeleteRuntimeConfig(runtimePath);
    }

    private void DeleteRuntimeConfig(string runtimePath)
    {
        try
        {
            _store.DeleteRuntimeConfig(runtimePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddLog("Settings", $"Could not remove the temporary runtime configuration: {exception.Message}", "red");
        }
    }

    private void SaveRules(string tunnelName)
    {
        _settings.TunnelRules[tunnelName] = new TunnelRules
        {
            IncludeApps = _includeApps.ToList(),
            ExcludeApps = _excludeApps.ToList(),
            UseTunnelDns = UseTunnelDnsCheck.IsChecked == true,
            CustomDns = CustomDnsText.Text.Trim()
        };
    }

    private TunnelRules GetRules(string name)
    {
        if (!_settings.TunnelRules.TryGetValue(name, out var rules) || rules is null)
        {
            rules = new TunnelRules();
            _settings.TunnelRules[name] = rules;
        }

        rules.IncludeApps ??= [];
        rules.ExcludeApps ??= [];
        rules.CustomDns ??= string.Empty;
        return rules;
    }

    private void SaveSettings()
    {
        if (!_isReady)
            return;

        try
        {
            _store.SaveSettings(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddLog("Settings", $"Could not save settings: {exception.Message}", "red");
            MessageBox.Show(this, $"Settings could not be saved:\n{exception.Message}",
                "Settings error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddLog(string category, string message, string color)
    {
        _logs.Insert(0, new LogEntry
        {
            Category = category,
            Message = WireSockController.SanitizeLogLine(message),
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Level = color
        });
        while (_logs.Count > 300)
            _logs.RemoveAt(_logs.Count - 1);
    }

    private void SetStatus(string status, string detail, string color)
    {
        ConnectionStatus.Text = status;
        StatusDetail.Text = detail;
        StatusDot.Fill = new BrushConverter().ConvertFromString(color) as Brush ?? Brushes.Gray;
    }

    private void ShowOperationError(string message)
    {
        SetStatus("Not connected", message, "#B42318");
        AddLog("Service", message, "red");
        MessageBox.Show(this, message, "Unable to start tunnel",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e) => _logs.Clear();

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record RunningApplication(string Name, string Path)
    {
        public string Display => $"{Name} — {Path}";
    }
}
