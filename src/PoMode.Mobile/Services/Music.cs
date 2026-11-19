using System.Globalization;
using PoMode.Shared.Analysis;

namespace PoMode.Mobile.Services;

/// <summary>One pitch reading off the microphone: when, which (fractional) MIDI note, and how sure.</summary>
public readonly record struct PitchPoint(double Sec, double Midi, double Clarity);

/// <summary>
/// Item 1: YIN pitch detection on the phone (Cheveigné &amp; Kawahara 2002), 16 kHz, 64 ms window, 32 ms hop: the line the
/// Practice plot draws as you sing, with nothing sent anywhere until the take is analysed.
/// </summary>
public sealed class PitchTracker
{
    private const int Window = 1024;
    private const int Hop = 512;
    private const double Threshold = 0.15;
    private const double MinHz = 70;
    private const double MaxHz = 1100;
    private readonly float[] _ring = new float[Window];
    private readonly float[] _diff = new float[Window / 2];
    private int _filled;
    private int _sinceHop;
    private long _samples;

    /// <summary>Feeds 16-bit PCM; returns a reading per hop (NaN MIDI when unvoiced or too quiet).</summary>
    public IEnumerable<PitchPoint> Push(byte[] pcm)
    {
        List<PitchPoint> points = [];
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            Array.Copy(_ring, 1, _ring, 0, Window - 1);
            _ring[Window - 1] = BitConverter.ToInt16(pcm, i) / 32768f;
            _samples++;
            _filled = Math.Min(Window, _filled + 1);
            if (++_sinceHop >= Hop && _filled == Window)
            {
                _sinceHop = 0;
                (double midi, double clarity) = Detect();
                points.Add(new PitchPoint(_samples / (double)Wav.Rate, midi, clarity));
            }
        }

        return points;
    }

    public void Reset()
    {
        Array.Clear(_ring);
        _filled = 0;
        _sinceHop = 0;
        _samples = 0;
    }

    private (double Midi, double Clarity) Detect()
    {
        double energy = 0;
        foreach (float s in _ring)
        {
            energy += s * s;
        }

        if (Math.Sqrt(energy / Window) < 0.01)
        {
            return (double.NaN, 0);
        }

        int minTau = (int)(Wav.Rate / MaxHz);
        int maxTau = Math.Min(_diff.Length - 1, (int)(Wav.Rate / MinHz));
        for (int tau = 1; tau <= maxTau; tau++)
        {
            double sum = 0;
            for (int j = 0; j < Window - maxTau; j++)
            {
                double d = _ring[j] - _ring[j + tau];
                sum += d * d;
            }

            _diff[tau] = (float)sum;
        }

        // Cumulative mean normalised difference; the first dip under the threshold is the period.
        double running = 0;
        _diff[0] = 1;
        for (int tau = 1; tau <= maxTau; tau++)
        {
            running += _diff[tau];
            _diff[tau] = running == 0 ? 1 : (float)(_diff[tau] * tau / running);
        }

        for (int tau = minTau; tau < maxTau; tau++)
        {
            if (_diff[tau] < Threshold)
            {
                while (tau + 1 < maxTau && _diff[tau + 1] < _diff[tau])
                {
                    tau++;
                }

                // Parabolic interpolation for a sub-sample period.
                double a = _diff[tau - 1], b = _diff[tau], c = _diff[tau + 1];
                double shift = (a - 2 * b + c) == 0 ? 0 : 0.5 * (a - c) / (a - 2 * b + c);
                double hz = Wav.Rate / (tau + shift);
                return (69 + 12 * Math.Log2(hz / 440.0), 1 - b);
            }
        }

        return (double.NaN, 0);
    }
}

/// <summary>Vibrato off the last second of a held note: rate (Hz) and width (± cents), as the web's readout shows it.</summary>
public static class Vibrato
{
    public static string? Describe(IReadOnlyList<PitchPoint> recent)
    {
        List<PitchPoint> voiced = [.. recent.Where(p => !double.IsNaN(p.Midi))];
        if (voiced.Count < 16 || voiced[^1].Sec - voiced[0].Sec < 0.6)
        {
            return null;
        }

        // Held note: the same semitone throughout.
        double mean = voiced.Average(p => p.Midi);
        if (voiced.Any(p => Math.Abs(p.Midi - mean) > 1.0))
        {
            return null;
        }

        double[] cents = [.. voiced.Select(p => (p.Midi - mean) * 100)];
        int crossings = 0;
        for (int i = 1; i < cents.Length; i++)
        {
            if (Math.Sign(cents[i]) != Math.Sign(cents[i - 1]))
            {
                crossings++;
            }
        }

        double seconds = voiced[^1].Sec - voiced[0].Sec;
        double rate = crossings / 2.0 / seconds;
        double width = Math.Sqrt(cents.Average(c => c * c)) * Math.Sqrt(2);
        return width < 8
            ? "Held steady — no vibrato"
            : string.Create(CultureInfo.InvariantCulture, $"Vibrato {rate:0.0} Hz · ±{width:0} cents");
    }
}

