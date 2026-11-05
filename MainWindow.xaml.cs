using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BootMonitor
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<StartupItem> _startupItems = new();
        private readonly DispatcherTimer _monitorTimer;
        private readonly Stopwatch _runtime = Stopwatch.StartNew();
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _diskIdleCounter;
        private bool _countersFailed;
        private bool _allTargetsSeen;
        private bool _finished;

        // Tuning
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan RequiredStableTime = TimeSpan.FromSeconds(10); // must stay calm this long = ready
        private static readonly TimeSpan MaxRuntime = TimeSpan.FromMinutes(15);         // hard cap: never run longer than this
        private const float CpuIdleThreshold = 20f;   // %
        private const float DiskBusyThreshold = 15f;  // %

        private TimeSpan _stableTime = TimeSpan.Zero;

        public MainWindow()
        {
            InitializeComponent();
            LstProcesses.ItemsSource = _startupItems;

            PositionWindowToBottomRight();
            RegisterAppToStartup();
            LoadStartupTargets();

            _monitorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
            _monitorTimer.Tick += MonitorTimer_Tick;

            Closed += (_, _) => Cleanup();
            Loaded += async (_, _) =>
            {
                // Creating perf counters can block for several seconds during boot; keep it off the UI thread
                // so the window stays responsive (and closable) meanwhile.
                await Task.Run(InitializePerformanceCounters);
                if (_finished)
                {
                    // Window was closed while counters were initializing
                    _cpuCounter?.Dispose();
                    _diskIdleCounter?.Dispose();
                    return;
                }
                if (_countersFailed)
                    TxtSystemMetrics.Text = "Metrics unavailable - close manually or wait for timeout";
                _monitorTimer.Start();
            };
        }

        private void PositionWindowToBottomRight()
        {
            var workingArea = SystemParameters.WorkArea;
            Left = workingArea.Right - Width - 20;
            Top = workingArea.Bottom - Height - 20;
        }

        // --- Closing / dragging -------------------------------------------------

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Cleanup()
        {
            _finished = true;
            _monitorTimer.Stop();
            _cpuCounter?.Dispose();
            _diskIdleCounter?.Dispose();
            _cpuCounter = null;
            _diskIdleCounter = null;
        }

        // --- Setup --------------------------------------------------------------

        // --- Run-at-startup handling ---------------------------------------------
        // The app adds itself to startup only on its very first launch. After that the user's choice
        // (made with the startup button) is respected, so removing it doesn't get undone on the next manual run.

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "BootMonitorApp";
        private const string SettingsKeyPath = @"Software\BootMonitor";
        private const string FirstRunDoneValue = "StartupInitialized";

        private static string? StartupCommand =>
            string.IsNullOrEmpty(Environment.ProcessPath) ? null : $"\"{Environment.ProcessPath}\"";

        private void RegisterAppToStartup()
        {
            try
            {
                using RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
                using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                string? command = StartupCommand;
                if (run == null || command == null) return;

                bool firstRun = settings.GetValue(FirstRunDoneValue) == null;
                object? existing = run.GetValue(RunValueName);

                if (firstRun)
                {
                    run.SetValue(RunValueName, command);
                    settings.SetValue(FirstRunDoneValue, 1, RegistryValueKind.DWord);
                }
                else if (existing != null && existing.ToString() != command)
                {
                    run.SetValue(RunValueName, command); // keep path current if the exe moved
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Startup registration failed: {ex.Message}");
            }
            UpdateStartupButton();
        }

        private static bool IsInStartup()
        {
            try
            {
                using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return run?.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }

        private void BtnStartup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (run == null) return;

                if (IsInStartup())
                {
                    run.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
                else if (StartupCommand is string command)
                {
                    run.SetValue(RunValueName, command);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Startup toggle failed: {ex.Message}");
            }
            UpdateStartupButton();
        }

        private void UpdateStartupButton()
        {
            BtnStartup.Content = IsInStartup() ? "Remove from startup" : "Add to startup";
        }

        private void InitializePerformanceCounters()
        {
            try
            {
                var cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                // "% Idle Time" is far more reliable than "% Disk Time" (which can exceed 100% and spikes wildly)
                var disk = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");

                // Warm up counters (first read always returns 0)
                cpu.NextValue();
                disk.NextValue();

                _cpuCounter = cpu;
                _diskIdleCounter = disk;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Perf counters unavailable: {ex.Message}");
                _countersFailed = true;
            }
        }

        private void LoadStartupTargets()
        {
            // Scan primary Registry Paths
            string[] runKeys = { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run" };
            foreach (var path in runKeys)
            {
                ExtractRegistryItems(Registry.CurrentUser, path);
                ExtractRegistryItems(Registry.LocalMachine, path);
            }

            // Scan Common Directory shortcuts
            ExtractFolderItems(Environment.SpecialFolder.Startup);
            ExtractFolderItems(Environment.SpecialFolder.CommonStartup);
        }

        private void ExtractRegistryItems(RegistryKey hive, string path)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) return;
                foreach (var name in key.GetValueNames())
                {
                    if (name.Contains("BootMonitorApp")) continue; // Skip itself
                    string exeName = GetExeNameFromCommand(key.GetValue(name)?.ToString()) ?? name;
                    _startupItems.Add(new StartupItem { ProcessName = name, ExeName = exeName, StatusText = "Pending...", StatusColor = "#FFCC00" });
                }
            }
            catch { /* Access protections */ }
        }

        private void ExtractFolderItems(Environment.SpecialFolder folder)
        {
            try
            {
                string path = Environment.GetFolderPath(folder);
                if (!Directory.Exists(path)) return;
                foreach (var file in Directory.GetFiles(path))
                {
                    if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileNameWithoutExtension(file);
                    _startupItems.Add(new StartupItem { ProcessName = name, ExeName = name, StatusText = "Pending...", StatusColor = "#FFCC00" });
                }
            }
            catch { }
        }

        // Extracts the executable name (without extension) from a Run-key command line,
        // e.g. "C:\Program Files\App\app.exe" --minimized  ->  app
        private static string? GetExeNameFromCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = Environment.ExpandEnvironmentVariables(command.Trim());

            string exePath;
            if (command.StartsWith('"'))
            {
                int end = command.IndexOf('"', 1);
                exePath = end > 1 ? command[1..end] : command.Trim('"');
            }
            else
            {
                int exeIdx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                exePath = exeIdx >= 0 ? command[..(exeIdx + 4)] : command.Split(' ')[0];
            }

            string name = Path.GetFileNameWithoutExtension(exePath);
            return string.IsNullOrEmpty(name) ? null : name;
        }

        // --- Monitoring loop ----------------------------------------------------

        private void MonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (_finished) return;

            // Hard cap so the app can never linger in the background
            if (_runtime.Elapsed >= MaxRuntime)
            {
                ExecuteFinishedState("Timed out - assuming PC is ready", "#FFCC00");
                return;
            }

            if (_cpuCounter == null || _diskIdleCounter == null) return; // counters unavailable: wait for timeout or manual close

            float cpuLoad;
            float diskBusy;
            try
            {
                cpuLoad = _cpuCounter.NextValue();
                diskBusy = Math.Clamp(100f - _diskIdleCounter.NextValue(), 0f, 100f);
            }
            catch
            {
                return;
            }

            if (!_allTargetsSeen) UpdateStartupItemStatus();

            // Count how long resources stay under the "settled" thresholds
            if (cpuLoad < CpuIdleThreshold && diskBusy < DiskBusyThreshold)
                _stableTime += TickInterval;
            else
                _stableTime = TimeSpan.Zero; // reset if hardware spikes again

            TxtSystemMetrics.Text = $"CPU: {cpuLoad:0}%  |  Disk: {diskBusy:0}%  |  Calm: {_stableTime.TotalSeconds:0}/{RequiredStableTime.TotalSeconds:0}s";

            if (_stableTime >= RequiredStableTime)
            {
                ExecuteFinishedState("PC Fully Loaded & Ready!", "#4CD964");
            }
        }

        private void UpdateStartupItemStatus()
        {
            // Track live process states matching discovered names
            var activeProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p) activeProcesses.Add(p.ProcessName);
            }

            bool allSeen = true;
            foreach (var item in _startupItems)
            {
                if (item.IsRunning) continue;
                if (activeProcesses.Contains(item.ExeName) || activeProcesses.Contains(item.ProcessName))
                {
                    item.IsRunning = true;
                    item.StatusText = "Active / Running";
                    item.StatusColor = "#4CD964"; // Green
                }
                else
                {
                    allSeen = false;
                }
            }
            _allTargetsSeen = allSeen; // stop scanning processes once everything has been found
        }

        private async void ExecuteFinishedState(string message, string color)
        {
            if (_finished) return;
            Cleanup(); // stop timer + release counters immediately

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            TxtStatus.Text = message;
            TxtStatus.Foreground = brush;
            BootProgress.IsIndeterminate = false; // stops the indeterminate animation
            BootProgress.Value = 100;
            BootProgress.Foreground = brush;

            // Change remaining unlaunched entries to settled status
            foreach (var item in _startupItems)
            {
                if (!item.IsRunning)
                {
                    item.StatusText = "Passed / Loaded";
                    item.StatusColor = "#8A8A93";
                }
            }

            // Leave a static message on screen for 5 minutes (or until closed with the X / Esc), then exit.
            // Task.Delay is just an OS timer: no CPU is used while waiting.
            TxtSystemMetrics.Text = $"Ready at {DateTime.Now:h:mm tt} - this will close in 5 min";
            await Task.Delay(TimeSpan.FromMinutes(5));
            Application.Current?.Shutdown();
        }
    }

    // Helper Model to Bind Data to XAML UI Elements dynamically
    public class StartupItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _statusText = "";
        private string _statusColor = "#8A8A93";

        public required string ProcessName { get; init; }
        public required string ExeName { get; init; }
        public bool IsRunning { get; set; }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public string StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(nameof(StatusColor)); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
