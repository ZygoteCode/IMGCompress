using System.Windows;
using System.Windows.Threading;
using Carbonado.UI.Cinematics;
using Carbonado.UI.Tests.TestSupport;

namespace Carbonado.UI.Tests.Cinematics;

public class TimelineTests
{
    [Fact]
    public void Track_InterpolatesAndClamps()
    {
        var track = Track.Double().Key(1, 10).Key(3, 30);

        Assert.Equal(10, track.Evaluate(-5));
        Assert.Equal(10, track.Evaluate(1));
        Assert.Equal(15, track.Evaluate(1.5), 12);
        Assert.Equal(20, track.Evaluate(2), 12);
        Assert.Equal(30, track.Evaluate(3));
        Assert.Equal(30, track.Evaluate(99));
        Assert.Equal(1, track.Start);
        Assert.Equal(3, track.End);
    }

    [Fact]
    public void Track_EaseShapesTheSegmentEndingAtItsKey()
    {
        var track = Track.Double().Key(0, 0).Key(1, 100, Easings.ExpoOut).Key(2, 0);

        Assert.Equal(100 * Easings.ExpoOut(0.25), track.Evaluate(0.25), 12);
        Assert.Equal(50, track.Evaluate(1.5), 12);
    }

    [Fact]
    public void Track_SortsKeysAndEqualTimesMakeSteps()
    {
        var track = Track.Double().Key(2, 20).Key(0, 0).Key(1, 5).Key(1, 10);

        Assert.Equal(0, track.Evaluate(0));
        Assert.Equal(2.5, track.Evaluate(0.5), 12);
        Assert.Equal(10, track.Evaluate(1));
        Assert.Equal(15, track.Evaluate(1.5), 12);
        Assert.Equal(new[] { 0.0, 1, 1, 2 }, track.Keys.Select(k => k.Time));
    }

    [Fact]
    public void Track_UsesTheSuppliedLerp()
    {
        var track = new Track<Point>((a, b, t) => new Point(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t)))
            .Key(0, new Point(0, 0))
            .Key(4, new Point(8, -4));

