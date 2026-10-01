using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

if (args.Length < 2) throw new ArgumentException("Usage: BrowserSmoke <installed-SchoolScreenBrowser.exe> <report-directory> [video-id]");
string browserPath = Path.GetFullPath(args[0]);
string reportDirectory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(reportDirectory);
string pipeName = "SchoolScreenPackageCheck_" + Guid.NewGuid().ToString("N");
using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var start = new ProcessStartInfo(browserPath, pipeName)
{
    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
    WorkingDirectory = Path.GetDirectoryName(browserPath)!,
};
start.Environment["WEBVIEW2_USER_DATA_FOLDER"] = Path.Combine(reportDirectory, "webview-profile");
using var browser = Process.Start(start) ?? throw new Exception("Browser helper did not start.");
bool pageReady = false;
byte[]? frame = null;
string? playingState = null;
try
{
    await pipe.WaitForConnectionAsync(deadline.Token);
    await pipe.WriteAsync(Encoding.UTF8.GetBytes("VOLUME\t0\n"), deadline.Token);
    if (args.Length >= 3)
        await pipe.WriteAsync(Encoding.UTF8.GetBytes("OPEN\t" + args[2] + "\t0\t1\n"), deadline.Token);
    while (!pageReady || frame == null || (args.Length >= 3 && playingState == null))
    {
        byte[] header = new byte[5];
        await pipe.ReadExactlyAsync(header, deadline.Token);
        int length = BitConverter.ToInt32(header, 1);
        if (length < 0 || length > 4 * 1024 * 1024) throw new Exception("Invalid helper packet size.");
        byte[] payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, deadline.Token);
        if (header[0] == (byte)'E') throw new Exception("Browser error: " + Encoding.UTF8.GetString(payload));
        if (header[0] == (byte)'S')
        {
            string message = Encoding.UTF8.GetString(payload);
            if (message == "READY|page") pageReady = true;
            if (args.Length >= 3 && message.StartsWith("STATE|"))
            {
                string[] fields = message.Split('|');
                if (fields.Length >= 6 && fields[2] == "1" && fields[4] == args[2] && fields[5] == "1") playingState = message;
            }
        }
        if (header[0] == (byte)'F' && pageReady)
        {
            if (length < 4 || payload[0] != 0xff || payload[1] != 0xd8 || payload[^2] != 0xff || payload[^1] != 0xd9)
                throw new Exception("Invalid JPEG frame.");
            frame = payload;
        }
    }
    await File.WriteAllBytesAsync(Path.Combine(reportDirectory, "browser-frame.jpg"), frame);
    var report = new { checks = "PASS", browserPath, pageReady, jpegFrameBytes = frame.Length, playingState,
        note = "Real installed helper, WebView2 startup, page navigation, IPC and frame capture. Does not test Unity or a multiplayer lobby." };
    string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(Path.Combine(reportDirectory, "browser-report.json"), json);
    Console.WriteLine(json);
}
finally
{
    if (pipe.IsConnected)
    {
        try { await pipe.WriteAsync(Encoding.UTF8.GetBytes("CLOSE\n")); } catch { }
    }
    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try { await browser.WaitForExitAsync(cleanupDeadline.Token); }
    catch (OperationCanceledException) { if (!browser.HasExited) browser.Kill(); }
}
