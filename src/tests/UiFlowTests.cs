using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChuniLanFour;

namespace ChuniUiTests
{
    public sealed class UiReport
    {
        public string Version { get; set; }
        public string Failure { get; set; }
        public string FixtureRoot { get; set; }
        public List<string> Checks { get; set; }
        public List<LayoutResult> Layouts { get; set; }
    }
    public sealed class LayoutResult
    {
        public string Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Tab { get; set; }
        public double Scroll { get; set; }
        public double OuterGap { get; set; }
        public string Role { get; set; }
        public string Image { get; set; }
    }

    // Runs the real controller offscreen. All file operations target generated
    // fixtures, and persistence/logging use injected memory-only callbacks.
    public static class UiFlowTests
    {
        internal const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static UiReport report;
        private static string output;
        internal static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + name);
            report.Checks.Add(name);
        }
        public static UiReport Run(string outputDirectory, string fixtureDirectory, string expectedVersion)
        {
            output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
            report = new UiReport { Version = typeof(Program).Assembly.GetName().Version.ToString(), Checks = new List<string>(), Layouts = new List<LayoutResult>() };
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChuniLanFour");
            string[] protectedFiles = { Path.Combine(settings, "settings.json"), Path.Combine(settings, "game-launch.json"), RuntimeSupport.LogPath };
            var before = protectedFiles.ToDictionary(p => p, Signature);
            var previous = SynchronizationContext.Current;
            string root = Path.GetFullPath(fixtureDirectory);
            report.FixtureRoot = root; Directory.CreateDirectory(root);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "controller runs on STA with Dispatcher synchronization");
                Check(report.Version == expectedVersion, "application version matches current source");
                TestReadOnlyActions(root);
                TestMetadataOnlySave(root);
                TestIncompatiblePatchAfterSave(root);
                TestSaveAndModes(root, 2);
                TestSaveAndModes(root, 3);
                TestModePreservesDrafts(root);
                TestModePreservesPatchDrafts(root);
                TestModeNormalizesDistribution(root);
                TestMissingPrerequisites(root);
                TestSoloWithoutNetwork(root);
                TestFailures(root);
                TestBusy(root, false);
                TestBusy(root, true);
            }
            catch (Exception ex) { report.Failure = ex.ToString(); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                foreach (string file in protectedFiles)
                    if (before[file] != Signature(file)) report.Failure = (report.Failure ?? "") + "\nProtected user file changed: " + file;
                    else report.Checks.Add("user file unchanged: " + Path.GetFileName(file));
                if (String.IsNullOrEmpty(report.Failure) && Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0) Directory.Delete(root, false);
            }
            return report;
        }

        private static void TestReadOnlyActions(string root)
        {
            using (var h = new Harness(root, "read-only", 3, 3, false))
            {
                CheckControls(h);
                string original = h.FilesSignature;
                h.ChooseBin();
                Check(h.InspectCalls == 1 && h.PrepareCalls == 0, "choosing bin calls only the read pipeline");
                h.AssertNoWrites(original, "folder selection");
                Check(!String.IsNullOrEmpty(h.Text("IniReadStatus")) && !String.IsNullOrEmpty(h.Text("BatReadStatus")) && !String.IsNullOrEmpty(h.Text("TestReadStatus")), "folder read supplies all three status rows");
                Check(h.Pending && h.LaunchReason != null && h.Visible("SaveButton"), "new folder remains pending until explicit Save");
                h.RefreshFiles(); h.RefreshFiles(); h.Process(true);
                h.AssertNoWrites(original, "retry and startup-style reads");
                Check(h.InspectCalls == 4, "refresh button performs a new read on every click");
                Check(h.Text("IniReadStatus").Contains("segatools") && h.Text("BatReadStatus").Contains("start.bat") && h.Text("TestReadStatus").Contains("机器状态"), "three file rows use the public file and machine-state labels");
                h.FailTestRead = true; h.RefreshFiles();
                Check(h.Field<FilePreparationState>("testState") == FilePreparationState.Error && h.Field<FilePreparationState>("iniState") == FilePreparationState.Ready && h.Field<FilePreparationState>("batState") == FilePreparationState.Ready, "refresh reads the other rows when machine-state inspection fails");
                h.FailTestRead = false; h.RefreshFiles();
                Check(h.Field<FilePreparationState>("testState") == FilePreparationState.Ready, "refresh recovers machine-state row without saving");
                h.AssertNoWrites(original, "failed and recovered refresh");
                var imported = new RoomConfig { RoomName = "导入的隔离房间", NetworkId = "fedcba9876543210", Subnet = "192.168.138.0", Slot = 2 };
                h.Call("StageRoom", imported);
                Check(h.Control<TextBox>("RoomNameInput").Text == imported.RoomName && h.Room.Slot == 3 && h.Room.NetworkId == "0123456789abcdef", "StageRoom updates editor without replacing committed room");
                h.AssertNoWrites(original, "room import");
                h.Control<TextBox>("RoomNameInput").Text = "编辑草稿";
                h.Control<TextBox>("SubnetInput").Text = "192.168.139.0";
                h.Control<ComboBox>("SlotInput").SelectedIndex = 1;
                h.AssertNoWrites(original, "room field editing");
                h.Control<CheckBox>("ConnectFixCheck").IsChecked = true;
                h.AssertNoWrites(original, "patch selection");
                Check(h.Pending && !h.Control<Button>("LaunchHomeButton").IsEnabled, "all unsaved drafts block game launch");
                h.Call("FillEditor", h.Room); h.Refresh();
                CapturePair(h, "client-game-read-status", 2, false);
                CapturePair(h, "client-room-save-position", 1, true);
            }
        }
        private static void TestSaveAndModes(string root, int slot)
        {
            using (var h = new Harness(root, "save-slot-" + slot, slot, 1, false))
            {
                h.ChooseBin();
                h.Set("targetStandalone", false); h.Call("SyncModeToggle");
                string original = h.FilesSignature;
                h.AssertNoWrites(original, "slot " + slot + " preparation draft");
                h.Save();
                Check(h.PrepareCalls == 1 && h.RoomPersistCalls == 1 && h.LaunchPersistCalls == 1, "slot " + slot + " first Save commits once to each injected destination");
                Check(h.Ready && !h.Pending && !h.TargetSolo, "slot " + slot + " successful Save completes online configuration");
                Check(h.IniSlot == slot && h.TestRole == 1, "slot " + slot + " Save synchronizes client INI and TEST role");
                Check(h.Room.Slot == slot && h.PersistedRooms.Last().Slot == slot && !h.Settings.StandaloneMode, "slot " + slot + " committed room and launch mode agree");
                Check(!h.Visible("NetworkToolsPanel"), "slot " + slot + " client hides Central");
                h.OnlineSnapshot();
                Check(h.LaunchReason == null && h.Control<Button>("LaunchHomeButton").IsEnabled, "slot " + slot + " saved matching files and network permit launch");
                int commits = h.PrepareCalls, roomWrites = h.RoomPersistCalls, prefWrites = h.LaunchPersistCalls;
                string committed = h.FilesSignature;
                h.Process(false);
                Check(h.PrepareCalls == commits && h.RoomPersistCalls == roomWrites && h.LaunchPersistCalls == prefWrites && h.FilesSignature == committed, "slot " + slot + " startup-style reinspection does not recommit saved files");
                h.ChangeMode(false);
                Check(h.PrepareCalls == commits + 1 && h.IniSlot == 1 && h.TestRole == 0 && h.Settings.StandaloneMode, "slot " + slot + " solo toggle immediately synchronizes host INI and machine state");
                Check(h.Room.Slot == slot && h.PersistedRooms.Last().Slot == slot, "slot " + slot + " solo toggle preserves online machine number");
                string success = h.Text("ModeHint"); h.Refresh();
                Check(success.Contains("保存完成") && h.Text("ModeHint") == success, "slot " + slot + " mode success survives unrelated UI refresh");
                Check(h.Ready && !h.Pending && h.LaunchReason == null, "slot " + slot + " saved solo can launch without network");
                Check(!h.Visible("NetworkOverviewPanel") && !h.Visible("NetworkToolsPanel"), "slot " + slot + " solo hides network overview and management");
                if (slot == 3) { LayoutMatrix(h, "solo"); CapturePair(h, "solo-mode-card", 0, false); CapturePair(h, "solo-game-status", 2, false); }
                committed = h.FilesSignature; commits = h.PrepareCalls;
                h.ChangeMode(true);
                h.OnlineSnapshot();
                Check(h.PrepareCalls == commits + 1 && h.IniSlot == slot && h.TestRole == 1 && h.Room.Slot == slot && !h.Settings.StandaloneMode && !h.Pending, "slot " + slot + " online toggle immediately restores saved client number and machine state");
                if (slot == 3) { LayoutMatrix(h, "client"); CapturePair(h, "client-game-saved-status", 2, false); }
            }
            if (slot == 2)
                using (var h = new Harness(root, "host", 1, 2, false))
                {
                    h.ChooseBin(); h.Save(); h.OnlineSnapshot();
                    Check(h.Ready && h.IniSlot == 1 && h.TestRole == 0, "host Save synchronizes INI and TEST base roles");
                    Check(h.Visible("NetworkToolsPanel") && h.Visible("DistributionOptionCard"), "online host exposes network management and distribution patch");
                    Check(h.Text("ConnectPatchHint").Contains("基准机和接入机都要启用"), "connection patch explanation requires both participants");
                    h.Control<CheckBox>("DistributionFixCheck").IsChecked = true;
                    int draftCommits = h.PrepareCalls, draftRooms = h.RoomPersistCalls, draftPrefs = h.LaunchPersistCalls;
                    string draftFiles = h.FilesSignature;
                    h.Control<ComboBox>("SlotInput").SelectedIndex = 1;
                    Check(!h.Visible("DistributionOptionCard") && h.Control<CheckBox>("DistributionFixCheck").IsChecked == true, "client draft hides distribution option without discarding checked value");
                    h.Control<ComboBox>("SlotInput").SelectedIndex = 0;
                    Check(h.Visible("DistributionOptionCard") && h.Control<CheckBox>("DistributionFixCheck").IsChecked == true, "returning to host draft preserves distribution selection");
                    Check(h.PrepareCalls == draftCommits && h.RoomPersistCalls == draftRooms && h.LaunchPersistCalls == draftPrefs && h.FilesSignature == draftFiles, "host-client-host draft changes perform no writes");
                    LayoutMatrix(h, "host"); CapturePair(h, "host-overview", 0, false); CapturePair(h, "host-game-status", 2, false);
                    h.Control<TextBox>("RoomNameInput").Text += " · 已修改";
                    Check(h.Text("SettingsNote").Contains("尚未保存") && !h.Text("SettingsNote").Contains("已同步"), "room draft replaces stale successful-save note");
                    CapturePair(h, "host-room-save-position", 1, true);
                    h.Control<ComboBox>("SlotInput").SelectedIndex = 1; h.Save();
                    Check(!h.Settings.UseDistribution && h.Control<CheckBox>("DistributionFixCheck").IsChecked == false && h.TestRole == 1, "client Save clears the host-only distribution flag and applies client TEST role");
                }
        }
        private static void TestModePreservesDrafts(string root)
        {
            using (var h = new Harness(root, "mode-with-room-draft", 3, 3, false))
            {
                h.ChooseBin(); h.Save();
                h.Control<TextBox>("RoomNameInput").Text = "尚未保存的新房间";
                h.Control<TextBox>("NetworkIdInput").Text = "fedcba9876543210";
                h.Control<TextBox>("SubnetInput").Text = "192.168.138.0";
                h.Control<ComboBox>("SlotInput").SelectedIndex = 1;
                h.Control<CheckBox>("ConnectFixCheck").IsChecked = true;
                h.ChangeMode(false);
                Check(h.IniSlot == 1 && h.TestRole == 0 && h.Settings.StandaloneMode, "mode-only solo deployment executes despite pending room edits");
                Check(h.Room.Slot == 3 && h.Room.Subnet == "192.168.139.0" && h.Room.NetworkId == "0123456789abcdef", "mode-only deployment uses the committed room rather than edited room");
                Check(h.PersistedRooms.Last().Slot == 3 && h.PersistedRooms.Last().NetworkId == "0123456789abcdef" && h.PersistedRooms.Last().RoomName == "隔离测试房间", "mode-only room persistence excludes all room drafts");
                Check(h.Control<TextBox>("RoomNameInput").Text == "尚未保存的新房间" && h.Control<TextBox>("NetworkIdInput").Text == "fedcba9876543210" && h.Control<TextBox>("SubnetInput").Text == "192.168.138.0" && h.Control<ComboBox>("SlotInput").SelectedIndex == 1, "mode-only success preserves all room editor drafts");
                Check(!h.Settings.UseConnect && !h.PersistedLaunch.Last().UseConnect && h.Control<CheckBox>("ConnectFixCheck").IsChecked == true, "mode-only success neither commits nor erases connection patch draft");
                Check(h.Ready && h.Pending && h.Visible("SaveButton") && h.LaunchReason != null, "successful mode save leaves unsaved room and patch drafts pending and launch blocked");
                h.ChangeMode(true);
                Check(h.IniSlot == 3 && h.TestRole == 1 && !h.Settings.StandaloneMode, "return to network uses original saved client slot despite edited slot");
                Check(h.Control<ComboBox>("SlotInput").SelectedIndex == 1 && h.Control<CheckBox>("ConnectFixCheck").IsChecked == true && h.Pending, "return to network still retains room and patch drafts");
                h.Control<TextBox>("NetworkIdInput").Text = "incomplete";
                h.ChangeMode(false);
                Check(h.IniSlot == 1 && h.TestRole == 0 && h.Settings.StandaloneMode && h.Control<TextBox>("NetworkIdInput").Text == "incomplete", "invalid unsaved room fields do not prevent mode-only use of saved room or get discarded");
                h.Control<TextBox>("NetworkIdInput").Text = "fedcba9876543210";
                h.Control<TextBox>("SubnetInput").Text = "192.168.139.0";
                h.Save();
                Check(h.Room.Slot == 2 && h.Room.RoomName == "尚未保存的新房间" && h.Room.NetworkId == "fedcba9876543210" && h.Settings.UseConnect && !h.Pending, "explicit full Save commits preserved room and patch drafts");
                h.ChangeMode(true);
                Check(h.IniSlot == 2 && h.TestRole == 1 && !h.Settings.StandaloneMode, "subsequent online toggle uses newly committed client slot");
            }
        }
        private static void TestModePreservesPatchDrafts(string root)
        {
            using (var h = new Harness(root, "mode-with-patch-drafts", 1, 1, false))
            {
                h.ChooseBin();
                h.Control<CheckBox>("ConnectFixCheck").IsChecked = true;
                h.Control<CheckBox>("DistributionFixCheck").IsChecked = true; h.Save();
                h.Control<CheckBox>("ConnectFixCheck").IsChecked = false;
                h.Control<CheckBox>("DistributionFixCheck").IsChecked = false;
                h.ChangeMode(false); h.ChangeMode(true);
                Check(h.Settings.UseConnect && h.Settings.UseDistribution && h.PersistedLaunch.Last().UseConnect && h.PersistedLaunch.Last().UseDistribution, "mode-only round trip retains saved enabled patches");
                Check(h.Control<CheckBox>("ConnectFixCheck").IsChecked == false && h.Control<CheckBox>("DistributionFixCheck").IsChecked == false && h.Pending, "mode-only round trip keeps unchecked patch drafts pending");
                h.Save();
                Check(!h.Settings.UseConnect && !h.Settings.UseDistribution && !h.Pending, "explicit Save can later commit unchecked patch drafts");
            }
        }
        private static void TestModeNormalizesDistribution(string root)
        {
            using (var h = new Harness(root, "mode-normalizes-distribution", 3, 3, false))
            {
                h.ChooseBin(); h.Save(); h.ChangeMode(false);
                h.Control<CheckBox>("DistributionFixCheck").IsChecked = true; h.Save();
                Check(h.Settings.StandaloneMode && h.Settings.UseDistribution && !h.Pending, "solo can explicitly save distribution patch for a saved client room");
                h.ChangeMode(true); h.OnlineSnapshot();
                Check(!h.Settings.UseDistribution && !h.PersistedLaunch.Last().UseDistribution && h.Control<CheckBox>("DistributionFixCheck").IsChecked == false, "client switch normalizes saved distribution patch and matching checkbox together");
                Check(h.IniSlot == 3 && h.TestRole == 1 && !h.Pending && h.LaunchReason == null && h.Control<Button>("LaunchHomeButton").IsEnabled, "client switch with patch normalization creates no pending draft and permits launch");
                h.ChangeMode(false);
                Check(!h.Settings.UseDistribution && h.Control<CheckBox>("DistributionFixCheck").IsChecked == false && !h.Pending, "return to solo does not restore normalized patch as a false draft");
                Check(h.IniSlot == 1 && h.TestRole == 0 && h.LaunchReason == null && h.Control<Button>("LaunchHomeButton").IsEnabled, "solo after normalized client round trip remains launchable");
            }
        }
        private static void TestMissingPrerequisites(string root)
        {
            using (var h = new Harness(root, "missing-bin-mode", 3, 3, false))
            {
                string original = h.FilesSignature; h.ChangeMode(false);
                h.AssertNoWrites(original, "mode toggle with missing bin");
                Check(h.Pending && !h.Ready && h.LaunchReason != null && h.Text("ModeHint").Contains("保存失败") && h.Text("ModeHint").Contains("bin"), "missing bin displays persistent failure and blocks launch");
                Check(h.Text("ModeStatus") == "未能切换为单机。" && h.Text("ModeStatus") != h.Text("ModeHint"), "failed mode label and detailed machine status are distinct");
                string failure = h.Text("ModeHint"); h.Refresh();
                Check(h.Text("ModeHint") == failure, "UI refresh does not overwrite missing-bin failure");
                CapturePair(h, "mode-save-missing-bin", 0, false);
            }
            using (var h = new Harness(root, "missing-saved-room-mode", 3, 3, true))
            {
                h.ChooseBin(); h.ChangeMode(false);
                int writes = h.PrepareCalls, rooms = h.RoomPersistCalls, preferences = h.LaunchPersistCalls;
                string original = h.FilesSignature;
                h.Control<TextBox>("NetworkIdInput").Text = "fedcba9876543210";
                h.ChangeMode(true);
                Check(h.PrepareCalls == writes && h.RoomPersistCalls == rooms && h.LaunchPersistCalls == preferences && h.FilesSignature == original, "network mode does not deploy or save an uncommitted valid room draft");
                Check(h.Settings.StandaloneMode && h.Pending && !h.Ready && h.LaunchReason != null && h.Text("ModeHint").Contains("保存失败"), "missing saved network room keeps committed solo mode and exposes failed requested mode");
                Check(h.Control<TextBox>("NetworkIdInput").Text == "fedcba9876543210", "failed network switch preserves new room draft for explicit Save");
            }
            using (var h = new Harness(root, "pending-launch-mode", 3, 3, false))
            {
                h.ChooseBin(); h.Save();
                int writes = h.PrepareCalls; string original = h.FilesSignature;
                h.Set("launchPendingUntil", DateTime.UtcNow.AddMinutes(1)); h.Refresh();
                Check(!h.Toggle.IsEnabled, "pending launch disables mode toggle");
                h.ChangeMode(false);
                Check(h.PrepareCalls == writes && h.FilesSignature == original && !h.TargetSolo && h.Toggle.IsChecked == true, "mode handler rejects pending launch and restores toggle");
                h.Set("launchPendingUntil", DateTime.MinValue); h.ChangeMode(false);
                Check(h.PrepareCalls == writes + 1 && h.Settings.StandaloneMode, "mode change works after pending launch expires");
            }
        }
        private static void TestMetadataOnlySave(string root)
        {
            using (var h = new Harness(root, "metadata-only", 1, 1, false))
            {
                string original = h.FilesSignature;
                h.Control<TextBox>("RoomNameInput").Text = "仅房间信息";
                h.Save();
                Check(h.PrepareCalls == 0 && h.InspectCalls == 0 && h.FilesSignature == original, "room-only Save without bin leaves all fixture game files unchanged");
                Check(h.RoomPersistCalls == 1 && h.LaunchPersistCalls == 1 && h.Room.RoomName == "仅房间信息", "room-only Save persists metadata once");
                Check(!h.Ready && h.LaunchReason != null && h.Control<Button>("ExportButton").IsEnabled, "saved metadata allows room export but does not allow game launch");
            }
        }
        private static void TestIncompatiblePatchAfterSave(string root)
        {
            using (var h = new Harness(root, "patch-compatibility-change", 2, 2, false))
            {
                h.ChooseBin();
                h.Control<CheckBox>("ConnectFixCheck").IsChecked = true; h.Save();
                Check(h.Settings.UseConnect && h.PersistedLaunch.Last().UseConnect, "compatible subnet 139 Save persists selected connection patch");
                h.Control<TextBox>("SubnetInput").Text = "192.168.138.0";
                h.CanUseConnect = false;
                h.Save();
                Check(h.Room.Subnet == "192.168.138.0" && !h.Control<CheckBox>("ConnectFixCheck").IsEnabled && h.Control<CheckBox>("ConnectFixCheck").IsChecked == false, "saving incompatible subnet cancels and disables obsolete connection patch");
                Check(!h.Settings.UseConnect && !h.PersistedLaunch.Last().UseConnect, "incompatible patch is false in committed and persisted preferences");
                Check(h.Text("SettingsNote").Contains("不兼容") && h.Text("SettingsNote").Contains("取消"), "Save reports cancellation of incompatible patch");
                Check(h.Ready && !h.Pending && !h.Visible("SaveButton"), "adjusted patch does not leave pending-save deadlock");
                h.OnlineSnapshot();
                Check(h.LaunchReason == null && h.Control<Button>("LaunchHomeButton").IsEnabled, "saved compatible configuration permits launch after patch cancellation");
            }
        }
        private static void TestSoloWithoutNetwork(string root)
        {
            using (var h = new Harness(root, "solo-no-network", 1, 2, true))
            {
                h.ChooseBin(); h.ChangeMode(false);
                Check(h.PrepareCalls == 1 && h.LaunchPersistCalls == 1 && h.IniSlot == 1 && h.TestRole == 0, "solo without network commits fixture roles");
                Check(h.Ready && !h.Pending && h.LaunchReason == null && h.Control<Button>("LaunchHomeButton").IsEnabled, "saved standalone setup requires no online room");
            }
        }
        private static void TestFailures(string root)
        {
            using (var h = new Harness(root, "failures", 3, 3, false))
            {
                h.FailTestRead = true; string original = h.FilesSignature;
                h.ChooseBin();
                Check(h.Field<FilePreparationState>("iniState") == FilePreparationState.Ready && h.Field<FilePreparationState>("batState") == FilePreparationState.Ready && h.Field<FilePreparationState>("testState") == FilePreparationState.Error, "TEST read failure preserves successful INI and BAT rows");
                Check(!h.Ready && h.LaunchReason != null && h.Visible("RetryFilesButton"), "partial read blocks launch and offers read retry");
                h.Process(false); h.AssertNoWrites(original, "failed read retry");
                h.FailTestRead = false; h.Process(false);
                Check(h.Field<FilePreparationState>("testState") == FilePreparationState.Ready && h.Pending, "read retry recovers TEST row but still needs first Save");
                h.FailBeforeWrite = true; h.ChangeMode(false);
                Check(!h.Ready && h.Pending && h.FilesSignature == original && h.RoomPersistCalls == 0 && h.LaunchPersistCalls == 0, "save failure before writes blocks launch and preserves fixture and preferences");
                Check(h.Text("ModeHint").Contains("保存失败"), "mode-only pre-write failure is shown in machine status");
                string failure = h.Text("ModeHint"); h.Refresh();
                Check(h.Text("ModeHint") == failure, "button refresh preserves mode-only pre-write failure");
                h.FailBeforeWrite = false; h.ThrowAfterIni = true; h.Save();
                Check(h.IniSlot == 1 && h.TestRole == 1 && !h.Ready && h.Pending, "partial Save leaves explicit pending state after INI changed but TEST did not");
                Check(h.Text("ModeHint").Contains("部分") && h.Text("ModeHint").Contains("保存失败") && !h.Control<Button>("LaunchHomeButton").IsEnabled, "partial Save reports possible file change and blocks launch");
                int commits = h.PrepareCalls; original = h.FilesSignature;
                h.ThrowAfterIni = false; h.Process(false);
                Check(h.PrepareCalls == commits && h.FilesSignature == original && h.Pending, "read retry after partial Save never completes the missing write");
                h.FailPersistence = true; h.Save();
                Check(h.IniSlot == 1 && h.TestRole == 0 && h.Pending && !h.Ready, "preference failure after file commit keeps synchronized files pending");
                Check(h.Text("ModeHint").Contains("部分") && h.TargetSolo, "preference failure reports file update and preserves requested mode");
                h.FailPersistence = false; h.Save();
                Check(h.Ready && !h.Pending && h.Settings.StandaloneMode, "explicit Save recovers preference failure");
            }
        }
        private static void TestBusy(string root, bool saving)
        {
            using (var h = new Harness(root, saving ? "busy-save" : "busy-read", 2, 2, false))
            {
                h.Control<TextBox>("BinPathInput").Text = h.DirectoryPath;
                h.GateRead = !saving; h.GateSave = saving;
                Task task = saving ? h.StartMode(false) : h.StartProcess(false);
                Pump(() => h.Text("TestReadStatus").Contains("模拟等待"), 15000);
                Check(h.Busy && !task.IsCompleted, (saving ? "save" : "read") + " progress reaches Dispatcher while operation is pending");
                foreach (string name in new[] { "NetworkModeToggle", "SaveButton", "ImportButton", "ExportButton", "BrowseBinButton", "RefreshFilesButton", "RetryFilesButton", "RefreshButton", "PeersButton", "FirewallButton", "DiagnosticsButton", "LaunchHomeButton", "SlotInput", "SettingsFields" })
                    Check(!h.Control<UIElement>(name).IsEnabled, (saving ? "save" : "read") + " busy disables " + name);
                Check(h.LaunchReason != null, "busy operation blocks launch");
                if (saving) Check(h.Text("ModeHint").Contains("更新中"), "mode save progress remains visible while worker is pending");
                if (saving) CapturePair(h, "mode-save-progress", 0, false);
                int reads = h.InspectCalls, writes = h.PrepareCalls;
                bool target = h.TargetSolo;
                Await(h.StartProcess(false)); Await(h.StartSave()); Await(h.StartMode(!h.Toggle.IsChecked.GetValueOrDefault()));
                Check(h.InspectCalls == reads && h.PrepareCalls == writes && h.TargetSolo == target, "busy controller rejects overlapping refresh, Save and mode change");
                h.Release.Set(); Await(task);
                Check(!h.Busy && h.Control<Button>("BrowseBinButton").IsEnabled && h.Toggle.IsEnabled, "operation completion restores editable controls");
            }
        }

        private static void CheckControls(Harness h)
        {
            Check(String.Join("/", h.Control<TabControl>("MainTabs").Items.Cast<TabItem>().Select(t => t.Header.ToString()).ToArray()) == "联机概览/房间设置/游戏设置/帮助与诊断", "four navigation tabs retained");
            foreach (string old in new[] { "IniPathInput", "BatPathInput", "BrowseIniButton", "BrowseBatButton", "ReadBatButton", "PreviewButton", "ApplyButton", "ConfigPreview", "RestoreHostButton", "ReturnRoomButton", "LaunchSetupButton", "NextStepButton" })
                Check(h.Window.FindName(old) == null, "obsolete control absent: " + old);
            Check(h.Control<TextBox>("BinPathInput").IsReadOnly, "bin path is read-only and chosen with a folder button");
            Check(Convert.ToString(h.Control<Button>("RetryFilesButton").Content) == "重试读取", "retry action is labelled read-only");
            var refresh = h.Control<Button>("RefreshFilesButton"); var browse = h.Control<Button>("BrowseBinButton");
            Check(Convert.ToString(refresh.Content).Contains("刷新"), "file refresh button uses refresh label");
            Check(refresh.Parent == browse.Parent && Grid.GetColumn(refresh) > Grid.GetColumn(browse), "file refresh button is directly alongside bin browse control");
            var save = h.Control<Button>("SaveButton"); var import = h.Control<Button>("ImportButton"); var panel = save.Parent as Panel;
            Check(panel != null && save.Parent == import.Parent && panel.Children.IndexOf(save) == panel.Children.IndexOf(import) + 1, "Save remains directly right of Import in same action row");
            Check(Descendants<Button>(h.Surface).Count(b => Convert.ToString(b.Content) == "启动游戏") == 1, "exactly one launch button exists");
            Check(h.Control<Button>("LaunchHomeButton").Parent is Grid && ((FrameworkElement)((Grid)h.Control<Button>("LaunchHomeButton").Parent).Parent).Name == "LaunchBar", "single launch action stays in fixed footer");
            Check(h.Toggle.Focusable && !String.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(h.Toggle)), "mode switch has keyboard and accessible name");
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            T match = root as T; if (match != null) yield return match;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var node = child as DependencyObject;
                if (node != null) foreach (T descendant in Descendants<T>(node)) yield return descendant;
            }
        }
        private static void LayoutMatrix(Harness h, string prefix)
        { foreach (int width in new[] { 1000, 720 }) for (int tab = 0; tab < 4; tab++) Render(h, prefix + "-layout-" + width + "-tab" + tab, width, width == 1000 ? 820 : 680, tab, true, false); }
        private static void CapturePair(Harness h, string name, int tab, bool bottom)
        { Render(h, name + "-1000", 1000, 820, tab, bottom, true); Render(h, name + "-720", 720, 680, tab, bottom, true); }
        private static void Render(Harness h, string name, int width, int height, int tab, bool bottom, bool save)
        {
            var tabs = h.Control<TabControl>("MainTabs"); tabs.SelectedIndex = tab;
            var scroll = (ScrollViewer)((TabItem)tabs.Items[tab]).Content; scroll.ScrollToTop();
            h.Surface.Measure(new Size(width, height)); h.Surface.Arrange(new Rect(0, 0, width, height)); h.Surface.UpdateLayout();
            double top = scroll.TranslatePoint(new Point(0, 0), tabs).Y;
            if (bottom) { scroll.ScrollToEnd(); h.Surface.UpdateLayout(); }
            Check(scroll.Margin.Top == 16 && Math.Abs(scroll.TranslatePoint(new Point(0, 0), tabs).Y - top) < 0.5, name + " keeps 16px outside scrolling content");
            Check(!scroll.Focusable && scroll.FocusVisualStyle == null, name + " scroll has no focus outline");
            Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, name + " has no horizontal overflow");
            var launch = h.Control<Button>("LaunchHomeButton"); Point point = launch.TranslatePoint(new Point(0, 0), h.Surface);
            Check(point.X >= 0 && point.Y >= 0 && point.X + launch.ActualWidth <= width && point.Y + launch.ActualHeight <= height, name + " fixed launch remains visible");
            string file = null;
            if (save)
            {
                file = Path.Combine(output, name + ".png"); var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(h.Surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(file)) encoder.Save(stream);
            }
            report.Layouts.Add(new LayoutResult { Name = name, Width = width, Height = height, Tab = tab, Scroll = scroll.VerticalOffset, OuterGap = scroll.Margin.Top, Role = h.Text("RoleBadgeText"), Image = file });
        }
        internal static void Await(Task task)
        {
            if (task != null) { Pump(() => task.IsCompleted, 20000); task.GetAwaiter().GetResult(); }
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        }
        internal static void Pump(Func<bool> done, int timeout)
        {
            if (done()) return;
            var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddMilliseconds(timeout); bool expired = false;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += delegate { if (done()) frame.Continue = false; else if (DateTime.UtcNow >= deadline) { expired = true; frame.Continue = false; } };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            if (expired) throw new TimeoutException("UI workflow did not complete while pumping Dispatcher.");
        }
        internal static string Hash(string path) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
        internal static string Signature(string path) { return File.Exists(path) ? Hash(path) + "|" + File.GetLastWriteTimeUtc(path).Ticks : "MISSING"; }
        internal static void Property(object target, string name, object value) { target.GetType().GetProperty(name).GetSetMethod(true).Invoke(target, new[] { value }); }

        private sealed class Harness : IDisposable
        {
            internal readonly Window Window;
            internal readonly LauncherController Controller;
            internal readonly Border Surface;
            internal readonly string DirectoryPath, Ini, Bat, TestFile, RoomPath;
            internal readonly List<RoomConfig> PersistedRooms = new List<RoomConfig>();
            internal readonly List<GameLaunchSettings> PersistedLaunch = new List<GameLaunchSettings>();
            internal readonly List<string> Activity = new List<string>();
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            internal int InspectCalls, PrepareCalls, RoomPersistCalls, LaunchPersistCalls;
            internal bool FailTestRead, FailBeforeWrite, ThrowAfterIni, FailPersistence, GateRead, GateSave;
            internal bool CanUseConnect = true;
            internal Harness(string root, string name, int slot, int existing, bool noNetwork)
            {
                DirectoryPath = Path.Combine(root, name); Directory.CreateDirectory(DirectoryPath);
                Ini = Path.Combine(DirectoryPath, "segatools.ini"); Bat = Path.Combine(DirectoryPath, "start.bat"); TestFile = Path.Combine(DirectoryPath, "fixture-test-role.bin"); RoomPath = Path.Combine(DirectoryPath, "room.json");
                File.WriteAllText(Ini, "[netenv]\r\nenable=1\r\naddrSuffix=" + (10 + existing) + "\r\n[keychip]\r\nsubnet=192.168.139.0\r\n[gpio]\r\ndipsw1=" + (existing == 1 ? 1 : 0) + "\r\n", new UTF8Encoding(false));
                File.WriteAllText(Bat, "@echo off\r\nrem isolated fixture; never executed\r\n", Encoding.ASCII);
                File.WriteAllBytes(TestFile, new byte[] { 1, (byte)(existing == 1 ? 0 : 1) });
                Window = Program.CreateWindow(); Controller = new LauncherController(Window, true);
                Set("settingsPath", RoomPath);
                Set("persistRoom", new Action<RoomConfig, string>(PersistRoom)); Set("persistLaunchSettings", new Action<GameLaunchSettings>(PersistLaunch));
                Set("appendActivity", new Action<string>(s => Activity.Add(s)));
                Set("inspectFiles", new Func<RoomConfig, string, bool, Action<string, FilePreparationState, string>, GameFilesResult>(Inspect));
                Set("prepareFiles", new Func<RoomConfig, string, bool, Action<string, FilePreparationState, string>, GameFilesResult>(Prepare));
                Set("config", new RoomConfig { RoomName = "隔离测试房间", NetworkId = noNetwork ? "" : "0123456789abcdef", Subnet = "192.168.139.0", Slot = slot });
                Set("launchSettings", new GameLaunchSettings()); Set("last", null); Set("session", null); Set("launchInspection", null); Set("inspectedFiles", null);
                Set("targetStandalone", false); Set("modeExplicit", false); Set("filesReady", false); Set("retryFiles", false); Set("needsSave", false);
                Set("updatingLaunch", true); Control<CheckBox>("ConnectFixCheck").IsChecked = false; Control<CheckBox>("DistributionFixCheck").IsChecked = false; Set("updatingLaunch", false);
                Call("FillEditor", Room); Control<TextBox>("BinPathInput").Text = ""; Call("SyncModeToggle"); Call("ShowConfigSummary");
                var content = (UIElement)Window.Content; Window.Content = null;
                Surface = new Border { Resources = Window.Resources, Background = Window.Background, Child = content };
                System.Windows.Documents.TextElement.SetFontFamily(Surface, Window.FontFamily); System.Windows.Documents.TextElement.SetFontSize(Surface, Window.FontSize); System.Windows.Documents.TextElement.SetForeground(Surface, Window.Foreground);
                Set("preview", false); Refresh();
            }
            internal RoomConfig Room { get { return Field<RoomConfig>("config"); } }
            internal GameLaunchSettings Settings { get { return Field<GameLaunchSettings>("launchSettings"); } }
            internal bool Ready { get { return Field<bool>("filesReady"); } }
            internal bool Pending { get { return (bool)Call("HasPendingChanges"); } }
            internal bool TargetSolo { get { return Field<bool>("targetStandalone"); } }
            internal bool Busy { get { return Field<bool>("busy"); } }
            internal int IniSlot { get { return SetupActions.InspectGameConfig(Ini).Slot; } }
            internal int TestRole { get { return File.ReadAllBytes(TestFile)[1]; } }
            internal string FilesSignature { get { return Signature(Ini) + "|" + Signature(Bat) + "|" + Signature(TestFile); } }
            internal ToggleButton Toggle { get { return Control<ToggleButton>("NetworkModeToggle"); } }
            internal string LaunchReason { get { return (string)Call("LaunchBlockReason", false); } }
            internal T Control<T>(string name) where T : class { var result = Window.FindName(name) as T; if (result == null) throw new InvalidOperationException("Missing control " + name); return result; }
            internal string Text(string name) { return Control<TextBlock>(name).Text; }
            internal T Field<T>(string name) { return (T)Controller.GetType().GetField(name, Private).GetValue(Controller); }
            internal void Set(string name, object value) { Controller.GetType().GetField(name, Private).SetValue(Controller, value); }
            internal object Call(string name, params object[] args)
            { try { return Controller.GetType().GetMethod(name, Private).Invoke(Controller, args); } catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; } }
            internal bool Visible(string name)
            {
                DependencyObject node = Control<DependencyObject>(name);
                while (node != null) { var e = node as UIElement; if (e != null && e.Visibility != Visibility.Visible) return false; var f = node as FrameworkElement; node = f != null && f.Parent != null ? f.Parent : VisualTreeHelper.GetParent(node); }
                return true;
            }
            internal void Refresh() { Call("UpdateButtons"); }
            internal Task StartProcess(bool detect) { return (Task)Call("ProcessFiles", detect); }
            internal Task StartSave() { return (Task)Call("SaveEditor"); }
            internal void Save() { Await(StartSave()); }
            internal void Process(bool detect) { Await(StartProcess(detect)); }
            internal void RefreshFiles() { Control<Button>("RefreshFilesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Await(Field<Task>("currentOperation")); }
            internal void ChooseBin() { Control<TextBox>("BinPathInput").Text = DirectoryPath; Process(true); }
            internal void ChangeMode(bool online)
            { Await(StartMode(online)); }
            internal Task StartMode(bool online)
            { Set("updatingMode", true); Toggle.IsChecked = online; Set("updatingMode", false); return (Task)Call("ModeChanged"); }
            internal void AssertNoWrites(string expected, string action)
            { Check(PrepareCalls == 0 && RoomPersistCalls == 0 && LaunchPersistCalls == 0 && FilesSignature == expected, action + " performs no configuration or preference writes"); }
            internal void OnlineSnapshot()
            {
                Call("ApplySnapshot", new ZeroTierSession.Snapshot { Installed = true, ServiceRunning = true, NetworkOk = true, CorrectAddress = true, SettingsOk = true, LocalIp = Room.ExpectedIp, Address = Room.ExpectedIp + "/24", NodeId = "0123456789", Service = "正在运行", Network = "OK / PRIVATE", Updated = "隔离测试" }); Refresh();
            }
            private static RoomConfig Clone(RoomConfig x) { return new RoomConfig { RoomName = x.RoomName, NetworkId = x.NetworkId, Subnet = x.Subnet, Slot = x.Slot, GameIniPath = x.GameIniPath }; }
            private void PersistRoom(RoomConfig value, string path)
            { if (!String.Equals(Path.GetFullPath(path), RoomPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Room persistence escaped fixture."); RoomPersistCalls++; PersistedRooms.Add(Clone(value)); }
            private void PersistLaunch(GameLaunchSettings value)
            {
                LaunchPersistCalls++; if (FailPersistence) throw new IOException("模拟启动设置保存失败");
                PersistedLaunch.Add(new GameLaunchSettings { BinDirectory = value.BinDirectory, IniPath = value.IniPath, BatPath = value.BatPath, PreparedIniHash = value.PreparedIniHash, StandaloneMode = value.StandaloneMode, StandaloneIniPath = value.StandaloneIniPath, StandaloneIniHash = value.StandaloneIniHash, StandaloneSubnet = value.StandaloneSubnet, UseConnect = value.UseConnect, UseDistribution = value.UseDistribution });
            }
            private void AssertPath(string bin)
            { if (!String.Equals(Path.GetFullPath(bin).TrimEnd('\\'), DirectoryPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("File workflow escaped fixture."); }
            private void Progress(Action<string, FilePreparationState, string> progress, bool gate)
            {
                progress("ini", FilePreparationState.Working, "模拟读取配置…"); progress("bat", FilePreparationState.Working, "模拟读取脚本…"); progress("test", FilePreparationState.Working, "模拟等待：读取机器状态…");
                if (gate && !Release.Wait(15000)) throw new TimeoutException("Fixture gate timed out.");
            }
            private GameFilesResult Inspect(RoomConfig room, string bin, bool solo, Action<string, FilePreparationState, string> progress)
            {
                AssertPath(bin); Interlocked.Increment(ref InspectCalls); Progress(progress, GateRead);
                return Result(room, solo, progress, FailTestRead);
            }
            private GameFilesResult Prepare(RoomConfig room, string bin, bool solo, Action<string, FilePreparationState, string> progress)
            {
                AssertPath(bin); Interlocked.Increment(ref PrepareCalls); Progress(progress, GateSave);
                if (FailBeforeWrite) { progress("test", FilePreparationState.Error, "模拟保存前校验失败"); throw new IOException("模拟保存前校验失败"); }
                var target = Clone(room); target.Slot = solo ? 1 : room.Slot; target.GameIniPath = Ini;
                if (String.IsNullOrEmpty(target.Subnet)) target.Subnet = "192.168.139.0";
                var current = SetupActions.InspectGameConfig(Ini);
                if (solo) SetupActions.RestoreHostConfigChecked(target, current.Hash); else SetupActions.ApplyGameConfigChecked(target, current.Hash);
                if (ThrowAfterIni)
                {
                    progress("test", FilePreparationState.Error, "模拟机器状态写入失败");
                    var ctor = typeof(GameFilePreparationException).GetConstructor(Private, null, new[] { typeof(string), typeof(bool), typeof(RoomConfig), typeof(Exception) }, null);
                    throw (Exception)ctor.Invoke(new object[] { "模拟机器状态写入失败", solo, target, new IOException("fixture partial write") });
                }
                File.WriteAllBytes(TestFile, new byte[] { 1, (byte)(solo || room.Slot == 1 ? 0 : 1) });
                return Result(room, solo, progress, false);
            }
            private GameFilesResult Result(RoomConfig room, bool solo, Action<string, FilePreparationState, string> progress, bool testFailure)
            {
                var current = SetupActions.InspectGameConfig(Ini); var target = Clone(room); target.Slot = solo ? 1 : room.Slot; target.GameIniPath = Ini;
                if (String.IsNullOrEmpty(target.Subnet)) target.Subnet = current.Subnet;
                var launch = new GameLaunchInspection();
                Property(launch, "BatPath", Bat); Property(launch, "GameDirectory", DirectoryPath); Property(launch, "BatHash", Hash(Bat)); Property(launch, "Summary", "隔离启动脚本");
                Property(launch, "CanUseConnect", CanUseConnect); Property(launch, "CanUseDistribution", true); Property(launch, "ConnectReason", CanUseConnect ? "兼容" : "当前网段不兼容"); Property(launch, "DistributionReason", "兼容");
                var role = new TestRoleInspection(); Property(role, "SavePath", TestFile); Property(role, "BinDirectory", DirectoryPath); Property(role, "FileHash", Hash(TestFile)); Property(role, "Role", TestRole); Property(role, "Group", 1);
                progress("ini", FilePreparationState.Ready, "已读取配置：" + current.Summary); progress("bat", FilePreparationState.Ready, "兼容性检查完成");
                progress("test", testFailure ? FilePreparationState.Error : FilePreparationState.Ready, testFailure ? "模拟机器状态读取失败" : role.RoleName + " · A");
                var result = new GameFilesResult(); Property(result, "Config", current); Property(result, "Launch", launch); Property(result, "Target", target); Property(result, "Standalone", solo); Property(result, "IniHash", current.Hash); Property(result, "BinDirectory", DirectoryPath); Property(result, "TestRole", testFailure ? null : role);
                return result;
            }
            public void Dispose()
            {
                Release.Set(); var operation = Field<Task>("currentOperation"); if (operation != null && !operation.IsCompleted) { try { Await(operation); } catch { } }
                Set("preview", true); Field<DispatcherTimer>("timer").Stop(); Set("allowClose", true); Window.Close(); Release.Dispose();
                foreach (string file in new[] { Ini, Bat, TestFile, RoomPath }) if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(DirectoryPath) && Directory.GetFileSystemEntries(DirectoryPath).Length == 0) Directory.Delete(DirectoryPath, false);
            }
        }
    }
}