        Assert.Equal(new Point(4, -2), track.Evaluate(2));
        Assert.Throws<InvalidOperationException>(() => new Track<Point>((a, _, _) => a).Evaluate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Track.Double().Key(double.NaN, 1));
    }

    [Fact]
    public void Timeline_EvaluateIsDeterministic()
    {
        var state = new double[3];
        var timeline = new Timeline()
            .Bind(Track.Double().Key(0, 0).Key(1, 1, Easings.Crystallize).Key(2, -1, Easings.Settle), v => state[0] = v)
            .Bind(Track.Double().Key(0.5, 10).Key(1.5, 20, Easings.ElasticOut), v => state[1] = v)
            .Bind(Track.Double().Key(0, 3).Key(2, 7, Easings.Implode), v => state[2] = v);

        var reference = new Dictionary<double, double[]>();
        double[] times = [0, 0.1, 0.37, 0.5, 0.99, 1, 1.25, 1.5, 1.75, 2, 2.5];
        foreach (double t in times)
        {
            timeline.Evaluate(t);
            reference[t] = (double[])state.Clone();
        }

        var random = new Random(1234);
        for (int i = 0; i < 500; i++)
        {
            double t = times[random.Next(times.Length)];
            timeline.Evaluate(random.NextDouble() * 3);
            timeline.Evaluate(t);
            Assert.Equal(reference[t], state);
        }

        Assert.Equal(2, timeline.Duration);
    }

    [Fact]
    public void Timeline_AtActionsFireOnce()
    {
        var fired = new List<string>();
        var timeline = new Timeline()
            .At(1.0, () => fired.Add("b"))
            .At(0.5, () => fired.Add("a"))
            .At(1.0, () => fired.Add("c"));

        timeline.Evaluate(0.25);
        Assert.Empty(fired);
        timeline.Evaluate(0.5);
        Assert.Equal(new[] { "a" }, fired);
        timeline.Evaluate(0.5);
        timeline.Evaluate(0.75);
        Assert.Equal(new[] { "a" }, fired);
        timeline.Evaluate(5);
        Assert.Equal(new[] { "a", "b", "c" }, fired);
        timeline.Evaluate(6);
        Assert.Equal(new[] { "a", "b", "c" }, fired);

        // Moving backwards fires nothing but re-arms the cues after the new position.
        timeline.Evaluate(0.75);
        Assert.Equal(3, fired.Count);
        timeline.Evaluate(1);
        Assert.Equal(new[] { "a", "b", "c", "b", "c" }, fired);

        timeline.Reset();
        timeline.Evaluate(2);
        Assert.Equal(new[] { "a", "b", "c", "b", "c", "a", "b", "c" }, fired);
    }

    [Fact]
    public void Timeline_DurationCoversTracksAndCues_UnlessSetExplicitly()
    {
        var timeline = new Timeline().Bind(Track.Double().Key(0, 0).Key(1.5, 1), _ => { }).At(2.25, () => { });
        Assert.Equal(2.25, timeline.Duration);

        timeline.Duration = 3.4;
        Assert.Equal(3.4, timeline.Duration);
        Assert.Equal(0, new Timeline().Duration);
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Duration = -1);
    }

    [Fact]
    public void Clock_SkipJumpsToEndAndCompletes() => Sta.Run(() =>
    {
        double applied = -1;
        bool cue = false;
        var timeline = new Timeline()
            .Bind(Track.Double().Key(0, 0).Key(2, 1), v => applied = v)
            .At(1.5, () => cue = true);
        var clock = new CinematicClock(timeline);
        int completed = 0;
        clock.Completed += (_, _) => completed++;

        clock.Play();
        Assert.True(clock.IsRunning);
        Assert.Equal(0, applied);

        clock.Skip();
        Assert.False(clock.IsRunning);
        Assert.Equal(2, clock.Time);
        Assert.Equal(1, applied);
        Assert.True(cue);
        Assert.Equal(1, completed);

        clock.Skip();
        Assert.Equal(1, completed);
    });

    [Fact]
    public void Clock_SeekEvaluatesWithoutCompleting() => Sta.Run(() =>
    {
        double applied = -1;
        var clock = new CinematicClock(new Timeline().Bind(Track.Double().Key(0, 0).Key(2, 1), v => applied = v));
        int completed = 0;
        clock.Completed += (_, _) => completed++;

        clock.Seek(1);
        Assert.Equal(1, clock.Time);
        Assert.Equal(0.5, applied, 12);
        clock.Seek(10);
        Assert.Equal(2, clock.Time);
        clock.Seek(-3);
        Assert.Equal(0, clock.Time);
        Assert.Equal(0, completed);
        Assert.False(clock.IsRunning);
    });

    [Fact]
    public void Clock_PlayAdvancesOnRenderingAndCompletes() => Sta.RunLoop(async () =>
    {
        var samples = new List<double>();
        var clock = new CinematicClock(new Timeline().Bind(Track.Double().Key(0, 0).Key(1, 1), samples.Add)) { Speed = 8 };
        var done = new TaskCompletionSource();
        clock.Completed += (_, _) => done.TrySetResult();

        clock.Play();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(clock.IsRunning);
        Assert.Equal(1, clock.Time);
        Assert.True(samples.Count >= 3, $"only {samples.Count} frames");
        Assert.Equal(1, samples[^1]);
        for (int i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i] >= samples[i - 1], "time went backwards");
        }
    });

    [Fact]
    public void Clock_FaultInPlay_EndsTheRunAndAttachesNothing() => Sta.RunLoop(async () =>
    {
        Func<Exception?> unhandled = CaptureUnhandled();
        int evaluations = 0;
        var clock = new CinematicClock(new Timeline().Bind(Track.Double().Key(0, 0).Key(1, 1), _ =>
        {
            evaluations++;
            throw new InvalidOperationException("render fault");
        }));
        int completed = 0;
        clock.Completed += (_, _) => completed++;

        Assert.Throws<InvalidOperationException>(clock.Play);
        Assert.False(clock.IsRunning);
        Assert.Equal(1, completed);

        // Several frames later nothing has evaluated (and thrown) again: no frame handler was left attached.
        await Task.Delay(200);
        Assert.Equal(1, evaluations);
        Assert.Null(unhandled());
    });

    [Fact]
    public void Clock_FaultInSkip_StillCompletes() => Sta.Run(() =>
    {
        var clock = new CinematicClock(new Timeline().Bind(Track.Double().Key(0, 0).Key(1, 1), v =>
        {
            if (v >= 1)
            {
                throw new InvalidOperationException("final frame fault");
            }
        }));
        int completed = 0;
        clock.Completed += (_, _) => completed++;
        clock.Play();
        Assert.True(clock.IsRunning);

        Assert.Throws<InvalidOperationException>(clock.Skip);
        Assert.False(clock.IsRunning);
        Assert.Equal(1, completed);
        clock.Skip();
        Assert.Equal(1, completed);
    });

    [Fact]
    public void Clock_FaultInAFrame_FinishesTheClockAndReachesTheDispatcher() => Sta.RunLoop(async () =>
    {
        Func<Exception?> unhandled = CaptureUnhandled();
        int evaluations = 0;
        var clock = new CinematicClock(new Timeline().Bind(Track.Double().Key(0, 0).Key(1, 1), v =>
        {
            evaluations++;
            if (v > 0.2)
            {
                throw new InvalidOperationException("frame fault");
            }
        })) { Speed = 4 };
        var done = new TaskCompletionSource();
        clock.Completed += (_, _) => done.TrySetResult();

        clock.Play();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(clock.IsRunning);
        Assert.True(clock.Time < 1, "the run should end at the faulted frame, not at the end");

        int afterFault = evaluations;
        await Task.Delay(200);
        Assert.Equal(afterFault, evaluations);
        Assert.IsType<InvalidOperationException>(unhandled());
    });

    [Fact]
    public void Clock_CompletesOnceWhenACueSkipsDuringEvaluation() => Sta.Run(() =>
    {
        CinematicClock? clock = null;
        var timeline = new Timeline().Bind(Track.Double().Key(0, 0).Key(1, 1), _ => { }).At(0, () => clock!.Skip());
        clock = new CinematicClock(timeline);
        int completed = 0;
        clock.Completed += (_, _) => completed++;

        clock.Play();

        Assert.Equal(1, completed);
        Assert.False(clock.IsRunning);
        Assert.Equal(1, clock.Time);
    });

    /// <summary>Handles exceptions escaping to the current dispatcher (e.g. from a frame handler); returns the first one.</summary>
    private static Func<Exception?> CaptureUnhandled()
    {
        Exception? captured = null;
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            captured ??= e.Exception;
            e.Handled = true;
        };
        return () => captured;
    }
}
