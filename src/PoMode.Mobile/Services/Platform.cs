namespace PoMode.Mobile.Services;

/// <summary>The microphone as 16 kHz mono 16-bit PCM in 100 ms frames; the phone's echo cancellation when asked (singing over a speaker).</summary>
public interface IMicrophone
{
    Task<bool> StartAsync(Action<byte[]> onPcm16k, bool voiceProcessing);

    void Stop();
}

/// <summary>One audio file playing (a backing WAV, a stem). Several can play at once — the stem mixer is two of them.</summary>
public interface ITrackPlayer : IDisposable
{
    bool IsPlaying { get; }

    double PositionSec { get; }

    double DurationSec { get; }

    /// <summary>0..1.</summary>
    double Volume { get; set; }

    bool Load(string path);

    void Play();

    void Pause();

    void Seek(double seconds);

    event Action? Ended;
}

public interface ITrackPlayerFactory
{
    ITrackPlayer Create();
}

/// <summary>A continuous output stream the <see cref="ToneMixer"/> fills (drone, click, a MIDI keyboard's notes).</summary>
public interface IAudioOut
{
    bool Running { get; }

    void Start(ToneMixer mixer);

    void Stop();
}

/// <summary>
/// Item 6: the drone/metronome keeps sounding with the screen locked, with play/pause and tempo on the lock screen.
/// Android: a media-playback foreground service with a MediaSession; iOS: background audio + Now Playing + remote commands.
/// </summary>
public interface IToneSession
{
    void Show(ToneMixer mixer);

    void Hide();
}

/// <summary>Item 5: a Bluetooth LE or USB MIDI keyboard. Notes come in as (MIDI note, velocity; 0 = off).</summary>
public interface IMidiInput
{
    bool IsSupported { get; }

    /// <summary>Connected/visible devices (USB, and Bluetooth ones found by <see cref="ScanBluetoothAsync"/> or paired by the OS).</summary>
    Task<IReadOnlyList<string>> ListAsync();

    /// <summary>Looks for Bluetooth LE MIDI devices for a few seconds (Android), or shows the system pairing sheet (iOS).</summary>
    Task ScanBluetoothAsync();

    Task<bool> OpenAsync(string device, Action<int, int> onNote);

    void Close();

    string? Connected { get; }
}

/// <summary>Item 4: uploads and the wait for the analysis keep going with the app in the background.</summary>
public interface IBackgroundWork
{
    void Start(Func<CancellationToken, Task> work, string status);

    void Update(string status);
}

/// <summary>A local notification that opens a link in the app when tapped (pomode://song/{jobId}).</summary>
public interface INotifier
{
    Task<bool> RequestAsync();

    void Show(string title, string text, Uri link);
}

/// <summary>Taps for the moments that matter (a take starts, an analysis lands). One switch in Settings.</summary>
public static class Haptics
{
    private const string Key = "pomode_haptics";

    public static bool Enabled
    {
        get => Preferences.Default.Get(Key, true);
        set => Preferences.Default.Set(Key, value);
    }

    public static void Tap() => Feedback(HapticFeedbackType.Click);

    public static void Strong() => Feedback(HapticFeedbackType.LongPress);

    private static void Feedback(HapticFeedbackType type)
    {
        try
        {
            if (Enabled && HapticFeedback.Default.IsSupported)
            {
                HapticFeedback.Default.Perform(type);
            }
        }
        catch (FeatureNotSupportedException)
        {
        }
    }
}

/// <summary>Mono 16-bit WAV around raw PCM (what /api/modal-melodies/hum and /api/chord-jams/take read).</summary>
public static class Wav
{
    public const int Rate = 16_000;

    public static byte[] Build(ReadOnlySpan<byte> pcm, int sampleRate = Rate)
    {
        byte[] wav = new byte[44 + pcm.Length];
        void Put(int at, string s) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(wav, at);
        void I32(int at, int v) => BitConverter.TryWriteBytes(wav.AsSpan(at, 4), v);
        void I16(int at, short v) => BitConverter.TryWriteBytes(wav.AsSpan(at, 2), v);
        Put(0, "RIFF");
        I32(4, 36 + pcm.Length);
        Put(8, "WAVE");
        Put(12, "fmt ");
        I32(16, 16);
        I16(20, 1);
        I16(22, 1);
        I32(24, sampleRate);
        I32(28, sampleRate * 2);
        I16(32, 2);
        I16(34, 16);
        Put(36, "data");
        I32(40, pcm.Length);
        pcm.CopyTo(wav.AsSpan(44));
        return wav;
    }
}
