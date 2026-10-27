using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;

namespace FindIt.DemoRecorder;

/// <summary>Drives FindIt through UI Automation, or with real mouse and keyboard input.</summary>
internal sealed class AppUi(AutomationElement root, bool realInput)
{
    private readonly Random typing = new(1);
    public TakeLog? Log { get; set; }

    public void Run(JsonNode step)
    {
        string Arg(string key) => (string?)step[key] ?? throw new ArgumentException($"Step {step.ToJsonString()} needs \"{key}\".");
        switch (Arg("do"))
        {
            case "hold": Thread.Sleep((int)step["ms"]!); break;
            case "type": TypeInto(Find(Arg("into")), Arg("text")); break;
            case "submit": Submit(); break;
            case "results": WaitForResults((int?)step["timeoutMs"] ?? 15000); break;
            case "open": Press(Tile(Arg("name")), "open:" + Arg("name")); break;
            case "moment": Press(Moments().ElementAt((int?)step["index"] ?? 0), "moment"); break;
            case "click": Press(Find(Arg("target")), Arg("target")); break;
            case "scroll": Scroll(Find(Arg("target")), (double?)step["percent"] ?? 100); break;
            case "mark":
                // Layout-only elements such as Border have no automation peer; mark what is exposed.
                var found = new List<(string, Rect)>();
                foreach (var target in step["elements"]?.AsArray().Select(e => (string)e!) ?? [])
                    if (TryFind(target) is { Current.IsOffscreen: false } element) found.Add((target, element.Current.BoundingRectangle));
                    else Console.WriteLine($"  mark {Arg("label")}: {target} is not visible");
                Log?.Mark(Arg("label"), found);
                break;
            default: throw new ArgumentException($"Unknown step {step.ToJsonString()}");
        }
    }

    public bool Visible(string idOrName) => TryFind(idOrName) is { Current.IsOffscreen: false };

    public string? Text(string idOrName) => TryFind(idOrName)?.Current.Name;

    public void WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            try { if (condition()) return; } catch (ElementNotAvailableException) { }
            Thread.Sleep(50);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private void TypeInto(AutomationElement box, string text)
    {
        Log?.Add("focus", box.Current.AutomationId, box.Current.BoundingRectangle);
        if (realInput) { Glide(box); Win32.Click(); Thread.Sleep(150); }
        var value = (ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern);
        for (var i = 1; i <= text.Length; i++)
        {
            if (realInput) Win32.Type(text[i - 1]); else value.SetValue(text[..i]);
            Log?.Add("type", box.Current.AutomationId, null, text[..i]);
            // A practised typist: quick within words, a beat longer at spaces.
            Thread.Sleep(55 + typing.Next(0, 45) + (text[i - 1] == ' ' ? 45 : 0));
        }
    }

    private void Submit()
    {
        if (realInput) { Log?.Add("key", "Enter"); Win32.Press(0x0D); return; }
        Press(Find("HeroSearchButton"), "HeroSearchButton");
    }

    private void WaitForResults(int timeoutMs)
    {
        WaitUntil(() => Tiles().Any() && TryFind("SearchProgress") is not { Current.IsOffscreen: false }, TimeSpan.FromMilliseconds(timeoutMs), "search results");
        Log?.Mark("results", Tiles().Take(12).Select((t, i) => ($"tile{i}:{t.Current.Name}", t.Current.BoundingRectangle)).ToArray());
    }

    private IEnumerable<AutomationElement> Tiles() => Find("Gallery")
        .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
        .Where(e => e.Current.Name.EndsWith("Open preview.", StringComparison.Ordinal) && !e.Current.IsOffscreen);

    private AutomationElement Tile(string name)
    {
        AutomationElement? found = null;
        WaitUntil(() => (found = Tiles().FirstOrDefault(t => t.Current.Name.Contains(name, StringComparison.OrdinalIgnoreCase))) is not null, TimeSpan.FromSeconds(10), $"a result named {name}");
        return found!;
    }

    private IEnumerable<AutomationElement> Moments()
    {
        AutomationElement[] items = [];
        WaitUntil(() => (items = Find("MomentsList").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().ToArray()).Length > 0,
            TimeSpan.FromSeconds(10), "matching moments");
        return items;
    }

    private void Press(AutomationElement element, string target)
    {
        var rect = element.Current.BoundingRectangle;
        if (realInput) { Glide(element); Thread.Sleep(90); }
        Log?.Add("click", target, rect, element.Current.Name);
        if (realInput) Win32.Click(); else Activate(element);
    }

    /// <summary>Scrolls the nearest scrollable ancestor of an element, as a reader would to reach it.</summary>
    private void Scroll(AutomationElement element, double percent)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var node = element; node is not null; node = walker.GetParent(node))
        {
            if (!node.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) || !((ScrollPattern)pattern).Current.VerticallyScrollable) continue;
            Log?.Add("scroll", element.Current.AutomationId, node.Current.BoundingRectangle, percent.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (realInput)
            {
                var r = node.Current.BoundingRectangle;
                Win32.Glide((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2), 400);
                var scroll = (ScrollPattern)pattern;
                for (var i = 0; i < 40 && scroll.Current.VerticalScrollPercent < percent - 0.5; i++) { Win32.Wheel(-120); Thread.Sleep(45); }
            }
            else ((ScrollPattern)pattern).SetScrollPercent(ScrollPattern.NoScroll, percent);
            return;
        }
        throw new InvalidOperationException($"{element.Current.Name} is not inside anything scrollable.");
    }

    private static void Activate(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) ((TogglePattern)toggle).Toggle();
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) ((SelectionItemPattern)select).Select();
        else throw new InvalidOperationException($"{element.Current.Name} cannot be activated.");
    }

    private void Glide(AutomationElement element)
    {
        var r = element.Current.BoundingRectangle;
        var (x, y) = ((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        var (cx, cy) = Win32.Cursor();
        var distance = Math.Sqrt(Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2));
        Win32.Glide(x, y, (int)Math.Clamp(250 + distance * 0.35, 300, 750));
    }

    private AutomationElement Find(string idOrName) => TryFind(idOrName) ?? throw new InvalidOperationException($"No element {idOrName}.");

    private AutomationElement? TryFind(string idOrName) => root.FindFirst(TreeScope.Descendants, new OrCondition(
        new PropertyCondition(AutomationElement.AutomationIdProperty, idOrName),
        new PropertyCondition(AutomationElement.NameProperty, idOrName)));
}
