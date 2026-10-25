using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Tonari")]
[assembly: AssemblyDescription("四机房间配置、网络检测与游戏启动")]
[assembly: AssemblyProduct("Tonari")]
[assembly: AssemblyVersion("3.6.0.0")]
[assembly: AssemblyFileVersion("3.6.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8")]

namespace ChuniLanFour
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            bool first;
            using (var mutex = new Mutex(true, "Local\\CHUNITHM_LAN_Four", out first))
            {
                if (!first) { MessageBox.Show("Tonari 已经打开，请查看已有窗口。", "Tonari"); return; }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var window = CreateWindow();
                    var controller = new LauncherController(window, args.Contains("--layout-preview"));
                    app.Run(window);
                    GC.KeepAlive(controller);
                }
                catch (Exception ex) { RuntimeSupport.AppendLog(RuntimeSupport.DescribeException(ex)); MessageBox.Show("Tonari 未能启动：" + RuntimeSupport.DescribeException(ex), "Tonari", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }
        public static Window CreateWindow()
        {
            Window window;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                window = (Window)XamlReader.Load(stream);
            using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(icon, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            return window;
        }
    }

    public sealed class LauncherController
    {
        private readonly Window window;
        private readonly bool preview;
        private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChuniLanFour", "settings.json");
        private readonly string appRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private RoomConfig config;
        private ZeroTierSession session;
        private ZeroTierSession.Snapshot last;
        private readonly Dictionary<int, DeviceCard> devices = new Dictionary<int, DeviceCard>();
        private Task currentOperation;
        private bool busy, closing, allowClose;
        private bool updatingLaunch;
        private GameLaunchSettings launchSettings;
        private GameLaunchInspection launchInspection;
        private string launchInspectionConfig;
        private DateTime launchPendingUntil = DateTime.MinValue;
        private string nodeId, lastError, lastNote;
        private bool targetStandalone, modeExplicit, updatingMode, filesReady, retryFiles, needsSave, applyingGameFiles;
        private int preparationRevision;
        private string preparedIniHash;
        private FilePreparationState iniState = FilePreparationState.Waiting, batState = FilePreparationState.Waiting, testState = FilePreparationState.Waiting;
        private GameFilesResult inspectedFiles;
        private Action<GameLaunchSettings> persistLaunchSettings = value => value.Save();
        private Action<RoomConfig, string> persistRoom = (value, path) => value.Save(path);
        private Action<string> appendActivity = RuntimeSupport.AppendLog;
        private Func<RoomConfig, string, bool, Action<string, FilePreparationState, string>, GameFilesResult> inspectFiles = GameFilePreparation.InspectBin;
        private Func<RoomConfig, string, bool, Action<string, FilePreparationState, string>, GameFilesResult> prepareFiles = GameFilePreparation.PrepareBin;
        private readonly string[] operationButtons = { "PeersButton", "RefreshButton", "SaveButton", "ImportButton", "ExportButton", "BrowseBinButton", "RefreshFilesButton", "FirewallButton", "DiagnosticsButton", "RetryFilesButton", "LaunchHomeButton" };

        public LauncherController(Window window, bool preview)
        {
            this.window = window; this.preview = preview;
            Text("PrivilegeStatus").Text = "v3.6.0 · " + RuntimeSupport.PrivilegeStatus;
            if (!preview) Log("Tonari v3.6.0 启动；" + RuntimeSupport.PrivilegeStatus + "；" + Assembly.GetExecutingAssembly().Location);
            try { config = File.Exists(settingsPath) ? RoomConfig.Load(settingsPath) : new RoomConfig(); }
            catch (Exception ex) { config = new RoomConfig(); Log("房间设置读取失败：" + ex.Message); }
            FillEditor(config);
            BuildCards();
            try { launchSettings = GameLaunchSettings.Load(); }
            catch (Exception ex) { launchSettings = new GameLaunchSettings(); Log("启动设置读取失败：" + ex.Message); }
            targetStandalone = StandaloneMode;
            Find<TextBox>("BinPathInput").Text = SavedBinDirectory();
            updatingMode = true; Find<System.Windows.Controls.Primitives.ToggleButton>("NetworkModeToggle").IsChecked = !targetStandalone; updatingMode = false;
            Find<CheckBox>("ConnectFixCheck").IsChecked = launchSettings.UseConnect;
            Find<CheckBox>("DistributionFixCheck").IsChecked = (StandaloneMode || config.Slot == 1) && launchSettings.UseDistribution;
            if (Configured()) session = new ZeroTierSession(config);
            Button("RefreshButton").Click += async delegate { if (session != null) await Run(session.ReadSnapshot, false); };
            Button("PeersButton").Click += async delegate { if (session != null) await Run(session.CheckPeers, false); };
            Button("SaveButton").Click += async delegate { await SaveEditor(); };
            Button("ImportButton").Click += delegate { Guard(ImportRoom); };
            Button("ExportButton").Click += delegate { Guard(ExportRoom); };
            Button("BrowseBinButton").Click += async delegate { await BrowseGameFolder(); };
            Button("CentralButton").Click += delegate { Guard(delegate { OpenExternal(Configured() ? "https://central.zerotier.com/network/" + config.NetworkId : "https://central.zerotier.com/"); }); };
            Button("DownloadButton").Click += delegate { Guard(delegate { OpenExternal("https://www.zerotier.com/download/"); }); };
            Button("CopyNodeButton").Click += delegate { Guard(delegate { if (!String.IsNullOrEmpty(nodeId)) { Clipboard.SetText(nodeId); Log("节点 ID 已复制。"); } }); };
            Button("FirewallButton").Click += async delegate { await PrepareFirewall(); };
            Button("DiagnosticsButton").Click += async delegate { await ExportDiagnostics(); };
            Button("LaunchHomeButton").Click += async delegate { await LaunchGame(); };
            Button("RetryFilesButton").Click += async delegate { await ProcessFiles(false); };
            Button("RefreshFilesButton").Click += async delegate { await ProcessFiles(false); };
            Find<System.Windows.Controls.Primitives.ToggleButton>("NetworkModeToggle").Checked += async delegate { await ModeChanged(); };
            Find<System.Windows.Controls.Primitives.ToggleButton>("NetworkModeToggle").Unchecked += async delegate { await ModeChanged(); };
            Find<CheckBox>("ConnectFixCheck").Checked += LaunchOptionChanged;
            Find<CheckBox>("ConnectFixCheck").Unchecked += LaunchOptionChanged;
            Find<CheckBox>("DistributionFixCheck").Checked += LaunchOptionChanged;
            Find<CheckBox>("DistributionFixCheck").Unchecked += LaunchOptionChanged;
            Button("HostGuideButton").Click += delegate { Guard(delegate { OpenGuide("基准机教程.html"); }); };
            Button("ClientGuideButton").Click += delegate { Guard(delegate { OpenGuide("接入机教程.html"); }); };
            foreach (string name in new[] { "RoomNameInput", "NetworkIdInput", "SubnetInput" }) Find<TextBox>(name).TextChanged += delegate { RoomEditorChanged(); };
            Find<ComboBox>("SlotInput").SelectionChanged += delegate { RoomEditorChanged(); };
            timer.Tick += async delegate { await RefreshCurrentMode(); };
            window.Loaded += delegate
            {
                var area = SystemParameters.WorkArea;
                window.MaxHeight = Math.Max(400, area.Height - 30); window.MaxWidth = Math.Max(600, area.Width - 30);
                window.MinHeight = Math.Min(520, window.MaxHeight); window.MinWidth = Math.Min(700, window.MaxWidth);
                window.Width = Math.Min(1000, window.MaxWidth); window.Height = Math.Min(820, window.MaxHeight);
                window.Left = area.Left + (area.Width - window.Width) / 2; window.Top = area.Top + (area.Height - window.Height) / 2;
            };
            window.ContentRendered += FirstRendered;
            window.Closing += OnClosing;
            window.Closed += delegate { timer.Stop(); cancellation.Dispose(); };
            ShowConfigSummary(); UpdateButtons();
        }
        private async void FirstRendered(object sender, EventArgs e)
        {
            window.ContentRendered -= FirstRendered;
            if (preview) { SetState("四机界面预览", "界面预览。", "界面预览", false); return; }
            if (!String.IsNullOrWhiteSpace(SelectedBin())) await ProcessFiles(false);
            if (closing) return;
            if (session != null && !targetStandalone) await Run(session.ReadSnapshot, false);
            if (!closing) timer.Start();
        }
        private async Task RefreshCurrentMode()
        {
            if (busy || closing || preview) return;
            if (!targetStandalone && session != null && EditorIsSaved()) await Run(session.ReadSnapshot, true);
            else UpdateButtons();
        }
        private T Find<T>(string name) where T : class { var value = window.FindName(name) as T; if (value == null) throw new InvalidOperationException("缺少界面组件：" + name); return value; }
        private Button Button(string name) { return Find<Button>(name); }
        private TextBlock Text(string name) { return Find<TextBlock>(name); }
        private static Brush Color(string color) { return (Brush)new BrushConverter().ConvertFromString(color); }
        private bool Configured() { try { config.Validate(); return true; } catch { return false; } }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { ShowError(RuntimeSupport.DescribeException(ex)); } }
        private void ShowError(string text) { Log(text); MessageBox.Show(window, text, "Tonari", MessageBoxButton.OK, MessageBoxImage.Information); }
        private void OpenExternal(string path) { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        private void OpenGuide(string name)
        {
            string path = Path.Combine(appRoot, name);
            if (!File.Exists(path)) throw new FileNotFoundException("教程文件未找到。请完整解压工具包，保留 EXE 旁的两份 HTML 教程。", path);
            OpenExternal(path);
        }
        private void Log(string text) { if (!preview) appendActivity(text); var log = Find<TextBox>("ActivityLog"); if (log.Text.Length > 30000) log.Text = log.Text.Substring(log.Text.Length - 18000); log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); log.ScrollToEnd(); }
        private void FillEditor(RoomConfig value)
        {
            Find<TextBox>("RoomNameInput").Text = value.RoomName;
            Find<TextBox>("NetworkIdInput").Text = value.NetworkId;
            Find<TextBox>("SubnetInput").Text = value.Subnet;
            Find<ComboBox>("SlotInput").SelectedIndex = value.Slot - 1;
        }
        private RoomConfig ReadEditor(bool validate = true)
        {
            var value = new RoomConfig { RoomName = Find<TextBox>("RoomNameInput").Text.Trim(), NetworkId = Find<TextBox>("NetworkIdInput").Text.Trim().ToLowerInvariant(), Subnet = Find<TextBox>("SubnetInput").Text.Trim(), GameIniPath = config.GameIniPath ?? "", Slot = Find<ComboBox>("SlotInput").SelectedIndex + 1 };
            if (validate) value.Validate(); return value;
        }
        private string Fingerprint(RoomConfig value) { return value.RoomName + "\n" + value.NetworkId + "\n" + value.Subnet + "\n" + value.Slot + "\n" + value.GameIniPath; }
        private void RequireSaved()
        {
            if (!Configured()) throw new InvalidOperationException("请填写房间设置，并点击“保存房间设置”。");
            if (!EditorIsSaved()) throw new InvalidOperationException("设置已有改动，请先点击“保存房间设置”。");
        }
        private void RequireEditable() { if (busy || closing || preview) throw new InvalidOperationException("请等待当前操作完成后再修改房间设置。"); }
        private Task SaveEditor()
        {
            if (busy || closing || preview) return Task.FromResult(0);
            var work = SaveEditorCore(false); currentOperation = work; return work;
        }
        private async Task SaveEditorCore(bool modeOnly)
        {
            GameFilesResult deployed = null;
            bool connectDraft = (Find<CheckBox>("ConnectFixCheck").IsChecked == true) != launchSettings.UseConnect;
            bool distributionDraft = (Find<CheckBox>("DistributionFixCheck").IsChecked == true) != launchSettings.UseDistribution;
            try
            {
                RequireEditable();
                if (GameIsRunning() || LaunchIsPending()) throw new InvalidOperationException("请先关闭游戏和 amdaemon，再保存房间设置。");
                var value = modeOnly ? CloneRoom(config) : ReadEditor(!targetStandalone);
                if (modeOnly && !targetStandalone && !Configured()) throw new InvalidOperationException("请先在房间设置中保存联机房间与机号。");
                bool validRoom = modeOnly ? Configured() : EditorIsValid();
                // Solo play requires no network. Invalid edited room fields must still be corrected.
                if (!modeOnly && !validRoom && Fingerprint(value) != Fingerprint(config)) value.Validate();
                string bin = SelectedBin();
                if ((targetStandalone || modeOnly) && String.IsNullOrWhiteSpace(bin)) throw new InvalidOperationException("请先选择游戏的 bin 文件夹。");
                busy = true; filesReady = false; needsSave = true; applyingGameFiles = true;
                SetMachineStatus("更新中：正在检查 segatools 与机器状态…", FilePreparationState.Working);
                if (modeOnly) SetModeStatus(targetStandalone ? "正在切换为单机…" : "正在恢复已保存的联机配置…");
                UpdateButtons();
                int revision = ++preparationRevision;
                if (!String.IsNullOrWhiteSpace(bin))
                {
                    bool standalone = targetStandalone;
                    deployed = await Task.Run(() => prepareFiles(value, bin, standalone, FileProgress(revision)));
                    if (deployed == null || !deployed.IsComplete) throw new InvalidOperationException("游戏文件检查未完成，请查看三项读取状态。");
                }
                var next = CopyLaunchSettings();
                next.StandaloneMode = targetStandalone;
                next.UseConnect = modeOnly ? launchSettings.UseConnect : Find<CheckBox>("ConnectFixCheck").IsChecked == true;
                next.UseDistribution = (targetStandalone || value.Slot == 1) && (modeOnly ? launchSettings.UseDistribution : Find<CheckBox>("DistributionFixCheck").IsChecked == true);
                bool patchesAdjusted = false;
                if (deployed != null)
                {
                    patchesAdjusted = next.UseConnect && !deployed.Launch.CanUseConnect || next.UseDistribution && !deployed.Launch.CanUseDistribution;
                    next.UseConnect = next.UseConnect && deployed.Launch.CanUseConnect;
                    next.UseDistribution = next.UseDistribution && deployed.Launch.CanUseDistribution;
                    value.GameIniPath = deployed.Target.GameIniPath;
                    next.BinDirectory = deployed.BinDirectory; next.IniPath = deployed.Target.GameIniPath;
                    next.BatPath = deployed.Launch.BatPath; next.PreparedIniHash = deployed.IniHash;
                    if (targetStandalone)
                    {
                        next.StandaloneIniPath = next.IniPath; next.StandaloneSubnet = deployed.Target.Subnet; next.StandaloneIniHash = deployed.IniHash;
                    }
                }
                if (validRoom) persistRoom(value, settingsPath);
                persistLaunchSettings(next);
                if (validRoom) config = value;
                launchSettings = next; inspectedFiles = deployed;
                updatingLaunch = true;
                if (!modeOnly || !connectDraft) Find<CheckBox>("ConnectFixCheck").IsChecked = next.UseConnect;
                if (!modeOnly || !distributionDraft) Find<CheckBox>("DistributionFixCheck").IsChecked = next.UseDistribution;
                updatingLaunch = false;
                launchInspection = deployed == null ? null : deployed.Launch;
                preparedIniHash = deployed == null ? null : deployed.IniHash;
                launchInspectionConfig = deployed == null ? null : LaunchConfigKey();
                filesReady = deployed != null; needsSave = false; modeExplicit = false; retryFiles = false;
                session = Configured() ? new ZeroTierSession(config) : null;
                last = null; nodeId = null;
                Text("NodeId").Text = "检测网络后显示"; Text("NetworkDetails").Text = "检测后显示网络信息。";
                ShowConfigSummary(); SetState("设置已保存", "连接房间网络后，点击“检测网络”。", "待检测", false);
                Text("SettingsNote").Text = deployed == null ? "房间已保存。请选择游戏 bin 文件夹后再次保存，完成游戏配置。" : "设置已保存，segatools 与机器状态已同步。";
                if (patchesAdjusted) Text("SettingsNote").Text += " 不兼容的补丁选项已取消。";
                SetModeStatus(deployed == null ? "房间已保存；游戏文件尚未选择。" : targetStandalone ? "单机配置已保存。" : "已保存“" + config.RoomName + "”的 " + config.Slot + " 号联机配置。");
                SetMachineStatus(deployed == null ? "尚未选择游戏目录。" : "当前机器状态：" + deployed.TestRole.RoleName + " · " + deployed.TestRole.GroupName + "（保存完成）。", deployed == null ? FilePreparationState.Waiting : FilePreparationState.Ready);
                Log(Text("SettingsNote").Text);
            }
            catch (Exception ex)
            {
                filesReady = false; needsSave = true; retryFiles = true;
                bool changed = deployed != null || ex is GameFilePreparationException && ((GameFilePreparationException)ex).ConfigurationMayHaveChanged;
                string message = (changed ? "保存未完成，部分文件可能已更新。请重新保存房间设置。" : "保存失败：" + ex.Message);
                SetModeStatus(modeOnly ? (targetStandalone ? "未能切换为单机。" : "未能切换为联机。") : "房间设置未能保存。", true); Text("SettingsNote").Text = message;
                SetMachineStatus("保存失败：" + ex.Message + (changed ? " 部分文件可能已更新，请重新保存。" : ""), FilePreparationState.Error);
                Log("保存房间设置失败：" + RuntimeSupport.DescribeException(ex));
            }
            finally { applyingGameFiles = false; busy = false; if (!closing) UpdateButtons(); }
        }
        private void ImportRoom()
        {
            RequireEditable(); var dialog = new OpenFileDialog { Filter = "CHUNITHM 房间 (*.chuniroom.json)|*.chuniroom.json|JSON (*.json)|*.json" };
            if (dialog.ShowDialog(window) != true) return;
            StageRoom(RoomConfig.ImportRoom(dialog.FileName));
        }
        private void StageRoom(RoomConfig room)
        {
            room.GameIniPath = config.GameIniPath; FillEditor(room);
            Text("SettingsNote").Text = "房间已导入。请选择分配给本机的机号，并保存设置。";
            UpdateButtons();
        }
        private void ExportRoom()
        {
            RequireSaved(); if (targetStandalone || config.Slot != 1) throw new InvalidOperationException("请在基准机导出房间配置。"); var dialog = new SaveFileDialog { Filter = "CHUNITHM 房间 (*.chuniroom.json)|*.chuniroom.json", FileName = "联机房间.chuniroom.json", DefaultExt = ".chuniroom.json" };
            if (dialog.ShowDialog(window) != true) return;
            config.ExportRoom(dialog.FileName); Log("房间文件已导出。"); Text("SettingsNote").Text = "房间文件已导出，可分发给接入机。";
        }
        private async Task BrowseGameFolder()
        {
            if (preview || busy || closing) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择包含 segatools.ini 和 start.bat 的游戏 bin 文件夹", ShowNewFolderButton = false, SelectedPath = SelectedBin() })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                Find<TextBox>("BinPathInput").Text = dialog.SelectedPath;
            }
            await ProcessFiles(true);
        }
        private string SelectedBin() { return Find<TextBox>("BinPathInput").Text.Trim(); }
        private string SavedBinDirectory()
        {
            if (!String.IsNullOrWhiteSpace(launchSettings.BinDirectory)) return launchSettings.BinDirectory;
            string ini = !String.IsNullOrWhiteSpace(launchSettings.IniPath) ? launchSettings.IniPath : StandaloneMode && !String.IsNullOrWhiteSpace(launchSettings.StandaloneIniPath) ? launchSettings.StandaloneIniPath : config.GameIniPath;
            if (String.IsNullOrWhiteSpace(ini)) return "";
            try { return Path.GetDirectoryName(Path.GetFullPath(ini)); } catch { return ""; }
        }
        private static bool SamePath(string a, string b)
        {
            try { return String.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return String.IsNullOrWhiteSpace(a) && String.IsNullOrWhiteSpace(b); }
        }
        private bool StandaloneMode { get { return launchSettings != null && launchSettings.StandaloneMode; } }
        private RoomConfig LaunchContext()
        {
            if (!StandaloneMode) return config;
            return new RoomConfig { Slot = 1, Subnet = launchSettings.StandaloneSubnet, GameIniPath = launchSettings.StandaloneIniPath };
        }
        private string LaunchConfigKey()
        {
            var value = LaunchContext();
            return (StandaloneMode ? "single" : value.NetworkId) + "|" + value.Subnet + "|" + value.Slot + "|" + value.GameIniPath;
        }
        private void RequireLaunchSettings()
        {
            if (!StandaloneMode) { RequireSaved(); return; }
            if (String.IsNullOrWhiteSpace(launchSettings.StandaloneIniPath) || !File.Exists(launchSettings.StandaloneIniPath))
                throw new InvalidOperationException("未找到单机配置文件，请重新选择游戏文件。");
        }
        private bool EditorIsSaved()
        {
            try { return !HasPendingChanges(); }
            catch { return false; }
        }
        private bool HasPendingChanges()
        {
            return needsSave || targetStandalone != StandaloneMode || Fingerprint(ReadEditor(false)) != Fingerprint(config) ||
                !SamePath(SelectedBin(), SavedBinDirectory()) ||
                (Find<CheckBox>("ConnectFixCheck").IsChecked == true) != launchSettings.UseConnect ||
                ((targetStandalone || ReadEditor(false).Slot == 1) && Find<CheckBox>("DistributionFixCheck").IsChecked == true) != launchSettings.UseDistribution;
        }
        private bool EditorIsValid() { try { ReadEditor(); return true; } catch { return false; } }
        private void RoomEditorChanged()
        {
            // Room edits remain a draft until SaveEditor commits them.
            UpdateButtons();
        }
        private GameLaunchSettings CopyLaunchSettings()
        {
            return new GameLaunchSettings { BatPath = launchSettings.BatPath, IniPath = launchSettings.IniPath, BinDirectory = launchSettings.BinDirectory,
                PreparedIniHash = launchSettings.PreparedIniHash, UseConnect = launchSettings.UseConnect, UseDistribution = launchSettings.UseDistribution,
                StandaloneMode = launchSettings.StandaloneMode, StandaloneIniPath = launchSettings.StandaloneIniPath,
                StandaloneSubnet = launchSettings.StandaloneSubnet, StandaloneIniHash = launchSettings.StandaloneIniHash };
        }
        private void LaunchOptionChanged(object sender, RoutedEventArgs e)
        {
            if (updatingLaunch || preview) return;
            UpdateButtons();
        }
        private void ApplyLaunchInspection(GameLaunchInspection inspection)
        {
            launchInspection = inspection; launchInspectionConfig = LaunchConfigKey();
            updatingLaunch = true;
            if (!inspection.CanUseConnect) Find<CheckBox>("ConnectFixCheck").IsChecked = false;
            if (!inspection.CanUseDistribution) Find<CheckBox>("DistributionFixCheck").IsChecked = false;
            updatingLaunch = false;
            SetFileState("bat", FilePreparationState.Ready, "兼容性检查完成");
        }
        private void RequireSameGameDirectory(GameLaunchInspection inspection)
        {
            var context = LaunchContext();
            if (String.IsNullOrWhiteSpace(context.GameIniPath)) return;
            string iniDirectory = Path.GetFullPath(Path.GetDirectoryName(context.GameIniPath)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string gameDirectory = Path.GetFullPath(inspection.GameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!String.Equals(iniDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("启动脚本与 segatools.ini 的游戏目录不同，请重新选择游戏 bin 文件夹。\n启动游戏：" + gameDirectory + "\n当前 INI：" + iniDirectory);
        }
        private void SetFileState(string item, FilePreparationState state, string message)
        {
            if (closing) return;
            if (item == "ini") iniState = state; else if (item == "bat") batState = state; else testState = state;
            var text = Text(item == "ini" ? "IniReadStatus" : item == "bat" ? "BatReadStatus" : "TestReadStatus");
            string prefix = item == "ini" ? "segatools：" : item == "bat" ? "start.bat：" : message.StartsWith("机器状态", StringComparison.Ordinal) ? "当前" : "当前机器状态：";
            text.Text = prefix + message.Replace("\r\n", " · ").Replace("\n", " · ");
            text.Foreground = Color(state == FilePreparationState.Error ? "#B74437" : state == FilePreparationState.Ready ? "#16805C" : state == FilePreparationState.Working ? "#235CC5" : "#69778D");
        }
        private void SetModeStatus(string message, bool error = false)
        {
            if (closing) return;
            Text("ModeStatus").Text = message; Text("ModeStatus").Foreground = Color(error ? "#B74437" : "#69778D");
        }
        private void SetMachineStatus(string message, FilePreparationState state)
        {
            if (closing) return;
            Text("ModeHint").Text = message;
            Text("ModeHint").Foreground = Color(state == FilePreparationState.Error ? "#B74437" : state == FilePreparationState.Ready ? "#16805C" : state == FilePreparationState.Working ? "#235CC5" : "#69778D");
        }
        private void SyncModeToggle()
        {
            updatingMode = true; Find<System.Windows.Controls.Primitives.ToggleButton>("NetworkModeToggle").IsChecked = !targetStandalone; updatingMode = false;
        }
        private Task ModeChanged()
        {
            if (updatingMode || preview || closing) return Task.FromResult(0);
            if (busy || GameIsRunning() || LaunchIsPending()) { SyncModeToggle(); return Task.FromResult(0); }
            targetStandalone = Find<System.Windows.Controls.Primitives.ToggleButton>("NetworkModeToggle").IsChecked != true;
            modeExplicit = true;
            var work = SaveEditorCore(true); currentOperation = work; return work;
        }
        private Task ProcessFiles(bool detectMode)
        {
            if (busy || closing || preview) return Task.FromResult(0);
            var work = ProcessFilesCore(detectMode); currentOperation = work; return work;
        }
        private Action<string, FilePreparationState, string> FileProgress(int revision)
        {
            return (item, state, message) => window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!closing && revision == preparationRevision)
                {
                    SetFileState(item, state, message);
                    if (applyingGameFiles && state == FilePreparationState.Working)
                        SetMachineStatus("更新中：" + (item == "ini" ? "segatools · " : item == "test" ? "机器状态 · " : "启动脚本 · ") + message, state);
                }
            }));
        }
        private async Task ProcessFilesCore(bool detectMode)
        {
            busy = true; filesReady = false; retryFiles = false;
            launchInspection = null; launchInspectionConfig = null; inspectedFiles = null;
            int revision = ++preparationRevision;
            string bin = SelectedBin();
            SetMachineStatus("读取中：正在刷新游戏文件状态…", FilePreparationState.Working);
            UpdateButtons();
            try
            {
                if (String.IsNullOrWhiteSpace(bin))
                {
                    foreach (string item in new[] { "ini", "bat", "test" }) SetFileState(item, FilePreparationState.Waiting, "请选择游戏 bin 文件夹。");
                    SetMachineStatus("尚未选择游戏 bin 文件夹。", FilePreparationState.Waiting);
                    SetModeStatus("请选择游戏 bin 文件夹，读取后保存房间设置。"); return;
                }
                var room = ReadEditor(false);
                bool standalone = targetStandalone;
                // Inspection never deploys files or persists preferences, including startup and retries.
                inspectedFiles = await Task.Run(() => inspectFiles(room, bin, standalone, FileProgress(revision)));
                if (closing || revision != preparationRevision) return;
                if (inspectedFiles == null) throw new InvalidOperationException("未能读取游戏文件。");
                if (detectMode && !modeExplicit && inspectedFiles.Config != null)
                    targetStandalone = GameFilePreparation.ResolveStandalone(inspectedFiles.Config, config, launchSettings, Path.Combine(bin, "segatools.ini"));
                SyncModeToggle();
                if (inspectedFiles.Launch != null) ApplyLaunchInspection(inspectedFiles.Launch);
                filesReady = MatchesSavedFiles(inspectedFiles);
                if (filesReady) preparedIniHash = inspectedFiles.IniHash;
                else if (SamePath(bin, SavedBinDirectory())) needsSave = true;
                retryFiles = inspectedFiles.Config == null || inspectedFiles.Launch == null || inspectedFiles.TestRole == null;
                SetMachineStatus(inspectedFiles.TestRole == null ? "机器状态读取失败，请查看游戏设置中的提示。" :
                    "当前机器状态：" + inspectedFiles.TestRole.RoleName + " · " + inspectedFiles.TestRole.GroupName +
                    (needsSave ? "；配置尚未完成保存。" : "。"), inspectedFiles.TestRole == null ? FilePreparationState.Error : needsSave ? FilePreparationState.Waiting : FilePreparationState.Ready);
                SetModeStatus(retryFiles ? "读取未完成，请查看游戏设置中的状态。" : HasPendingChanges() || !filesReady ? "游戏文件已读取；点击“保存房间设置”后应用所选模式。" : "已读取保存的配置。");
            }
            catch (Exception ex)
            {
                filesReady = false; retryFiles = true;
                SetModeStatus("读取失败：" + ex.Message, true);
                SetMachineStatus("读取失败：" + ex.Message, FilePreparationState.Error);
                Log("游戏文件读取失败：" + RuntimeSupport.DescribeException(ex));
            }
            finally { busy = false; if (!closing) UpdateButtons(); }
        }
        private bool MatchesSavedFiles(GameFilesResult result)
        {
            if (result == null || result.Config == null || result.Launch == null || result.TestRole == null || !SamePath(result.BinDirectory, SavedBinDirectory())) return false;
            var context = LaunchContext();
            int slot = StandaloneMode ? 1 : config.Slot;
            return SamePath(context.GameIniPath, Path.Combine(result.BinDirectory, "segatools.ini")) &&
                (StandaloneMode || Configured()) && result.Config.Slot == slot && result.Config.Subnet == context.Subnet &&
                result.Config.Role == (slot == 1 ? GameConfigRole.Host : GameConfigRole.Client) && result.TestRole.Role == (slot == 1 ? 0 : 1);
        }
        private static RoomConfig CloneRoom(RoomConfig source)
        {
            return new RoomConfig { RoomName = source.RoomName, NetworkId = source.NetworkId, Slot = source.Slot, Subnet = source.Subnet, GameIniPath = source.GameIniPath };
        }
        private bool GameIsRunning()
        {
            return GameLauncher.IsGameRunning();
        }
        private bool LaunchIsPending() { return DateTime.UtcNow < launchPendingUntil; }
        private string LaunchBlockReason(bool gameRunning)
        {
            if (preview) return "界面预览。";
            if (closing) return "正在关闭助手。";
            if (gameRunning) return "游戏正在运行。";
            if (LaunchIsPending()) return "游戏正在启动…";
            if (busy) return "正在处理当前操作，请稍候。";
            if (HasPendingChanges()) return "设置未保存，请点击“保存房间设置”。";
            if (!filesReady) return "请在“游戏设置”选择 bin 文件夹，读取成功后保存房间设置。";
            if (!StandaloneMode && !EditorIsValid()) return "请填写房间网络与机号，再保存设置。";
            if (!StandaloneMode && !EditorIsSaved()) return "设置未保存，请点击“保存房间设置”。";
            if (launchInspection == null || launchInspectionConfig != LaunchConfigKey()) return "游戏文件需要重新处理。";
            if (StandaloneMode)
            {
                if (!String.Equals(SelectedBin(), Path.GetDirectoryName(launchSettings.StandaloneIniPath), StringComparison.OrdinalIgnoreCase))
                    return "配置文件已更改，请重新应用单机配置。";
                return null;
            }
            if (last == null) return "请连接 ZeroTier 后检测网络。";
            if (!last.Installed) return "未安装 ZeroTier。请在“帮助与诊断”下载客户端。";
            if (!last.ServiceRunning) return "请启动 ZeroTier。";
            if (!last.NetworkOk) return "房间网络尚未就绪：" + last.Network + "。请检查成员授权及网络详细信息。";
            if (!last.CorrectAddress) return "本机地址尚未就绪：需要 " + config.ExpectedIp + "/24；当前 " + last.Address + "。";
            return null;
        }
        private async Task LaunchGame()
        {
            if (busy || closing || preview) return;
            try
            {
                RequireLaunchSettings(); string reason = LaunchBlockReason(GameIsRunning());
                if (reason != null) throw new InvalidOperationException(reason);
                var context = LaunchContext();
                if (String.IsNullOrWhiteSpace(preparedIniHash) || FileHash(context.GameIniPath) != preparedIniHash)
                { filesReady = false; needsSave = true; retryFiles = true; throw new InvalidOperationException("游戏配置已更改，请读取后重新保存房间设置。"); }
                TestRoleInspection role;
                try { role = GameTestRole.Inspect(context.GameIniPath); }
                catch (Exception ex)
                {
                    filesReady = false; retryFiles = true;
                    SetFileState("test", FilePreparationState.Error, ex.Message);
                    throw;
                }
                if (role.Role != (context.Slot == 1 ? 0 : 1))
                {
                    filesReady = false; needsSave = true;
                    SetFileState("test", FilePreparationState.Error, "当前角色与已保存的房间设置不同，请重新保存。");
                    throw new InvalidOperationException("机器状态已变化，请重新保存房间设置。");
                }
                bool connect = Find<CheckBox>("ConnectFixCheck").IsChecked == true;
                bool distribution = context.Slot == 1 && Find<CheckBox>("DistributionFixCheck").IsChecked == true;
                string path = launchInspection.BatPath, expectedHash = launchInspection.BatHash, subnet = context.Subnet;
                int slot = context.Slot;
                busy = true; UpdateButtons();
                if (!StandaloneMode)
                {
                    var networkWork = Task.Run(() => session.ReadSnapshot(cancellation.Token)); currentOperation = networkWork;
                    var snapshot = await networkWork;
                    if (closing) return;
                    ApplySnapshot(snapshot);
                    if (!snapshot.ServiceRunning || !snapshot.NetworkOk || !snapshot.CorrectAddress) throw new InvalidOperationException("网络未就绪。" + (snapshot.Note ?? ""));
                }
                var inspectWork = Task.Run(() => GameLauncher.Inspect(path, subnet)); currentOperation = inspectWork;
                var fresh = await inspectWork;
                if (closing) return;
                RequireSameGameDirectory(fresh);
                if (fresh.BatHash != expectedHash)
                {
                    launchInspection = null; launchInspectionConfig = null; filesReady = false; retryFiles = true;
                    Text("BatReadStatus").Text = "启动脚本已改变，请重试读取。";
                    throw new InvalidOperationException("启动脚本在读取后发生变化，请重试读取。");
                }
                if (connect && !fresh.CanUseConnect) throw new InvalidOperationException("修正连接等待当前不可用：" + fresh.ConnectReason);
                if (distribution && !fresh.CanUseDistribution) throw new InvalidOperationException("配信启动修复当前不可用：" + fresh.DistributionReason);
                var prepareWork = Task.Run(() => GameLauncher.Prepare(fresh, connect, distribution, slot, subnet)); currentOperation = prepareWork;
                var prepared = await prepareWork;
                if (closing) return;
                var startWork = Task.Run(() => GameLauncher.Start(prepared)); currentOperation = startWork;
                await startWork;
                launchPendingUntil = DateTime.UtcNow.AddSeconds(20);
                Log("已打开启动入口。" + prepared.Summary);
            }
            catch (Exception ex) { if (!closing) ShowError(RuntimeSupport.DescribeException(ex)); }
            finally { busy = false; if (!closing) UpdateButtons(); }
        }
        private static string FileHash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = System.Security.Cryptography.SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(stream));
        }
        private async Task PrepareFirewall()
        {
            try { RequireSaved(); await RunAction(() => SetupActions.ConfigureFirewall(config), "防火墙规则已配置。"); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        private async Task ExportDiagnostics()
        {
            if (busy || closing || preview) return;
            try
            {
                RequireSaved();
                var dialog = new SaveFileDialog { Filter = "诊断报告 (*.txt)|*.txt", FileName = "Tonari-诊断-" + config.Slot + "号-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
                if (dialog.ShowDialog(window) != true) return;
                busy = true; UpdateButtons();
                var work = Task.Run(() => NetworkDiagnostics.BuildReport(config)); currentOperation = work;
                string report = await work;
                if (File.Exists(RuntimeSupport.LogPath))
                {
                    string tail = File.ReadAllText(RuntimeSupport.LogPath);
                    report += "\r\n===== 助手最近活动（本地记录）=====\r\n" + RuntimeSupport.SafeText(tail.Length > 16000 ? tail.Substring(tail.Length - 16000) : tail, 16000);
                }
                File.WriteAllText(dialog.FileName, report, new System.Text.UTF8Encoding(true));
                if (!closing) { Text("SetupResult").Text = "已保存诊断报告：" + dialog.FileName; Log("诊断报告：" + dialog.FileName); }
            }
            catch (Exception ex) { if (!closing) ShowError(RuntimeSupport.DescribeException(ex)); }
            finally { busy = false; if (!closing) UpdateButtons(); }
        }
        private async Task<bool> RunAction(Func<string> action, string message)
        {
            if (busy || closing || preview) return false;
            busy = true; UpdateButtons();
            var work = Task.Run(action); currentOperation = work;
            try
            {
                string result = await work;
                if (!closing)
                {
                    Text("SetupResult").Text = result;
                    Log(message + " " + result);
                }
                return true;
            }
            catch (Exception ex) { if (!closing) ShowError(RuntimeSupport.DescribeException(ex)); }
            finally { busy = false; if (!closing) UpdateButtons(); }
            return false;
        }
        private async Task<bool> Run(Func<CancellationToken, ZeroTierSession.Snapshot> action, bool automatic)
        {
            if (busy || closing || preview) return false;
            busy = true; UpdateButtons();
            var work = Task.Run(() => action(cancellation.Token)); currentOperation = work;
            try { var result = await work; if (!closing) { ApplySnapshot(result); lastError = null; } return true; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!closing)
                {
                    last = null;
                    ShowConfigSummary();
                    string detail = RuntimeSupport.DescribeException(ex);
                    SetState("需要检查连接", detail, "需要处理", false);
                    if (!automatic || detail != lastError) Log(detail);
                    lastError = detail;
                }
            }
            finally { busy = false; if (!closing) UpdateButtons(); }
            return false;
        }
        private void SetState(string headline, string detail, string badge, bool ready)
        {
            Text("Headline").Text = headline; Text("Description").Text = detail; Text("StatusBadgeText").Text = badge;
            Text("StatusBadgeText").Foreground = Color(ready ? "#16805C" : "#6B7890");
            Find<Border>("StatusBadge").Background = Color(ready ? "#E6F5EC" : "#EDF1F7");
        }
        private void ShowConfigSummary()
        {
            bool configured = Configured();
            Text("RoomSummary").Text = configured ? config.RoomName + "  ·  " + config.Cidr + "  ·  本机 " + config.Slot + " 号" : "1 台基准机，最多 3 台接入机。";
            foreach (var pair in devices)
            {
                bool local = configured && pair.Key == config.Slot;
                pair.Value.Title.Text = pair.Key + " 号 · " + (pair.Key == 1 ? "基准机" : "接入机") + (local ? " · 本机" : "");
                pair.Value.Ip.Text = configured ? config.AddressForSlot(pair.Key) : "待设置";
                pair.Value.Status.Text = local ? "未连接" : "未检测";
                pair.Value.Detail.Text = local ? "等待取得本机实际地址" : "等待连接检测。";
                pair.Value.Border.BorderBrush = Color(local ? "#AFC8FA" : pair.Key == 1 && config.Slot != 1 ? "#B8D9CE" : "#E2E8F1");
                pair.Value.Border.Background = Color(local ? "#F1F6FF" : "#FFFFFF");
            }
        }
        private void ApplySnapshot(ZeroTierSession.Snapshot value)
        {
            last = value;
            bool ready = value.ServiceRunning && value.NetworkOk && value.CorrectAddress;
            if (!value.Installed) SetState("先安装 ZeroTier", "在“帮助与诊断”下载并安装 ZeroTier。", "未安装", false);
            else if (!value.ServiceRunning) SetState("ZeroTier 尚未运行", "请启动 ZeroTier 并连接房间网络。", "未运行", false);
            else if (ready) SetState("本机网络已就绪", config.Slot == 1 ? "基准机地址已就绪，可检测接入设备。" : "接入机地址已就绪，可检测基准机及其他设备。", "网络正常", true);
            else if (value.Network == "网段冲突") SetState("房间网段发生冲突", value.Note, "需要处理", false);
            else if ((value.Network ?? "").Contains("授权")) SetState("等待网络授权", "请由网络管理员授权本机节点，并分配 " + config.ExpectedIp + " /24。", "等待授权", false);
            else SetState("等待网络就绪", "目标地址 " + config.ExpectedIp + " /24。请核对云端授权、地址分配与“网络详细信息”。", "等待连接", false);
            nodeId = value.NodeId; Text("NodeId").Text = String.IsNullOrEmpty(nodeId) ? "检测网络后显示" : nodeId;
            Text("NetworkDetails").Text = "Network ID：" + config.NetworkId + "\n服务：" + value.Service + "\n启动方式：" + value.Startup + "\n本网络：" + value.Network + "\n连接选项：" + value.Settings + "\n系统实际地址：" + value.Address + "\n更新时间：" + value.Updated;
            var local = devices[config.Slot]; local.Status.Text = ready ? "已连接" : value.ServiceRunning ? "等待连接" : "未连接"; local.Status.Foreground = Color(ready ? "#16805C" : "#65748B");
            local.Ip.Text = String.IsNullOrEmpty(value.LocalIp) ? "尚未分配" : value.LocalIp;
            local.Detail.Text = value.CorrectAddress ? "已核对 Windows 实际网卡地址 /24" : "目标 " + config.ExpectedIp + " /24";
            if (!ready)
            {
                foreach (var pair in devices.Where(p => p.Key != config.Slot)) { pair.Value.Status.Text = "未检测"; pair.Value.Status.Foreground = Color("#65748B"); pair.Value.Detail.Text = "本机就绪后可检测回应。"; }
            }
            else if (value.Peers != null)
            {
                foreach (var peer in value.Peers)
                {
                    if (!devices.ContainsKey(peer.Slot) || peer.Slot == config.Slot) continue;
                    var card = devices[peer.Slot]; card.Status.Text = peer.Replies > 0 ? "有回应" : "未收到回应";
                    card.Status.Foreground = Color(peer.Replies > 0 ? "#16805C" : "#9C6B23"); card.Detail.Text = peer.Text;
                }
            }
            if (!String.IsNullOrEmpty(value.Note) && value.Note != lastNote) Log(value.Note);
            lastNote = value.Note;
        }
        private void Visible(string name, bool show) { Find<FrameworkElement>(name).Visibility = show ? Visibility.Visible : Visibility.Collapsed; }
        private void UpdateButtons()
        {
            bool available = !busy && !closing && !preview, configured = Configured(), saved = EditorIsSaved();
            const string draftNote = "更改尚未保存，点击“保存房间设置”后统一应用。";
            if (!busy && !retryFiles && !saved) Text("SettingsNote").Text = draftNote;
            else if (saved && Text("SettingsNote").Text == draftNote) Text("SettingsNote").Text = "当前房间设置已保存。";
            bool gameRunning = GameIsRunning(), pending = LaunchIsPending();
            foreach (var name in operationButtons) Button(name).IsEnabled = available;
            Button("SaveButton").IsEnabled = available && !gameRunning && !pending && (EditorIsValid() || targetStandalone);
            Button("RefreshFilesButton").IsEnabled = available && !String.IsNullOrWhiteSpace(SelectedBin());
            bool ready = last != null && last.ServiceRunning && last.NetworkOk && last.CorrectAddress;
            Button("PeersButton").IsEnabled = available && !targetStandalone && ready && saved;
            Button("RefreshButton").IsEnabled = available && !targetStandalone && configured && saved;
            Button("ExportButton").IsEnabled = available && !targetStandalone && configured && config.Slot == 1 && saved;
            Button("CopyNodeButton").IsEnabled = available && !targetStandalone && !String.IsNullOrEmpty(nodeId);
            bool hasIni = !String.IsNullOrWhiteSpace(config.GameIniPath) && File.Exists(config.GameIniPath);
            Button("FirewallButton").IsEnabled = available && !targetStandalone && configured && saved && hasIni;
            Button("DiagnosticsButton").IsEnabled = available && !targetStandalone && configured && saved;
            Find<Grid>("SettingsFields").IsEnabled = available;
            Find<ComboBox>("SlotInput").IsEnabled = available;
            Find<System.Windows.Controls.Primitives.ToggleButton>("NetworkModeToggle").IsEnabled = available && !gameRunning && !pending;
            foreach (string name in new[] { "CentralButton", "DownloadButton", "HostGuideButton", "ClientGuideButton" }) Button(name).IsEnabled = available;
            RefreshRoleUi(configured, saved); UpdateLaunchUi(available, gameRunning);
            window.Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        }
        private void RefreshRoleUi(bool configured, bool saved)
        {
            bool solo = targetStandalone, host = !solo && configured && config.Slot == 1, client = !solo && configured && config.Slot != 1;
            string role = solo ? "单机 · 基准机" : !configured ? "联机 · 尚未配置" : config.Slot + " 号 · " + (host ? "基准机" : "接入机");
            Text("RoleBadgeText").Text = role + (saved ? "" : " · 待保存");
            Text("RoomProfileSummary").Text = configured ? "已保存房间：" + config.RoomName + " · " + config.Slot + " 号 · " + config.ExpectedIp : "尚未保存联机房间。";
            Text("RoleEditorHint").Text = solo ? "保存下次联机使用的房间与机号。" : Find<ComboBox>("SlotInput").SelectedIndex == 0 ? "1 号基准机组织房间；配置完成后向接入机分发房间文件。" : "接入机使用 2～4 号。导入房间后，选择分配给本机的机号。";
            Visible("NetworkOverviewPanel", !solo); Visible("MembersPanel", !solo && configured);
            Visible("NodePanel", !solo); Visible("NetworkToolsPanel", host);
            Visible("NetworkDiagnosticsPanel", !solo); Visible("NetworkDetailsPanel", !solo); Visible("ToolsPanel", !solo);
            Visible("ExportButton", host); Visible("SaveButton", !saved); Visible("RetryFilesButton", retryFiles);
            Button("SaveButton").Content = "保存房间设置";
            Button("ImportButton").Content = host ? "导入其他房间" : "导入房间";
            Button("ImportButton").Style = (Style)window.FindResource(client || !configured ? "Primary" : (object)typeof(Button));
            Button("PeersButton").Content = host ? "检测接入设备" : client ? "检测房间设备" : "检测设备";
            Text("MembersTitle").Text = "房间设备";
            Text("MembersHint").Text = host ? "本机基准机与接入设备的地址及网络状态。" : "先确认 1 号基准机在线，再检查其他参与设备。";
            Text("NodeRoleHint").Text = host ? "本机网络身份，用于核对基准机授权。" : "将节点 ID 提供给网络管理员，完成本机授权与地址分配。";
            Text("NetworkToolsHint").Text = "在网络管理页核对成员授权与机号地址。";
            Button("CentralButton").Content = "打开网络管理";
            Text("FirewallRoleHint").Text = "为房间内的游戏进程与设备检测配置防火墙规则。";
            Text("ConnectPatchHint").Text = "接入机卡在「Aグループ基準機から設定を取得します」时，基准机和接入机都要启用。";
            Text("DistributionPatchHint").Text = "接入机停在「配信サーバーを探しています」时，在基准机启用。";
            Text("GuideTitle").Text = solo ? "单机与联机切换" : host ? "基准机使用指南" : client ? "接入机使用指南" : "开始使用";
            Text("GuideDescription").Text = solo ? "在首页切换游玩模式，状态栏显示保存结果。" : host ? "配置 1 号基准机，分发房间并组织联机。" : client ? "导入房间、设置机号并加入店内匹配。" : "根据本机用途选择基准机或接入机教程。";
            Button("HostGuideButton").Content = host ? "阅读基准机教程" : "基准机教程";
            Button("ClientGuideButton").Content = client ? "阅读接入机教程" : "接入机教程";
            Button("HostGuideButton").Style = (Style)window.FindResource(host ? "Primary" : (object)typeof(Button));
            Button("ClientGuideButton").Style = (Style)window.FindResource(client ? "Primary" : (object)typeof(Button));
        }
        private void UpdateLaunchUi(bool available, bool gameRunning)
        {
            bool inspected = launchInspection != null;
            bool host = targetStandalone || ReadEditor(false).Slot == 1;
            Visible("PatchOptionsPanel", inspected); Visible("PatchEmptyHint", !inspected);
            Visible("DistributionOptionCard", host);
            Find<CheckBox>("ConnectFixCheck").IsEnabled = available && inspected && launchInspection.CanUseConnect;
            Find<CheckBox>("DistributionFixCheck").IsEnabled = available && host && inspected && launchInspection.CanUseDistribution;
            Text("LaunchRoleHint").Text = targetStandalone ? "单机兼容设置" : host ? "基准机补丁" : "接入机补丁";
            string role = targetStandalone ? "单机 · 基准机" : !Configured() ? "尚未配置" : config.Slot + " 号 · " + (host ? "基准机" : "接入机");
            if (inspected)
            {
                Text("ConnectCompatibility").Text = launchInspection.CanUseConnect ? "兼容当前版本" : launchInspection.ConnectReason;
                Text("DistributionCompatibility").Text = launchInspection.CanUseDistribution ? "兼容当前版本" : launchInspection.DistributionReason;
                Text("ConnectCompatibility").Foreground = Color(launchInspection.CanUseConnect ? "#16805C" : "#986524");
                Text("DistributionCompatibility").Foreground = Color(launchInspection.CanUseDistribution ? "#16805C" : "#986524");
                var selected = new List<string>();
                if (Find<CheckBox>("ConnectFixCheck").IsChecked == true) selected.Add("连接等待");
                if (host && Find<CheckBox>("DistributionFixCheck").IsChecked == true) selected.Add("配信启动");
                Text("LaunchHomeSummary").Text = role + " · " + Path.GetFileName(launchInspection.BatPath) + (selected.Count == 0 ? "" : " · " + String.Join(" / ", selected.ToArray()));
            }
            else Text("LaunchHomeSummary").Text = role;
            string reason = LaunchBlockReason(gameRunning);
            Button("LaunchHomeButton").IsEnabled = available && reason == null;
            Text("LaunchHomeStatus").Text = reason ?? (targetStandalone ? "可以启动单人游玩。" : host ? "准备就绪。先启动本机，再启动接入机。" : "准备就绪。请等待基准机进入标题画面后启动。");
        }
        private void BuildCards()
        {
            var grid = Find<System.Windows.Controls.Primitives.UniformGrid>("DeviceGrid");
            for (int slot = 1; slot <= 4; slot++)
            {
                var card = new DeviceCard();
                card.Border = new Border { Background = Brushes.White, BorderBrush = Color("#E2E8F1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0,0,0,6), Padding = new Thickness(14,8,14,8) };
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                card.Title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                card.Ip = new TextBlock { FontSize = 15, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(card.Ip,1);
                card.Status = new TextBlock { FontSize = 12, Foreground = Color("#65748B"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(card.Status,2);
                card.Detail = new TextBlock { Style = (Style)window.Resources["Caption"], LineHeight = 18, Margin = new Thickness(0,2,0,0) }; Grid.SetRow(card.Detail,1); Grid.SetColumnSpan(card.Detail,3);
                row.Children.Add(card.Title); row.Children.Add(card.Ip); row.Children.Add(card.Status); row.Children.Add(card.Detail);
                card.Border.Child = row; grid.Children.Add(card.Border); devices.Add(slot,card);
            }
        }
        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (allowClose) return; e.Cancel = true; if (closing) return;
            closing = true; timer.Stop(); cancellation.Cancel(); UpdateButtons();
            SetState("正在关闭助手", "正在完成当前操作。", "正在关闭", false);
            await Task.Yield();
            try
            {
                if (currentOperation != null) { try { await currentOperation; } catch { } }
            }
            catch (Exception ex) { Log("关闭时结束本机操作：" + RuntimeSupport.DescribeException(ex)); }
            allowClose = true; window.Close();
        }
        private sealed class DeviceCard { public Border Border; public TextBlock Title, Status, Ip, Detail; }
    }
}
