using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OperatorVR;

/// <summary>
/// How much main-thread time the mod's own scripts take. With Virtual Desktop's runtime the main thread (~12–14 ms per
/// frame, game and mod together) and the GPU became the limit for 72 fps. Each Update/LateUpdate/onBeforeRender and
/// the busiest game hooks measure themselves (<c>using var _ = ModTime.Measure("...")</c>); VRDiagnostics' 10-second
/// [Perf] report adds the total and the costliest parts in ms per frame, then starts again.
/// </summary>
internal static class ModTime
{
    private static readonly Dictionary<string, long> s_ticks = new();

    internal readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _start;

        internal Scope(string name)
        {
            _name = name;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var t = Stopwatch.GetTimestamp() - _start;
            s_ticks[_name] = s_ticks.TryGetValue(_name, out var sum) ? sum + t : t;
        }
    }

    internal static Scope Measure(string name) => new(name);

    /// <summary>"total x ms/frame: A y, B z, ..." over <paramref name="frames"/> frames, then resets.</summary>
    internal static string Report(int frames)
    {
        if (frames <= 0 || s_ticks.Count == 0)
            return "nothing measured";
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency / frames;
        var total = Ms(s_ticks.Values.Sum());
        // Made into a list before clearing: the first report's list came out empty (the query ran after the Clear)
        var top = s_ticks.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {Ms(kv.Value):0.00}").ToList();
        s_ticks.Clear();
        return $"total {total:0.00} ms/frame: {string.Join(", ", top)}";
    }
}
