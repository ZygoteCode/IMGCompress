using System.Media;
using Carbonado.Core.Logging;

namespace Carbonado.App.Services;

/// <summary>
/// Plays Carbonado's completion chime: a short crystalline "ting" synthesized at runtime (no audio asset):
/// two bell-like partials (E6 + B6) with an octave shimmer and an exponential decay.
/// </summary>
public sealed class SoundService(Func<bool> enabled) : ISoundService, IDisposable
{
    private readonly Lazy<byte[]> _wav = new(() => SynthesizeChime());
    private SoundPlayer? _player;

    public void PlayCompletion()
    {
        if (!enabled())
        {
            return;
        }

        try
        {
            _player ??= new SoundPlayer(new MemoryStream(_wav.Value));
            _player.Play();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException)
        {
            Log.Warn($"Chime failed: {e.Message}");
        }
    }

    public void Dispose() => _player?.Dispose();

    /// <summary>16-bit mono PCM WAV, 44.1 kHz, 0.7 s.</summary>
    internal static byte[] SynthesizeChime()
    {
        const int rate = 44100;
        const double seconds = 0.7;
        int samples = (int)(rate * seconds);
        var pcm = new short[samples];
        (double Freq, double Amp, double Decay)[] partials =
        [
            (1318.51, 0.42, 5.5),  // E6
            (1975.53, 0.30, 7.0),  // B6
            (2637.02, 0.16, 9.0),  // E7 shimmer
            (3951.07, 0.07, 12.0), // B7 sparkle
        ];
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)rate;
            double attack = Math.Min(1.0, t / 0.004);
            double v = 0;
            foreach (var (freq, amp, decay) in partials)
            {
                v += amp * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * freq * t);
            }

            pcm[i] = (short)Math.Clamp(v * attack * 0.55 * short.MaxValue, short.MinValue, short.MaxValue);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (short s in pcm)
        {
            w.Write(s);
        }

        w.Flush();
        return ms.ToArray();
    }
}
