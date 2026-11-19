using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class PatchInstaller
{
    private const string Title = "Smash Bandits Racing 社区补丁";
    private const string AppId = "Hutch.SmashBanditsRacing_wv0sdxekm2bhy!App";
    private const string Notice = "ThirdPartyNotices.txt";
    private static readonly string[] Payload = {
        "CenterGameViewport.exe", "GarageViewport.dll", "PatchGarage.exe", "Mono.Cecil.dll"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            if (args.Length != 1) return 2;
            try
            {
                using (var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                {
                    output.AutoFlush = true;
                    switch (args[0].ToLowerInvariant())
                    {
                        case "--check": return Complete("Check", Run("Check", output.WriteLine), output.WriteLine);
                        case "--install": return Complete("Install", Run("Install", output.WriteLine), output.WriteLine);
                        case "--launch": return Complete("Launch", Run("Launch", output.WriteLine), output.WriteLine);
                        case "--uninstall": return Complete("Uninstall", Run("Uninstall", output.WriteLine), output.WriteLine);
                        case "--licenses": output.WriteLine(ReadResource(Notice)); return 0;
                        default: return 2;
                    }
                }
            }
            catch (Exception error)
            {
                using (var errors = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)))
                    errors.WriteLine(error.Message);
                return 1;
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerForm());
        return 0;
    }

    private static string ReadResource(string name)
    {
        using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(name)))
            return reader.ReadToEnd();
    }

    private sealed class InstallerResult
    {
        public int ExitCode;
        public bool CanLaunch;
    }

    private static InstallerResult Run(string action, Action<string> log)
    {
        string folder = Path.Combine(Path.GetTempPath(), "sbr-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            foreach (string name in Payload)
            {
                using (var source = assembly.GetManifestResourceStream(name))
                using (var target = new FileStream(Path.Combine(folder, name), FileMode.CreateNew))
                    source.CopyTo(target);
            }

            // Pass only our embedded command and pass the temp path as data. Do not change execution policy.
            string command = "& {\n" + ReadResource("Install-Game.ps1") +
                "\n} -Action " + action + " -PayloadPath $env:SBR_PATCH_PAYLOAD";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var start = new ProcessStartInfo {
                FileName = Path.Combine(windows, @"System32\WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded,
                WorkingDirectory = folder,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["SBR_PATCH_PAYLOAD"] = folder;
            using (var process = Process.Start(start))
            {
                Task<string> errorOutput = process.StandardError.ReadToEndAsync();
                var result = new InstallerResult();
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line == "APPID=" + AppId)
                        result.CanLaunch = true;
                    else if (!String.IsNullOrWhiteSpace(line))
                        log(line);
                }
                process.WaitForExit();
                result.ExitCode = process.ExitCode;
                string errors = errorOutput.GetAwaiter().GetResult().Trim();
                if (errors.Length != 0)
                    log(errors);
                return result;
            }
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { log("未能清理临时文件：" + folder); }
            catch (UnauthorizedAccessException) { log("未能清理临时文件：" + folder); }
        }
    }

    private static int Complete(string action, InstallerResult result, Action<string> log)
    {
        if (result.ExitCode != 0 || action != "Launch") return result.ExitCode;
        if (!result.CanLaunch) throw new InvalidOperationException("Windows 未确认修改版的启动项。");
        var activation = (IApplicationActivationManager)new ApplicationActivationManager();
        uint processId;
        int activationResult = activation.ActivateApplication(AppId, null, 2, out processId);
        if (activationResult < 0)
            throw new InvalidOperationException("Windows 无法启动修改版。错误代码 0x" + activationResult.ToString("X8"));
        log("Windows 已启动修改版（PID " + processId + "）。");
        return 0;
    }

    private sealed class InstallerForm : Form
    {
        private readonly FlowLayoutPanel actions = new FlowLayoutPanel();
        private readonly TextBox output = new TextBox();
        private readonly BackgroundWorker worker = new BackgroundWorker();

        public InstallerForm()
        {
            Text = Title;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 328);
            MinimumSize = new Size(590, 310);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
                Padding = new Padding(14) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var intro = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 8),
                Text = "先安装 Microsoft Store 原版（1.1.1.10），再点击「安装修改版」。原版和原版存档不会被覆盖。" };
            actions.AutoSize = true;
            actions.Dock = DockStyle.Fill;
            actions.WrapContents = false;
            AddButton("检查", "Check");
            AddButton("安装修改版", "Install");
            AddButton("启动修改版", "Launch");
            AddButton("卸载修改版", "Uninstall");
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.Dock = DockStyle.Fill;
            output.AccessibleName = "操作日志";
            var notices = new LinkLabel { Text = "第三方许可", AutoSize = true, Margin = new Padding(3, 8, 3, 0) };
            notices.LinkClicked += (sender, e) => MessageBox.Show(this, ReadResource(Notice), "Mono.Cecil");
            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(actions, 0, 1);
            layout.Controls.Add(output, 0, 2);
            layout.Controls.Add(notices, 0, 3);
            Controls.Add(layout);

            worker.WorkerReportsProgress = true;
            worker.DoWork += (sender, e) => {
                var action = (string)e.Argument;
                e.Result = new Tuple<string, InstallerResult>(action, Run(action, line => worker.ReportProgress(0, line)));
            };
            worker.ProgressChanged += (sender, e) => Append((string)e.UserState);
            worker.RunWorkerCompleted += (sender, e) => {
                try
                {
                    if (e.Error != null) throw e.Error;
                    var operation = (Tuple<string, InstallerResult>)e.Result;
                    if (Complete(operation.Item1, operation.Item2, Append) != 0)
                        Append("操作未完成。请查看上方提示。");
                }
                catch (Exception error) { Append(error.Message); }
                finally { actions.Enabled = true; UseWaitCursor = false; }
            };
            Shown += (sender, e) => StartAction("Check");
            FormClosing += (sender, e) => { if (worker.IsBusy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        }

        private void AddButton(string title, string action)
        {
            var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(95, 33) };
            button.Click += (sender, e) => {
                if (action == "Uninstall" && MessageBox.Show(this,
                    "将移除修改版及其本地存档。商店原版和原版存档不会被移除。继续吗？", Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                StartAction(action);
            };
            actions.Controls.Add(button);
        }

        private void StartAction(string action)
        {
            if (worker.IsBusy) return;
            output.Clear();
            Append("正在处理，请稍候……");
            actions.Enabled = false;
            UseWaitCursor = true;
            worker.RunWorkerAsync(action);
        }

        private void Append(string line)
        {
            output.AppendText(line + Environment.NewLine);
        }
    }
}
