using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using Android.Media.Midi;
using Android.Media.Session;
using Android.OS;
using AndroidX.Core.App;
using PoMode.Mobile.Services;
using AndroidApp = Android.App.Application;
using AUri = Android.Net.Uri;

namespace PoMode.Mobile;

/// <summary>AudioRecord at 16 kHz; VoiceCommunication + the echo canceller while the chords play through the speaker.</summary>
public sealed class AndroidMicrophone : IMicrophone, IDisposable
{
    private AudioRecord? _record;
    private AcousticEchoCanceler? _aec;
    private CancellationTokenSource? _stop;

    public Task<bool> StartAsync(Action<byte[]> onPcm16k, bool voiceProcessing)
    {
        int min = AudioRecord.GetMinBufferSize(Wav.Rate, ChannelIn.Mono, Encoding.Pcm16bit);
        try
        {
            _record = new AudioRecord(voiceProcessing ? AudioSource.VoiceCommunication : AudioSource.Mic, Wav.Rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(min, Wav.Rate));
            if (_record.State != Android.Media.State.Initialized)
            {
                return Task.FromResult(false);
            }

            if (voiceProcessing && AcousticEchoCanceler.IsAvailable)
            {
                _aec = AcousticEchoCanceler.Create(_record.AudioSessionId);
                _aec?.SetEnabled(true);
            }

            _record.StartRecording();
        }
        catch (Java.Lang.Exception)
        {
            return Task.FromResult(false);
        }

        _stop = new CancellationTokenSource();
        CancellationToken ct = _stop.Token;
        AudioRecord record = _record;
        _ = Task.Run(() =>
        {
            byte[] frame = new byte[Wav.Rate / 10 * 2]; // 100 ms
            while (!ct.IsCancellationRequested)
            {
                int read = record.Read(frame, 0, frame.Length);
                if (read > 0)
                {
                    onPcm16k(frame.AsSpan(0, read & ~1).ToArray());
                }
            }
        }, ct);
        return Task.FromResult(true);
    }

    public void Stop()
    {
        _stop?.Cancel();
        try
        {
            _record?.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }

        _aec?.Release();
        _record?.Release();
        _aec = null;
        _record = null;
    }

    public void Dispose()
    {
        Stop();
        _stop?.Dispose();
    }
}

public sealed class AndroidTrackPlayerFactory : ITrackPlayerFactory
{
    public ITrackPlayer Create() => new AndroidTrackPlayer();
}

/// <summary>MediaPlayer on one file (a backing, a stem).</summary>
public sealed class AndroidTrackPlayer : ITrackPlayer
{
    private readonly MediaPlayer _player = new();
    private double _volume = 1;
    private bool _ready;

    public AndroidTrackPlayer() => _player.Completion += (_, _) => Ended?.Invoke();

    public event Action? Ended;

    public bool IsPlaying => _ready && _player.IsPlaying;

    public double PositionSec => _ready ? _player.CurrentPosition / 1000.0 : 0;

    public double DurationSec => _ready ? _player.Duration / 1000.0 : 0;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            _player.SetVolume((float)_volume, (float)_volume);
        }
    }

    public bool Load(string path)
    {
        try
        {
            _player.Reset();
            _player.SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!);
            _player.SetDataSource(path);
            _player.Prepare();
            _ready = true;
        }
        catch (Java.IO.IOException)
        {
            _ready = false;
        }

        return _ready;
    }

    public void Play()
    {
        if (_ready)
        {
            _player.Start();
        }
    }

    public void Pause()
    {
        if (IsPlaying)
        {
            _player.Pause();
        }
    }

    public void Seek(double seconds)
    {
        if (_ready)
        {
            _player.SeekTo((int)(seconds * 1000));
        }
    }

    public void Dispose()
    {
        _player.Release();
        _player.Dispose();
    }
}

/// <summary>A float AudioTrack the <see cref="ToneMixer"/> fills, ~23 ms at a time.</summary>
public sealed class AndroidAudioOut : IAudioOut, IDisposable
{
    private AudioTrack? _track;
    private CancellationTokenSource? _stop;

    public bool Running => _track is not null;

