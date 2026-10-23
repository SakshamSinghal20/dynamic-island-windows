using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using Island.Core;
using Island.Services;

var results = new List<object>();
int failures = 0;
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Near(double actual, double expected, double tolerance = 1e-6) => Check(Math.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}");
void Test(string name, Action test)
{
    try { test(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; results.Add(new { name, passed = false, error = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
Test("Retarget preserves position and momentum", () =>
{
    var spring = new Spring(44); spring.Retarget(286); spring.Advance(.12); double x = spring.Value, v = spring.Velocity;
    spring.Retarget(44); Near(spring.Value, x, 0); Near(spring.Velocity, v, 0); Check(v > 0); spring.Advance(.001); Check(spring.Value > x); spring.Advance(2); Near(spring.Value, 44, .001);
});
foreach (double damping in new[] { .65, .84, 1, 1.04, 1.4 })
    Test($"Frame subdivision invariance, damping {damping}", () =>
    {
        var a = new Spring(0, damping: damping); var b = new Spring(0, damping: damping); a.Retarget(396); b.Retarget(396);
        a.Advance(.35); for (int i = 0; i < 42; i++) b.Advance(1.0 / 120); Near(a.Value, b.Value); Near(a.Velocity, b.Velocity);
    });
Test("60 / 120 / 144 Hz motion parity", () =>
{
    var values = new List<double>();
    foreach (int fps in new[] { 60, 120, 144 }) { var spring = new Spring(0, 14, 1.04); spring.Retarget(208); for (int i = 0; i < fps; i++) spring.Advance(1.0 / fps); values.Add(spring.Value); }
    Near(values[0], values[1]); Near(values[0], values[2]);
});
Test("Rapid reversal with dropped frames remains stable", () =>
{
    var motion = new IslandMotion();
    for (int i = 0; i < 600; i++) { if (i % 7 == 0) motion.SetExpanded(i % 14 == 0); motion.Advance(i % 29 == 0 ? .08 : 1.0 / 60); Check(double.IsFinite(motion.Width.Value) && double.IsFinite(motion.Height.Value)); Check(motion.Height.Value > 0); }
    motion.SetExpanded(false); motion.Advance(3); Check(motion.IsSettled); Near(motion.Height.Value, 32, .025);
});
Test("Drag release retains inertia", () => { var spring = new Spring(45, 17, .76) { Velocity = 120 }; spring.Retarget(0); spring.Advance(.001); Check(spring.Value > 45); spring.Advance(2); Check(spring.IsSettled); });
Test("Hidden shape settles without rebound", () =>
{
    var motion = new IslandMotion(); motion.SetExpanded(true); motion.Advance(.1); double v = motion.Height.Velocity; motion.Hide(); Near(motion.Height.Velocity, v);
    for (int i = 0; i < 180; i++) { motion.Advance(1.0 / 60); Check(motion.Height.Value >= 0); } Check(motion.IsSettled);
});
Test("Faster expansion reaches useful size without overshoot", () =>
{
    var motion = new IslandMotion(); motion.SetExpanded(true); motion.Advance(.15); Check(motion.Height.Value > 208 * .7 && motion.Height.Value < 208 * .85);
    double previous = motion.Height.Value; for (int i = 0; i < 120; i++) { motion.Advance(1.0 / 60); Check(motion.Height.Value >= previous && motion.Height.Value <= 208); previous = motion.Height.Value; } Check(motion.IsSettled);
});
Test("Fast hover response ignores a brief exit and reentry", () =>
{
    var hover = new HoverGate();
    Check(hover.Sample(true, false, false, 0) == null);
    Check(hover.Sample(true, false, false, .03) == null);
    Check(hover.Sample(true, false, false, .041) == true);
    Check(hover.Sample(false, true, false, .1) == null);
    Check(hover.Sample(false, true, false, .2) == null);
    Check(hover.Sample(true, true, false, .21) == null);
    Check(hover.Sample(false, true, false, .3) == null);
    Check(hover.Sample(false, true, false, .47) == null);
    Check(hover.Sample(false, true, false, .481) == false);
});
Test("Tab reversal retains velocity", () => { var tab = new Spring(0, 16, 1); tab.Retarget(3); tab.Advance(.08); double v = tab.Velocity; tab.Retarget(1); Near(tab.Velocity, v); tab.Advance(2); Check(tab.IsSettled); });
Test("Waveform is continuous and bounded", () => { for (int bar = 0; bar < 7; bar++) for (int i = 0; i < 600; i++) { double a = Waveform.Height(bar, i / 60.0), b = Waveform.Height(bar, (i + 1) / 60.0); Check(a >= 0 && a <= 1 && Math.Abs(a - b) < .1); } });
Test("Invalid frame durations do not corrupt state", () => { var spring = new Spring(5); spring.Retarget(20); foreach (double dt in new[] { double.NaN, double.PositiveInfinity, 0, -1 }) spring.Advance(dt); Near(spring.Value, 5, 0); });
Test("Exact bezel edge on negative-origin and scaled monitors", () =>
{
    foreach (var rect in new[] { (0d, 0d, 1440d), (-1920d, -1080d, 1920d), (1440d, 200d, 1728d) })
        foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
        { double center = rect.Item1 + rect.Item3 / 2; Check(HoverGate.BezelContains(center, rect.Item2, rect.Item1, rect.Item2, rect.Item3, scale)); Check(!HoverGate.BezelContains(center, rect.Item2 - .5, rect.Item1, rect.Item2, rect.Item3, scale)); Check(!HoverGate.BezelContains(center + 121 * scale, rect.Item2, rect.Item1, rect.Item2, rect.Item3, scale)); }
});
Test("Hover dwell, dismissal suppression and reentry", () =>
{
    var hover = new HoverGate(); Check(hover.Sample(true, false, false, 0) == null); Check(hover.Sample(true, false, false, .11) == true);
    hover.Suppress(); Check(hover.Sample(true, false, false, .5) == null); hover.Sample(false, false, false, .6); hover.Sample(true, false, false, .7); Check(hover.Sample(true, false, false, .81) == true);
    hover.Sample(false, true, false, 1); Check(hover.Sample(false, true, true, 2) == null); Check(hover.Sample(false, true, false, 2) == false);
});
var now = DateTimeOffset.FromUnixTimeSeconds(1000);
Test("Timer persistence reconciles sleep", () => { var timer = new TimerSession(60, now); var restored = JsonSerializer.Deserialize<TimerSession>(JsonSerializer.Serialize(timer))!; Near(restored.Remaining(now.AddSeconds(30)), 30); Check(restored.IsComplete(now.AddSeconds(120))); });
Test("Pause, extension and resume", () => { var timer = new TimerSession(60, now); timer.TogglePause(now.AddSeconds(20)); Near(timer.Remaining(now.AddSeconds(500)), 40); timer.Extend(60, now.AddSeconds(500)); timer.TogglePause(now.AddSeconds(500)); Near(timer.Remaining(now.AddSeconds(530)), 70); });
Test("Extending expired timer starts from current time", () => { var timer = new TimerSession(60, now); timer.Extend(60, now.AddSeconds(120)); Near(timer.Remaining(now.AddSeconds(120)), 60); });
Test("Timer label rounds up and handles hours", () => { Check(TimerSession.Label(.2) == "00:01"); Check(TimerSession.Label(3601) == "1:00:01"); Check(TimerSession.Label(-5) == "00:00"); });
Test("SSE split UTF-8 and CRLF framing", () =>
{
    byte[] bytes = Encoding.UTF8.GetBytes("event: delta\r\ndata: 你好 🌍\r\ndata: next\r\n\r\n");
    for (int split = 1; split < bytes.Length; split++) { var parser = new ServerEventParser(); var events = parser.Append(bytes.AsSpan(0, split)); events.AddRange(parser.Append(bytes.AsSpan(split))); Check(events.Count == 1); Check(events[0].Name == "delta" && events[0].Data == "你好 🌍\nnext"); }
});
Test("SSE comments and multiple events", () => { var parser = new ServerEventParser(); var events = parser.Append(Encoding.UTF8.GetBytes(":ping\n\ndata: one\n\ndata: two\n\n")); Check(events.Count == 2 && events[1].Data == "two"); });
Test("SSE oversized event rejected", () => { var parser = new ServerEventParser(); bool rejected = false; try { parser.Append(new byte[2_000_001]); } catch (InvalidDataException) { rejected = true; } Check(rejected); });
Test("Partial assistant responses excluded from follow-up context", () => { var chat = new Conversation(); var reply = chat.Begin("Hi"); reply.Text = "partial"; reply.State = "stopped"; Check(chat.Input.Length == 1); Check(chat.RemoveLastExchange() == "Hi"); Check(chat.Messages.Count == 0); });
Test("Completed multi-turn context retained", () => { var chat = new Conversation(); var reply = chat.Begin("Hi"); reply.Text = "Hello"; reply.State = "complete"; chat.Begin("Next"); Check(chat.Input.Length == 3); });
Test("OAuth state and duplicate parameter rejection", () => { Check(OAuthLoopback.ParseCallback("?state=abc&code=x", "abc")?["code"] == "x"); Check(OAuthLoopback.ParseCallback("?state=abc&code=x", "wrong") == null); Check(OAuthLoopback.ParseCallback("?state=abc&state=abc&code=x", "abc") == null); Check(OAuthLoopback.ParseCallback("", "") == null); });
Test("PKCE RFC 7636 vector", () => Check(OAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
Test("Identity discovery rejects foreign and insecure hosts", () => { Check(OAuth.TrustedOpenAi("https://auth.openai.com/keys")); Check(!OAuth.TrustedOpenAi("https://auth.openai.com.evil.invalid/keys")); Check(!OAuth.TrustedOpenAi("http://auth.openai.com/keys")); Check(!OAuth.TrustedOpenAi("https://auth.openai.com:444/keys")); });
try
{
    using var server = new OAuthLoopback("/test", "expected"); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var waiting = server.Wait(cancellation.Token); using var http = new HttpClient();
    var wrong = await http.GetAsync(server.Redirect + "?state=wrong&code=no"); Check((int)wrong.StatusCode == 400);
    var good = await http.GetAsync(server.Redirect + "?state=expected&code=accepted"); Check(good.IsSuccessStatusCode); Check((await waiting)["code"] == "accepted");
    results.Add(new { name = "Loopback listener rejects forged request then accepts valid request", passed = true }); Console.WriteLine("PASS Loopback listener");
}
catch (Exception e) { failures++; results.Add(new { name = "Loopback listener", passed = false, error = e.Message }); }
try
{
    using var server = SpotifyService.CreateCallback("expected");
    Check(server.Redirect == SpotifyService.RedirectUri);
    bool collisionRejected = false;
    try { using var duplicate = SpotifyService.CreateCallback("other"); }
    catch (InvalidOperationException e) { collisionRejected = e.Message.Contains("8888"); }
    Check(collisionRejected, "An occupied registered port must not silently choose another port.");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var waiting = server.Wait(cancellation.Token); using var http = new HttpClient();
    Check((int)(await http.GetAsync(server.Redirect + "?state=wrong&code=no")).StatusCode == 400);
    Check((await http.GetAsync(server.Redirect + "?state=expected&code=accepted")).IsSuccessStatusCode);
    Check((await waiting)["code"] == "accepted");
    results.Add(new { name = "Spotify registered callback port, exclusive binding and state validation", passed = true }); Console.WriteLine("PASS Spotify registered callback");
}
catch (Exception e) { failures++; results.Add(new { name = "Spotify registered callback", passed = false, error = e.Message }); Console.WriteLine("FAIL Spotify registered callback: " + e.Message); }
SpotifyChecks.Run(Test);
Test("Media disconnect clears stale track duration and progress", () =>
{
    using var media = new MediaService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
    media.Fixture(true); Check(media.Duration > 0); media.Disconnect();
    Check(media.Duration == 0 && media.Position == 0 && !media.CanSeek && !media.CanShuffle && !media.CanRepeat);
});
ClipboardChecks.Run(Test);
ScratchpadChecks.Run(Test);
string output = args.Length > 0 ? args[0] : "test-results.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { total = results.Count, failures, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed"); return failures == 0 ? 0 : 1;
