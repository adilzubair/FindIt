using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using FindIt.DemoRecorder;

// Recorder --app <FindIt.App.exe> --drive <demo drive> --storyboard <storyboard.json> --out <takes folder>
//          [--take <name>] [--input uia|real] [--ffmpeg <ffmpeg.exe>]
//
// Plays each storyboard take against the real app and records only its window, at 60 fps and
// without the cursor. Every action is logged with its time and on-screen rectangle so the edit
// can draw the cursor and aim its zooms. "uia" input never touches the mouse or keyboard;
// "real" input moves the mouse and types, which shows hover states and the caret but needs
// the machine to be left alone while it runs.
string? Option(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
if (Option("--app") is not { } app || Option("--drive") is not { } drive || Option("--storyboard") is not { } storyboardPath || Option("--out") is not { } outFolder)
{
    Console.Error.WriteLine("Usage: Recorder --app <FindIt.App.exe> --drive <demo drive> --storyboard <storyboard.json> --out <folder> [--take <name>] [--input uia|real] [--ffmpeg <ffmpeg.exe>]");
    return 2;
}
var realInput = (Option("--input") ?? "uia") == "real";
var ffmpeg = Option("--ffmpeg") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "runtime", "tools", "ffmpeg.exe"); // bin/<config>/<tfm> → repo root
if (!File.Exists(ffmpeg)) { Console.Error.WriteLine($"ffmpeg not found at {ffmpeg}; pass --ffmpeg."); return 2; }
drive = Path.GetFullPath(drive);
if (!File.Exists(Path.Combine(drive, ".findit-demo-drive"))) { Console.Error.WriteLine($"{drive} is not a generated demo drive."); return 2; }
var storyboard = JsonNode.Parse(File.ReadAllText(storyboardPath))!;
var window = storyboard["window"]!;
var takes = storyboard["takes"]!.AsArray().Where(t => Option("--take") is not { } only || (string)t!["name"]! == only).ToList();
if (takes.Count == 0) { Console.Error.WriteLine("No takes to record."); return 2; }
Directory.CreateDirectory(outFolder);

// Each take starts from a fresh launch, so every one opens on the same, untouched home page.
foreach (var take in takes)
{
    var name = (string)take!["name"]!;
    Console.WriteLine($"Take {name}");
    using var appProcess = Process.Start(new ProcessStartInfo(app, ["--root", drive]) { UseShellExecute = false })!;
    try
    {
        var hwnd = Win32.WaitForMainWindow(appProcess, TimeSpan.FromSeconds(60));
        var dpi = Win32.GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        Win32.Place(hwnd, (int)window["x"]!, (int)window["y"]!, (int)Math.Round((int)window["width"]! * scale), (int)Math.Round((int)window["height"]! * scale));
        var ui = new AppUi(AutomationElement.FromHandle(hwnd), realInput);
        ui.WaitUntil(() => ui.Text("ConnectionLabel")?.Contains("Ready", StringComparison.OrdinalIgnoreCase) == true, TimeSpan.FromSeconds(90), "the drive to report Ready");
        ui.WaitUntil(() => ui.Visible("HeroQueryBox"), TimeSpan.FromSeconds(10), "the home page");
        if (realInput) Win32.SetForegroundWindow(hwnd);
        Thread.Sleep(1500); // let thumbnails finish loading before the first frame
        var video = Path.Combine(outFolder, name + ".mp4");
        using var capture = Capture.Start(ffmpeg, hwnd, video);
        var log = new TakeLog(name, capture, hwnd, dpi);
        ui.Log = log;
        Thread.Sleep(600);
        foreach (var step in take["steps"]!.AsArray()) ui.Run(step!);
        log.Mark("end");
        capture.Stop(() => Win32.Nudge(hwnd));
        log.Save(Path.Combine(outFolder, name + ".json"));
        Console.WriteLine($"  {video} at {dpi} dpi ({log.Events.Count} events)");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
    finally
    {
        if (!appProcess.HasExited) { appProcess.CloseMainWindow(); if (!appProcess.WaitForExit(10000)) appProcess.Kill(true); }
        appProcess.WaitForExit();
    }
}
return 0;