/// <summary>Note names for MIDI numbers (C4 = 60).</summary>
public static class Notes
{
    public static string Name(int midi) => $"{ScaleModes.NoteName(midi)}{(midi / 12) - 1}";

    public static double Hz(double midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    public static string ModeName(int parentTonic, ScaleMode mode)
        => $"{ScaleModes.NoteName((parentTonic + ScaleModes.ModeDegreeOffset(mode)) % 12, parentTonic)} {ScaleModes.DisplayName(mode.ToString())}";
}

/// <summary>
/// Item 6 + 5: the phone's own sounds, mixed into one stream — a drone on the key's home note (root, fifth, octave), a
/// metronome click with an accented downbeat, and whatever a MIDI keyboard is playing. Thread-safe: the audio thread
/// renders while pages and lock-screen buttons change settings.
/// </summary>
public sealed class ToneMixer
{
    public const int Rate = 44_100;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Voice> _voices = [];
    private double _dronePhase1, _dronePhase2, _dronePhase3, _droneLevel;
    private long _clickSample;
    private int _beat;

    public bool DroneOn { get; private set; }

    public int DroneRoot { get; private set; } = 2; // D

    public bool ClickOn { get; private set; }

    public double Bpm { get; private set; } = 90;

    public int BeatsPerBar { get; private set; } = 4;

    public double Volume { get; set; } = 0.7;

    public bool Active => DroneOn || ClickOn;

    public event Action? Changed;

    public string Title => (DroneOn, ClickOn) switch
    {
        (true, true) => string.Create(CultureInfo.InvariantCulture, $"Drone {ScaleModes.NoteName(DroneRoot)} · {Bpm:0} BPM"),
        (true, false) => $"Drone {ScaleModes.NoteName(DroneRoot)}",
        (false, true) => string.Create(CultureInfo.InvariantCulture, $"Click {Bpm:0} BPM · {BeatsPerBar}/4"),
        _ => "Paused",
    };

    public void Set(bool? drone = null, int? root = null, bool? click = null, double? bpm = null, int? beatsPerBar = null)
    {
        lock (_gate)
        {
            DroneOn = drone ?? DroneOn;
            DroneRoot = ((root ?? DroneRoot) % 12 + 12) % 12;
            if (click is true && !ClickOn)
            {
                _clickSample = 0;
                _beat = 0;
            }

            ClickOn = click ?? ClickOn;
            Bpm = Math.Clamp(bpm ?? Bpm, 30, 260);
            BeatsPerBar = Math.Clamp(beatsPerBar ?? BeatsPerBar, 1, 12);
        }

        Changed?.Invoke();
    }

    /// <summary>Lock-screen play/pause: remembers what was on.</summary>
    private (bool Drone, bool Click) _paused = (true, false);

    public void TogglePlay()
    {
        if (Active)
        {
            _paused = (DroneOn, ClickOn);
            Set(drone: false, click: false);
        }
        else
        {
            Set(drone: _paused.Drone, click: _paused.Click || !_paused.Drone);
        }
    }

    public void NoteOn(int midi, int velocity)
    {
        lock (_gate)
        {
            _voices[midi] = new Voice(Notes.Hz(midi), velocity / 127.0);
        }
    }

    public void NoteOff(int midi)
    {
        lock (_gate)
        {
            if (_voices.TryGetValue(midi, out Voice? v))
            {
                v.Releasing = true;
            }
        }
    }

    public bool HasVoices
    {
        get
        {
            lock (_gate)
            {
                return _voices.Count > 0;
            }
        }
    }

