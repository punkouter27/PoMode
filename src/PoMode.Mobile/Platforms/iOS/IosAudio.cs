using AVFoundation;
using CoreAudioKit;
using CoreMidi;
using Foundation;
using MediaPlayer;
using PoMode.Mobile.Services;
using UIKit;
using UserNotifications;

namespace PoMode.Mobile;

internal static class AudioSessionSetup
{
    /// <summary>Play-and-record (voice processing on when singing over the speaker), audio that carries on when the screen locks.</summary>
    public static void Activate(bool voiceChat)
    {
        AVAudioSession session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetoothA2DP | AVAudioSessionCategoryOptions.MixWithOthers, out _);
        session.SetMode(voiceChat ? AVAudioSessionMode.VoiceChat : AVAudioSessionMode.Default, out _);
        session.SetActive(true, out _);
    }
}

public sealed class IosMicrophone : IMicrophone, IDisposable
{
    private AVAudioEngine? _engine;

    public Task<bool> StartAsync(Action<byte[]> onPcm16k, bool voiceProcessing)
    {
        AudioSessionSetup.Activate(voiceProcessing);
        _engine = new AVAudioEngine();
        AVAudioInputNode input = _engine.InputNode;
        if (voiceProcessing)
        {
            input.SetVoiceProcessingEnabled(true, out _);
        }

        AVAudioFormat source = input.GetBusOutputFormat(0);
        AVAudioFormat target = new(AVAudioCommonFormat.PCMInt16, Wav.Rate, 1, interleaved: true);
        AVAudioConverter converter = new(source, target);
        AVAudioNodeTapBlock tap = (buffer, _) =>
        {
            uint capacity = (uint)(buffer.FrameLength * Wav.Rate / source.SampleRate) + 32;
            using AVAudioPcmBuffer output = new(target, capacity);
            bool given = false;
            converter.ConvertToBuffer(output, out NSError? error, (uint _, out AVAudioConverterInputStatus status) =>
            {
                status = given ? AVAudioConverterInputStatus.NoDataNow : AVAudioConverterInputStatus.HaveData;
                given = true;
                return buffer;
            });
            if (error is null && output.FrameLength > 0)
            {
                byte[] pcm = new byte[output.FrameLength * 2];
                unsafe
                {
                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)((short**)output.Int16ChannelData)[0], pcm, 0, pcm.Length);
                }

                onPcm16k(pcm);
            }
        };
        if (OperatingSystem.IsIOSVersionAtLeast(27))
        {
            input.InstallTapOnBus(0, (uint)(source.SampleRate / 10), source, out _, tap);
        }
        else
        {
            input.InstallTapOnBus(0, (uint)(source.SampleRate / 10), source, tap);
        }

        _engine.Prepare();
        return Task.FromResult(_engine.StartAndReturnError(out NSError? startError) && startError is null);
    }

    public void Stop()
    {
        _engine?.InputNode.RemoveTapOnBus(0);
        _engine?.Stop();
        _engine?.Dispose();
        _engine = null;
    }

    public void Dispose() => Stop();
}

public sealed class IosTrackPlayerFactory : ITrackPlayerFactory
{
    public ITrackPlayer Create() => new IosTrackPlayer();
}

/// <summary>AVAudioPlayer on one file.</summary>
public sealed class IosTrackPlayer : ITrackPlayer
{
    private AVAudioPlayer? _player;
    private double _volume = 1;

    public event Action? Ended;

    public bool IsPlaying => _player?.Playing == true;

    public double PositionSec => _player?.CurrentTime ?? 0;

    public double DurationSec => _player?.Duration ?? 0;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_player is not null)
            {
                _player.Volume = (float)_volume;
            }
        }
    }

    public bool Load(string path)
    {
        AudioSessionSetup.Activate(voiceChat: false);
        _player?.Dispose();
        _player = AVAudioPlayer.FromUrl(NSUrl.FromFilename(path), out NSError? error);
        if (_player is null || error is not null)
        {
            return false;
        }

        _player.Volume = (float)_volume;
        _player.FinishedPlaying += (_, _) => Ended?.Invoke();
        return _player.PrepareToPlay();
    }

    public void Play() => _player?.Play();

    public void Pause() => _player?.Pause();

    public void Seek(double seconds)
    {
        if (_player is not null)
        {
            _player.CurrentTime = seconds;
        }
    }

    public void Dispose() => _player?.Dispose();
}