    public void Start(ToneMixer mixer)
    {
        if (_track is not null)
        {
            return;
        }

        int min = AudioTrack.GetMinBufferSize(ToneMixer.Rate, ChannelOut.Mono, Encoding.PcmFloat);
        _track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!)!
            .SetAudioFormat(new AudioFormat.Builder().SetSampleRate(ToneMixer.Rate)!.SetEncoding(Encoding.PcmFloat)!.SetChannelMask(ChannelOut.Mono)!.Build()!)!
            .SetBufferSizeInBytes(Math.Max(min, 4096 * 4))!
            .SetTransferMode(AudioTrackMode.Stream)!
            .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)!
            .Build()!;
        _track.Play();
        _stop = new CancellationTokenSource();
        CancellationToken ct = _stop.Token;
        AudioTrack track = _track;
        Thread thread = new(() =>
        {
            float[] buffer = new float[1024];
            while (!ct.IsCancellationRequested)
            {
                mixer.Render(buffer);
                track.Write(buffer, 0, buffer.Length, WriteMode.Blocking);
            }
        })
        { IsBackground = true, Priority = System.Threading.ThreadPriority.Highest };
        thread.Start();
    }

    public void Stop()
    {
        _stop?.Cancel();
        _track?.Stop();
        _track?.Release();
        _track = null;
    }

    public void Dispose()
    {
        Stop();
        _stop?.Dispose();
    }
}

/// <summary>Item 6: shows the drone/metronome as a media session — lock screen and notification shade controls.</summary>
public sealed class AndroidToneSession : IToneSession
{
    private static Context Ctx => AndroidApp.Context;

    public void Show(ToneMixer mixer)
    {
        ToneService.Mixer = mixer;
        Ctx.StartForegroundService(new Intent(Ctx, typeof(ToneService)));
    }

    public void Hide() => Ctx.StopService(new Intent(Ctx, typeof(ToneService)));
}

/// <summary>
/// Keeps the process (and so the AudioTrack thread) alive with the screen locked: a media-playback foreground service
/// with a MediaSession whose play/pause and "slower/faster" custom actions show on the lock screen.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class ToneService : Service
{
    private const int Id = 606;
    private const string Channel = "tone";
    private const string Toggle = "toggle", Slower = "slower", Faster = "faster", StopAll = "stop";
    private MediaSession? _session;

    public static ToneMixer? Mixer { get; set; }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (Mixer is not { } mixer)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        Apply(intent?.Action, mixer);
        if (_session is null)
        {
            _session = new MediaSession(this, "PoModeTone");
            _session.SetCallback(new Callback(this));
            _session.Active = true;
            mixer.Changed += Refresh;
        }

        Notification note = Build(mixer);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(Id, note, ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(Id, note);
        }

        return StartCommandResult.NotSticky;
    }

    private void Apply(string? action, ToneMixer mixer)
    {
        switch (action)
        {
            case Toggle:
                mixer.TogglePlay();
                break;
            case Slower:
                mixer.Set(bpm: mixer.Bpm - 5);
                break;
            case Faster:
                mixer.Set(bpm: mixer.Bpm + 5);
                break;
            case StopAll:
                mixer.Set(drone: false, click: false);
                StopSelf();
                break;
        }
    }

    private void Refresh()
    {
        if (Mixer is { } mixer && _session is not null)
        {
            ((NotificationManager)GetSystemService(NotificationService)!).Notify(Id, Build(mixer));
        }
    }

    private Notification Build(ToneMixer mixer)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Drone and metronome", NotificationImportance.Low));
        bool playing = mixer.Active;
        _session!.SetMetadata(new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, mixer.Title)!
            .PutString(MediaMetadata.MetadataKeyArtist, "PoMode practice")!
            .Build());
        _session.SetPlaybackState(new PlaybackState.Builder()
            .SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause | PlaybackState.ActionStop)!
            .AddCustomAction(Slower, "Slower", Android.Resource.Drawable.IcMediaRew)!
            .AddCustomAction(Faster, "Faster", Android.Resource.Drawable.IcMediaFf)!
            .SetState(playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused, PlaybackState.PlaybackPositionUnknown, 1)!
            .Build());

        Notification.Builder builder = new(this, Channel);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(mixer.Title);
        builder.SetContentText("PoMode practice");
        builder.SetVisibility(NotificationVisibility.Public);
        builder.SetOngoing(playing);
        builder.SetContentIntent(PendingIntent.GetActivity(this, 0, PackageManager!.GetLaunchIntentForPackage(PackageName!)!, PendingIntentFlags.Immutable));
        builder.AddAction(Action(Slower, "−5", Android.Resource.Drawable.IcMediaRew));
        builder.AddAction(Action(Toggle, playing ? "Pause" : "Play", playing ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay));
        builder.AddAction(Action(Faster, "+5", Android.Resource.Drawable.IcMediaFf));
        builder.AddAction(Action(StopAll, "Stop", Android.Resource.Drawable.IcMenuCloseClearCancel));
        builder.SetStyle(new Notification.MediaStyle().SetMediaSession(_session.SessionToken)!.SetShowActionsInCompactView(0, 1, 2));
        return builder.Build();
    }

    private Notification.Action Action(string action, string title, int icon)
    {
        PendingIntent intent = PendingIntent.GetService(this, action.GetHashCode(StringComparison.Ordinal), new Intent(this, typeof(ToneService)).SetAction(action), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        return new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, icon), title, intent).Build()!;
    }

    public override void OnDestroy()
    {
        if (Mixer is { } mixer)
        {
            mixer.Changed -= Refresh;
        }

        _session?.Release();
        _session = null;
        base.OnDestroy();
    }

    private sealed class Callback(ToneService owner) : MediaSession.Callback
    {
        public override void OnPlay() => Send(Toggle, playWanted: true);

        public override void OnPause() => Send(Toggle, playWanted: false);

        public override void OnStop() => owner.Apply(StopAll, Mixer!);

        public override void OnCustomAction(string action, Bundle? extras) => owner.Apply(action, Mixer!);

        private void Send(string action, bool playWanted)
        {
            if (Mixer is { } mixer && mixer.Active != playWanted)
            {
                owner.Apply(action, mixer);
            }
        }
    }
}

