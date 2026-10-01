using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SchoolScreenBrowser
{
    internal sealed class BrowserForm : Form
    {
        private readonly string _pipeName;
        private readonly WebView2 _webView;
        private NamedPipeClientStream _pipe;
        private BinaryWriter _writer;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingCommands = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private bool _webReady;
        private bool _capturing;

        public BrowserForm(string pipeName)
        {
            _pipeName = pipeName;
            Text = "On-Together School Screen";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-1200, -1200);
            ClientSize = new Size(768, 384);
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Shown += async (_, __) => await InitializeAsync();
            FormClosing += (_, __) => _cancel.Cancel();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: omit from Alt+Tab.
                parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE: keep it from taking focus.
                parameters.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW: do not show as an app window.
                return parameters;
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(15000);
                _writer = new BinaryWriter(_pipe, Encoding.UTF8, true);
                _ = Task.Run(ReadCommandsAsync);

                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnTogetherSchoolScreen", "WebView2");
                Directory.CreateDirectory(appData);
                var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling");
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, appData, options);
                await _webView.EnsureCoreWebView2Async(environment);
                string www = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "www");
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("schoolvideo.local", www, CoreWebView2HostResourceAccessKind.Allow);
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _webView.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    _webReady = e.IsSuccess;
                    string command;
                    while (_webReady && _pendingCommands.TryDequeue(out command))
                        _ = ExecuteCommandAsync(command);
                };
                _webView.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    try { SendPacket((byte)'S', Encoding.UTF8.GetBytes(e.TryGetWebMessageAsString())); }
                    catch { }
                };
                _webView.Source = new Uri("https://schoolvideo.local/index.html");
                _ = CaptureLoopAsync();
            }
            catch (Exception ex)
            {
                try { SendPacket((byte)'E', Encoding.UTF8.GetBytes(ex.GetBaseException().Message)); }
                catch { }
                CloseAfterDelay();
            }
        }

        private async void CloseAfterDelay()
        {
            await Task.Delay(10000);
            if (!IsDisposed) BeginInvoke((Action)Close);
        }

        private async Task ReadCommandsAsync()
        {
            try
            {
                using (var reader = new StreamReader(_pipe, Encoding.UTF8, false, 1024, true))
                {
                    while (!_cancel.IsCancellationRequested && _pipe.IsConnected)
                    {
                        string line = await reader.ReadLineAsync();
                        if (line == null) break;
                        if (line == "CLOSE") { BeginInvoke((Action)Close); break; }
                        await ExecuteCommandOnUiThreadAsync(line);
                    }
                }
            }
            catch { }
        }

        private Task ExecuteCommandOnUiThreadAsync(string command)
        {
            var completion = new TaskCompletionSource<bool>();
            if (IsDisposed || !IsHandleCreated)
            {
                completion.TrySetResult(false);
                return completion.Task;
            }
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (!_webReady) _pendingCommands.Enqueue(command);
                    else await ExecuteCommandAsync(command);
                    completion.TrySetResult(true);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            }));
            return completion.Task;
        }

        private async Task ExecuteCommandAsync(string command)
        {
            string[] parts = command.Split('\t');
            string type = parts[0];
            string script;
            if (type == "OPEN" && parts.Length >= 3)
                script = "schoolScreenCommand('open'," + JsString(parts[1]) + "," + Number(parts[2]) + "," + (parts.Length >= 4 ? Number(parts[3]) : "0") + ");";
            else if (type == "PLAY") script = "schoolScreenCommand('play');";
            else if (type == "PAUSE") script = "schoolScreenCommand('pause');";
            else if (type == "SEEK" && parts.Length >= 2) script = "schoolScreenCommand('seek'," + Number(parts[1]) + ");";
            else if (type == "VOLUME" && parts.Length >= 2) script = "schoolScreenCommand('volume'," + Number(parts[1]) + ");";
            else if (type == "LOOKUP" && parts.Length >= 2) script = "schoolScreenLookupTitle(" + JsString(parts[1]) + ");";
            else if (type == "RESET_LOOKUPS") script = "schoolScreenResetTitleLookups();";
            else if (type == "CLEAR") script = "schoolScreenCommand('clear');";
            else return;

            if (_webView.CoreWebView2 == null) return;
            await _webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private async Task CaptureLoopAsync()
        {
            long framePeriod = Stopwatch.Frequency / 30;
            long nextFrame = Stopwatch.GetTimestamp();
            while (!_cancel.IsCancellationRequested && !IsDisposed)
            {
                try
                {
                    if (!_capturing && _webView.CoreWebView2 != null && _webView.IsHandleCreated)
                    {
                        _capturing = true;
                        using (var stream = new MemoryStream())
                        {
                            await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
                            SendPacket((byte)'F', stream.ToArray());
                        }
                        _capturing = false;
                    }
                }
                catch { _capturing = false; }

                nextFrame += framePeriod;
                long remainingTicks = nextFrame - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0) nextFrame = Stopwatch.GetTimestamp();
                else
                {
                    int delayMs = Math.Max(1, (int)Math.Ceiling(remainingTicks * 1000.0 / Stopwatch.Frequency));
                    await Task.Delay(delayMs, _cancel.Token).ContinueWith(_ => { });
                }
            }
        }

        private void SendPacket(byte type, byte[] payload)
        {
            if (_writer == null || payload == null || payload.Length > 4 * 1024 * 1024) return;
            _writeLock.Wait();
            try
            {
                _writer.Write(type);
                _writer.Write(payload.Length);
                _writer.Write(payload);
                _writer.Flush();
            }
            catch { }
            finally { _writeLock.Release(); }
        }

        private static string Number(string value)
        {
            double parsed;
            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed)
                ? parsed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "0";
        }

        private static string JsString(string value)
        {
            return "'" + value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "") + "'";
        }
    }
}