/// <summary>An AVAudioPlayerNode kept three buffers ahead, each rendered by the <see cref="ToneMixer"/> as the last one finishes.</summary>
public sealed class IosAudioOut : IAudioOut, IDisposable
{
    private const uint Frames = 2048;
    private AVAudioEngine? _engine;
    private AVAudioPlayerNode? _node;
    private AVAudioFormat? _format;
    private ToneMixer? _mixer;
    private volatile bool _running;

    public bool Running => _running;

    public void Start(ToneMixer mixer)
    {
        if (_running)
        {
            return;
        }

        AudioSessionSetup.Activate(voiceChat: false);
        _mixer = mixer;
        _engine = new AVAudioEngine();
        _node = new AVAudioPlayerNode();
        _format = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, ToneMixer.Rate, 1, interleaved: false);
        _engine.AttachNode(_node);
        if (OperatingSystem.IsIOSVersionAtLeast(27))
        {
            _engine.Connect(_node, _engine.MainMixerNode, _format, out _);
        }
        else
        {
            _engine.Connect(_node, _engine.MainMixerNode, _format);
        }
        _engine.Prepare();
        _engine.StartAndReturnError(out _);
        _running = true;
        for (int i = 0; i < 3; i++)
        {
            ScheduleNext();
        }

        if (OperatingSystem.IsIOSVersionAtLeast(27))
        {
            _node.Play(out _);
        }
        else
        {
            _node.Play();
        }
    }

    private void ScheduleNext()
    {
        if (!_running || _node is null || _format is null || _mixer is null)
        {
            return;
        }

        AVAudioPcmBuffer buffer = new(_format, Frames) { FrameLength = Frames };
        unsafe
        {
            float* samples = ((float**)buffer.FloatChannelData)[0];
            _mixer.Render(new Span<float>(samples, (int)Frames));
        }

        _node.ScheduleBuffer(buffer, () =>
        {
            buffer.Dispose();
            ScheduleNext();
        });
    }

    public void Stop()
    {
        _running = false;
        _node?.Stop();
        _engine?.Stop();
        _node?.Dispose();
        _engine?.Dispose();
        _format?.Dispose();
        _node = null;
        _engine = null;
        _format = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Item 6 on iOS: Now Playing on the lock screen, with play/pause and skip back/forward as "5 BPM slower/faster".</summary>
public sealed class IosToneSession : IToneSession
{
    private ToneMixer? _mixer;
    private bool _wired;

    public void Show(ToneMixer mixer)
    {
        _mixer = mixer;
        MPRemoteCommandCenter commands = MPRemoteCommandCenter.Shared;
        if (!_wired)
        {
            _wired = true;
            commands.TogglePlayPauseCommand.AddTarget(_ => Do(m => m.TogglePlay()));
            commands.PlayCommand.AddTarget(_ => Do(m =>
            {
                if (!m.Active)
                {
                    m.TogglePlay();
                }
            }));
            commands.PauseCommand.AddTarget(_ => Do(m =>
            {
                if (m.Active)
                {
                    m.TogglePlay();
                }
            }));
            commands.SkipBackwardCommand.PreferredIntervals = [5];
            commands.SkipForwardCommand.PreferredIntervals = [5];
            commands.SkipBackwardCommand.AddTarget(_ => Do(m => m.Set(bpm: m.Bpm - 5)));
            commands.SkipForwardCommand.AddTarget(_ => Do(m => m.Set(bpm: m.Bpm + 5)));
            mixer.Changed += Refresh;
        }

        commands.SkipBackwardCommand.Enabled = true;
        commands.SkipForwardCommand.Enabled = true;
        Refresh();
    }

    public void Hide() => MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo();

    private MPRemoteCommandHandlerStatus Do(Action<ToneMixer> change)
    {
        if (_mixer is null)
        {
            return MPRemoteCommandHandlerStatus.CommandFailed;
        }

        change(_mixer);
        return MPRemoteCommandHandlerStatus.Success;
    }

    private void Refresh()
    {
        if (_mixer is { } m)
        {
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo
            {
                Title = m.Title,
                Artist = "PoMode practice",
                PlaybackRate = m.Active ? 1 : 0,
            };
        }
    }
}

/// <summary>Item 5 on iOS: CoreMIDI sources (USB, and Bluetooth ones paired through the system sheet).</summary>
public sealed class IosMidiInput : IMidiInput, IDisposable
{
    private MidiClient? _client;
    private MidiPort? _port;
    private MidiEndpoint? _source;
    private Action<int, int>? _onNote;

    public bool IsSupported => true;

    public string? Connected { get; private set; }

    public Task<IReadOnlyList<string>> ListAsync()
    {
        List<string> names = [];
        for (nint i = 0; i < Midi.SourceCount; i++)
        {
            if (MidiEndpoint.GetSource(i) is { } source)
            {
                names.Add(source.DisplayName ?? source.Name ?? $"MIDI source {i}");
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    /// <summary>The system's Bluetooth MIDI sheet: pairing happens there, and the keyboard then shows as a source.</summary>
    public async Task ScanBluetoothAsync()
    {
        TaskCompletionSource closed = new();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            UIViewController? top = Platform.GetCurrentUIViewController();
            if (top is null)
            {
                closed.TrySetResult();
                return;
            }

            CABTMidiCentralViewController central = new();
            UINavigationController nav = new(central);
            central.NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) =>
                nav.DismissViewController(true, () => closed.TrySetResult()));
            top.PresentViewController(nav, true, null);
        });
        await closed.Task;
    }

    public Task<bool> OpenAsync(string device, Action<int, int> onNote)
    {
        Close();
        _onNote = onNote;
        for (nint i = 0; i < Midi.SourceCount; i++)
        {
            if (MidiEndpoint.GetSource(i) is { } source && (source.DisplayName ?? source.Name) == device)
            {
                _client = new MidiClient("PoMode");
                _port = _client.CreateInputPort("PoMode in");
                _port.MessageReceived += OnPackets;
                _port.ConnectSource(source);
                _source = source;
                Connected = device;
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    private void OnPackets(object? sender, MidiPacketsEventArgs e)
    {
        foreach (MidiPacket packet in e.Packets)
        {
            byte[] bytes = packet.ByteArray ?? [];
            for (int i = 0; i + 2 < bytes.Length + 1 && i + 2 < bytes.Length; i++)
            {
                int status = bytes[i] & 0xF0;
                if (status is 0x90 or 0x80)
                {
                    _onNote?.Invoke(bytes[i + 1] & 0x7F, status == 0x80 ? 0 : bytes[i + 2] & 0x7F);
                    i += 2;
                }
            }
        }
    }

    public void Close()
    {
        if (_port is not null && _source is not null)
        {
            _port.Disconnect(_source);
        }

        _port?.Dispose();
        _client?.Dispose();
        _port = null;
        _client = null;
        _source = null;
        Connected = null;
    }

    public void Dispose() => Close();
}

/// <summary>Item 4 on iOS: a background task keeps an upload going for the time iOS allows after the app is left.</summary>
public sealed class IosBackgroundWork : IBackgroundWork
{
    public void Start(Func<CancellationToken, Task> work, string status)
    {
        nint task = 0;
        CancellationTokenSource cts = new();
        task = UIApplication.SharedApplication.BeginBackgroundTask("pomode-upload", () =>
        {
            cts.Cancel();
            UIApplication.SharedApplication.EndBackgroundTask(task);
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await work(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                MainThread.BeginInvokeOnMainThread(() => UIApplication.SharedApplication.EndBackgroundTask(task));
                cts.Dispose();
            }
        });
    }

    public void Update(string status)
    {
    }
}

/// <summary>"Analysis ready" as a local notification; tapping it opens the song.</summary>
public sealed class IosNotifier : INotifier
{
    private const string LinkKey = "link";

    public async Task<bool> RequestAsync()
    {
        (bool granted, _) = await UNUserNotificationCenter.Current.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound);
        return granted;
    }

    public void Show(string title, string text, Uri link)
    {
        UNMutableNotificationContent content = new()
        {
            Title = title,
            Body = text,
            Sound = UNNotificationSound.Default,
            UserInfo = NSDictionary.FromObjectAndKey(new NSString(link.ToString()), new NSString(LinkKey)),
        };
        UNUserNotificationCenter.Current.AddNotificationRequest(UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString("N"), content, null), _ => { });
    }

    /// <summary>Shows them while the app is open too, and opens the song when one is tapped.</summary>
    public sealed class Delegate : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
            => completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound | UNNotificationPresentationOptions.List);

        public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
        {
            if (response.Notification.Request.Content.UserInfo[LinkKey] is NSString link && Uri.TryCreate(link.ToString(), UriKind.Absolute, out Uri? uri))
            {
                Microsoft.Maui.Controls.Application.Current?.SendOnAppLinkRequestReceived(uri);
            }

            completionHandler();
        }
    }
}