/// <summary>Item 5: USB MIDI through MidiManager, Bluetooth LE MIDI found by a scan for the MIDI service and opened through it.</summary>
public sealed class AndroidMidiInput : IMidiInput, IDisposable
{
    private static readonly Java.Util.UUID MidiService = Java.Util.UUID.FromString("03B80E5A-EDE8-4B33-A751-6CE34EC4C700")!;
    private readonly Dictionary<string, BluetoothDevice> _bluetooth = [];
    private MidiDevice? _device;
    private MidiOutputPort? _port;

    private static Context Ctx => AndroidApp.Context;

    private static MidiManager? Manager => Ctx.GetSystemService(Context.MidiService) as MidiManager;

    public bool IsSupported => Ctx.PackageManager?.HasSystemFeature(PackageManager.FeatureMidi) == true && Manager is not null;

    public string? Connected { get; private set; }

    public Task<IReadOnlyList<string>> ListAsync()
    {
        List<string> names = [.. Devices().Select(Name)];
        names.AddRange(_bluetooth.Keys.Where(k => !names.Contains(k)));
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public async Task ScanBluetoothAsync()
    {
        if (await Permissions.RequestAsync<Permissions.Bluetooth>() != PermissionStatus.Granted
            || (Ctx.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter?.BluetoothLeScanner is not { } scanner)
        {
            return;
        }

        Scan callback = new(this);
        ScanFilter filter = new ScanFilter.Builder().SetServiceUuid(new ParcelUuid(MidiService))!.Build()!;
        scanner.StartScan([filter], new ScanSettings.Builder().SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency)!.Build(), callback);
        await Task.Delay(TimeSpan.FromSeconds(6));
        scanner.StopScan(callback);
    }

    public async Task<bool> OpenAsync(string device, Action<int, int> onNote)
    {
        Close();
        if (Manager is not { } manager)
        {
            return false;
        }

        TaskCompletionSource<MidiDevice?> opened = new();
        Opened listener = new(opened);
        Handler handler = new(Looper.MainLooper!);
        if (Devices().FirstOrDefault(d => Name(d) == device) is { } info)
        {
            manager.OpenDevice(info, listener, handler);
        }
        else if (_bluetooth.TryGetValue(device, out BluetoothDevice? bt))
        {
            manager.OpenBluetoothDevice(bt, listener, handler);
        }
        else
        {
            return false;
        }

        _device = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (_device?.Info is not { OutputPortCount: > 0 })
        {
            return false;
        }

        _port = _device.OpenOutputPort(0);
        _port?.Connect(new Receiver(onNote));
        Connected = _port is null ? null : device;
        return _port is not null;
    }

    public void Close()
    {
        _port?.Close();
        _device?.Close();
        _port = null;
        _device = null;
        Connected = null;
    }

    public void Dispose() => Close();

    private static IEnumerable<MidiDeviceInfo> Devices()
    {
        if (Manager is not { } manager)
        {
            return [];
        }

        return OperatingSystem.IsAndroidVersionAtLeast(33)
            ? manager.GetDevicesForTransport((int)MidiTransport.MidiByteStream)
            : manager.GetDevices() ?? [];
    }

    private static string Name(MidiDeviceInfo info)
        => info.Properties?.GetString(MidiDeviceInfo.PropertyName) is { Length: > 0 } name ? name : $"MIDI device {info.Id}";

    private sealed class Scan(AndroidMidiInput owner) : ScanCallback
    {
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            if (result?.Device is { } device)
            {
                owner._bluetooth[$"{device.Name ?? "Bluetooth MIDI"} (Bluetooth)"] = device;
            }
        }
    }