    /// <summary>Fills <paramref name="output"/> with mono samples in -1..1.</summary>
    public void Render(Span<float> output)
    {
        lock (_gate)
        {
            double rootHz = Notes.Hz(48 + DroneRoot);
            long samplesPerBeat = (long)(Rate * 60.0 / Bpm);
            for (int i = 0; i < output.Length; i++)
            {
                double s = 0;

                // Drone: fades in and out over ~0.3 s so switching it never clicks.
                double target = DroneOn ? 1 : 0;
                _droneLevel += (target - _droneLevel) * 0.00015;
                if (_droneLevel > 0.0005)
                {
                    _dronePhase1 += rootHz / Rate;
                    _dronePhase2 += rootHz * 1.5 / Rate;
                    _dronePhase3 += rootHz * 2 / Rate;
                    s += _droneLevel * ((0.22 * Math.Sin(2 * Math.PI * _dronePhase1))
                                        + (0.12 * Math.Sin(2 * Math.PI * _dronePhase2))
                                        + (0.08 * Math.Sin(2 * Math.PI * _dronePhase3)));
                }

                if (ClickOn)
                {
                    long at = _clickSample % samplesPerBeat;
                    if (at == 0 && _clickSample > 0)
                    {
                        _beat = (_beat + 1) % BeatsPerBar;
                    }

                    if (at < Rate / 30)
                    {
                        double t = at / (double)Rate;
                        double hz = _beat == 0 ? 2000 : 1300;
                        s += 0.45 * Math.Exp(-t * 90) * Math.Sin(2 * Math.PI * hz * t);
                    }

                    _clickSample++;
                }

                foreach (Voice v in _voices.Values)
                {
                    s += v.Next();
                }

                output[i] = (float)Math.Clamp(s * Volume, -1, 1);
            }

            foreach (int done in _voices.Where(p => p.Value.Silent).Select(p => p.Key).ToList())
            {
                _voices.Remove(done);
            }
        }
    }

    private sealed class Voice(double hz, double level)
    {
        private double _phase;
        private double _env;

        public bool Releasing { get; set; }

        public bool Silent => Releasing && _env < 0.0005;

        public double Next()
        {
            _env = Releasing ? _env * 0.9992 : Math.Min(1, _env + 0.004);
            _phase += hz / Rate;
            double x = 2 * Math.PI * _phase;
            return _env * level * 0.25 * (Math.Sin(x) + (0.3 * Math.Sin(2 * x)) + (0.1 * Math.Sin(3 * x)));
        }
    }
}

/// <summary>
/// Item 5: a take played on a MIDI keyboard instead of sung. Notes are kept with their times and rendered to the same
/// 16 kHz WAV a sung take is, so the server's hum path reads them exactly like a voice (a clean tone is the easiest
/// melody its pitch tracker will ever see).
/// </summary>
public sealed class KeyboardTake
{
    private readonly List<(int Midi, double On, double Off, double Level)> _notes = [];
    private readonly Dictionary<int, (double On, double Level)> _held = [];
    private readonly System.Diagnostics.Stopwatch _clock = new();

    public double Elapsed => _clock.Elapsed.TotalSeconds;

    public int Count => _notes.Count + _held.Count;

    public void Start()
    {
        _notes.Clear();
        _held.Clear();
        _clock.Restart();
    }

    public void Note(int midi, int velocity)
    {
        double now = Elapsed;
        if (velocity > 0)
        {
            _held[midi] = (now, velocity / 127.0);
        }
        else if (_held.Remove(midi, out var on))
        {
            _notes.Add((midi, on.On, now, on.Level));
        }
    }

    public byte[] StopAndRender()
    {
        double end = Elapsed;
        _clock.Stop();
        foreach (var (midi, on) in _held)
        {
            _notes.Add((midi, on.On, end, on.Level));
        }

        _held.Clear();
        int samples = (int)(end * Wav.Rate);
        float[] mix = new float[samples];
        foreach (var (midi, on, off, level) in _notes)
        {
            double hz = Notes.Hz(midi);
            int from = (int)(on * Wav.Rate);
            int to = Math.Min(samples, (int)((off + 0.08) * Wav.Rate));
            for (int i = from; i < to; i++)
            {
                double t = (i - from) / (double)Wav.Rate;
                double env = Math.Min(1, t * 200) * (i > off * Wav.Rate ? Math.Exp(-((i / (double)Wav.Rate) - off) * 40) : 1);
                mix[i] += (float)(0.35 * level * env * Math.Sin(2 * Math.PI * hz * t));
            }
        }

        byte[] pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), (short)(Math.Clamp(mix[i], -1f, 1f) * 32000));
        }

        return pcm;
    }
}

/// <summary>
/// Backings rendered on the phone, as the web's player voices them in the browser: the Practice chords from the
/// server's BackingNotes (looped, with a count-in), and a guitar jam strummed with plucked strings (Karplus–Strong).
/// </summary>
public static class Render
{
    public const int Rate = 22_050;

    /// <summary>The chords looped <paramref name="loops"/> times after <paramref name="countInBeats"/> clicks; a click on every beat when asked.</summary>
    public static byte[] Backing(IReadOnlyList<NoteEvent> notes, double loopSec, double bpm, int countInBeats, int loops, bool click)
    {
        double beat = 60.0 / bpm;
        double lead = countInBeats * beat;
        double total = lead + (loopSec * loops) + 1;
        float[] mix = new float[(int)(total * Rate)];
        for (int b = 0; b < countInBeats; b++)
        {
            Click(mix, b * beat, b == 0);
        }

        for (int loop = 0; loop < loops; loop++)
        {
            double offset = lead + (loop * loopSec);
            foreach (NoteEvent n in notes)
            {
                Tone(mix, offset + n.StartSec, n.DurationSec, n.MidiPitch, n.Velocity / 127.0 * 0.22);
            }

            if (click)
            {
                for (double t = 0; t < loopSec - 0.001; t += beat)
                {
                    Click(mix, offset + t, Math.Abs(t % (beat * 4)) < 0.001);
                }
            }
        }

        return ToWav(mix);
    }

    /// <summary>The jam's form, section by section, each chord strummed down-up on its beats; then the ending chord rings.</summary>
    public static (byte[] Wav, double LeadSec) Jam(ChordJamDto jam, int countInBeats)
    {
        double beat = 60.0 / jam.Bpm;
        List<(JamChordDto Chord, double At, int Beats)> plan = [];
        double t = countInBeats * beat;
        foreach (int index in jam.Form)
        {
            JamSectionDto section = jam.Sections[index];
            for (int r = 0; r < section.Repeats; r++)
            {
                foreach (JamChordDto chord in section.Chords)
                {
                    plan.Add((chord, t, chord.Beats));
                    t += chord.Beats * beat;
                }
            }
        }

        plan.Add((jam.Ending, t, 4));
        float[] mix = new float[(int)((t + (4 * beat) + 2) * Rate)];
        Random rng = new(jam.Id.GetHashCode(StringComparison.Ordinal));
        for (int b = 0; b < countInBeats; b++)
        {
            Click(mix, b * beat, b == 0);
        }

        foreach (var (chord, at, beats) in plan)
        {
            bool last = ReferenceEquals(chord, jam.Ending);
            for (int b = 0; b < (last ? 1 : beats); b++)
            {
                Strum(mix, at + (b * beat), chord.Midi, down: b % 2 == 0, level: b == 0 ? 0.32 : 0.2, ring: last ? 4 * beat : beat * 1.2, rng);
            }
        }

        return (ToWav(mix), countInBeats * beat);
    }

    private static void Tone(float[] mix, double start, double dur, int midi, double level)
    {
        double hz = Notes.Hz(midi);
        int from = (int)(start * Rate);
        int to = Math.Min(mix.Length, (int)((start + dur + 0.15) * Rate));
        for (int i = Math.Max(0, from); i < to; i++)
        {
            double t = (i - from) / (double)Rate;
            double env = Math.Min(1, t * 80) * (t > dur ? Math.Exp(-(t - dur) * 25) : Math.Exp(-t * 0.6));
            double x = 2 * Math.PI * hz * t;
            mix[i] += (float)(level * env * (Math.Sin(x) + (0.35 * Math.Sin(2 * x)) + (0.12 * Math.Sin(3 * x))));
        }
    }

    private static void Click(float[] mix, double at, bool accent)
    {
        int from = (int)(at * Rate);
        double hz = accent ? 2000 : 1300;
        for (int i = 0; i < Rate / 30 && from + i < mix.Length; i++)
        {
            double t = i / (double)Rate;
            mix[from + i] += (float)(0.35 * Math.Exp(-t * 90) * Math.Sin(2 * Math.PI * hz * t));
        }
    }

    private static void Strum(float[] mix, double at, int[] strings, bool down, double level, double ring, Random rng)
    {
        IEnumerable<int> order = down ? strings : strings.Reverse();
        int k = 0;
        foreach (int midi in order)
        {
            Pluck(mix, at + (k++ * 0.012), midi, level, ring, rng);
        }
    }

    private static void Pluck(float[] mix, double at, int midi, double level, double ring, Random rng)
    {
        int period = Math.Max(2, (int)(Rate / Notes.Hz(midi)));
        float[] line = new float[period];
        for (int i = 0; i < period; i++)
        {
            line[i] = (float)((rng.NextDouble() * 2) - 1);
        }

        int from = (int)(at * Rate);
        int length = (int)(ring * Rate);
        for (int i = 0; i < length && from + i < mix.Length; i++)
        {
            int j = i % period;
            float next = line[(j + 1) % period];
            float sample = line[j];
            line[j] = 0.497f * (sample + next);
            double fade = i > length - (Rate / 20) ? (length - i) / (Rate / 20.0) : 1;
            mix[from + i] += (float)(sample * level * fade);
        }
    }

    private static byte[] ToWav(float[] mix)
    {
        byte[] pcm = new byte[mix.Length * 2];
        for (int i = 0; i < mix.Length; i++)
        {
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), (short)(Math.Clamp(mix[i], -1f, 1f) * 32000));
        }

        return Wav.Build(pcm, Rate);
    }
}