    private sealed class Opened(TaskCompletionSource<MidiDevice?> done) : Java.Lang.Object, MidiManager.IOnDeviceOpenedListener
    {
        public void OnDeviceOpened(MidiDevice? device) => done.TrySetResult(device);
    }

    /// <summary>Note-on (0x9n) and note-off (0x8n, or note-on at velocity 0), any channel.</summary>
    private sealed class Receiver(Action<int, int> onNote) : MidiReceiver
    {
        public override void OnSend(byte[]? msg, int offset, int count, long timestamp)
        {
            if (msg is null)
            {
                return;
            }

            for (int i = offset; i + 2 < offset + count + 1 && i + 2 < msg.Length; i++)
            {
                int status = msg[i] & 0xF0;
                if (status is 0x90 or 0x80)
                {
                    int note = msg[i + 1] & 0x7F;
                    int velocity = status == 0x80 ? 0 : msg[i + 2] & 0x7F;
                    onNote(note, velocity);
                    i += 2;
                }
            }
        }
    }
}

/// <summary>Item 4: a data-sync foreground service holds the process while songs upload and the analyses run.</summary>
public sealed class AndroidBackgroundWork : IBackgroundWork
{
    internal const string ProgressChannel = "uploads";
    private static Context Ctx => AndroidApp.Context;

    public void Start(Func<CancellationToken, Task> work, string status)
    {
        AndroidNotifier.Channels(Ctx);
        Ctx.StartForegroundService(new Intent(Ctx, typeof(UploadService)).PutExtra("text", status));
        _ = Task.Run(async () =>
        {
            try
            {
                await work(CancellationToken.None);
            }
            finally
            {
                Ctx.StopService(new Intent(Ctx, typeof(UploadService)));
            }
        });
    }

    public void Update(string status)
        => NotificationManagerCompat.From(Ctx)!.Notify(UploadService.Id, UploadService.Build(Ctx, status));
}

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class UploadService : Service
{
    public const int Id = 505;
    private PowerManager.WakeLock? _wake;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        Notification note = Build(this, intent?.GetStringExtra("text") ?? "Uploading songs");
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(Id, note, ForegroundService.TypeDataSync);
        }
        else
        {
            StartForeground(Id, note);
        }

        if (_wake is null)
        {
            _wake = ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "pomode:upload");
            _wake!.Acquire((long)TimeSpan.FromHours(1).TotalMilliseconds); // a safety cap; released when the work ends
        }

        return StartCommandResult.NotSticky;
    }

    internal static Notification Build(Context context, string text)
    {
        var builder = new NotificationCompat.Builder(context, AndroidBackgroundWork.ProgressChannel);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("PoMode");
        builder.SetContentText(text);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetProgress(0, 0, true);
        builder.SetContentIntent(PendingIntent.GetActivity(context, 0, context.PackageManager!.GetLaunchIntentForPackage(context.PackageName!)!, PendingIntentFlags.Immutable));
        return builder.Build()!;
    }

    public override void OnDestroy()
    {
        if (_wake?.IsHeld == true)
        {
            _wake.Release();
        }

        base.OnDestroy();
    }
}

/// <summary>"Analysis ready" — high importance, opens pomode://song/{id}.</summary>
public sealed class AndroidNotifier : INotifier
{
    private const string DoneChannel = "done";
    private static Context Ctx => AndroidApp.Context;

    public async Task<bool> RequestAsync()
    {
        Channels(Ctx);
        return !OperatingSystem.IsAndroidVersionAtLeast(33) || await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.PostNotifications>()) == PermissionStatus.Granted;
    }

    public void Show(string title, string text, Uri link)
    {
        Channels(Ctx);
        Intent open = new(Intent.ActionView, AUri.Parse(link.ToString()));
        open.SetPackage(Ctx.PackageName);
        var builder = new NotificationCompat.Builder(Ctx, DoneChannel);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(title);
        builder.SetContentText(text);
        builder.SetAutoCancel(true);
        builder.SetPriority(NotificationCompat.PriorityHigh);
        builder.SetContentIntent(PendingIntent.GetActivity(Ctx, link.GetHashCode(), open, PendingIntentFlags.Immutable));
        NotificationManagerCompat.From(Ctx)!.Notify(System.Environment.TickCount & 0x3FFFFFFF, builder.Build()!);
    }

    internal static void Channels(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(AndroidBackgroundWork.ProgressChannel, "Uploads", NotificationImportance.Low));
        manager.CreateNotificationChannel(new NotificationChannel(DoneChannel, "Finished analyses", NotificationImportance.High));
    }
}
