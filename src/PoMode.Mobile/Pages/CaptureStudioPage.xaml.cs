using PoMode.Mobile.Services;
using PoMode.Shared.Analysis;

namespace PoMode.Mobile.Pages;

public partial class CaptureStudioPage : ContentPage
{
    private readonly PoModeMobileClient _client;
    private readonly OfflineTakeQueue _queue;

    public CaptureStudioPage(PoModeMobileClient client, OfflineTakeQueue queue)
    {
        _client = client;
        _queue = queue;
        InitializeComponent();
        _client.JobUpdated += OnJobUpdated;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        QueueLabel.Text = $"Pending offline takes: {(await _queue.ListPendingAsync()).Count}";
    }

    private void OnJobUpdated(JobStatusDto status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            JobProgressLabel.Text = $"Job {status.JobId}: {status.Stage} ({status.Progress:P0})";
            if (status.Stage.IsTerminal() && HapticFeedback.Default.IsSupported)
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.LongPress);
            }
        });
    }

    private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess == NetworkAccess.Internet)
        {
            await FlushQueueInternalAsync();
        }
    }

    private async void OnMicPermissionClicked(object? sender, EventArgs e)
    {
        var status = await Permissions.RequestAsync<Permissions.Microphone>();
        StatusLabel.Text = $"Microphone permission: {status}";
    }

    private async void OnPickAudioClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select MP3 or WAV audio",
            });
            if (result is null)
            {
                return;
            }

            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                await _queue.EnqueueFileAsync(result.FullPath, result.FileName);
                QueueLabel.Text = $"Pending offline takes: {(await _queue.ListPendingAsync()).Count}";
                StatusLabel.Text = $"Offline — queued {result.FileName} for upload.";
                return;
            }

            StatusLabel.Text = $"Uploading {result.FileName} via tus...";
            var job = await _client.UploadViaTusAndStartAsync(result.FullPath, result.FileName);
            JobProgressLabel.Text = $"Job {job.JobId}: {job.Stage}";
            await _client.ConnectJobHubAsync(job.JobId);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Upload error: {ex.Message}";
        }
    }

    private async void OnFlushQueueClicked(object? sender, EventArgs e) => await FlushQueueInternalAsync();

    private async Task FlushQueueInternalAsync()
    {
        var pending = await _queue.ListPendingAsync();
        var uploaded = 0;
        foreach (var take in pending)
        {
            try
            {
                var job = await _client.UploadViaTusAndStartAsync(take.LocalPath, take.FileName);
                await _queue.RemoveAsync(take.Id);
                uploaded++;
                await _client.ConnectJobHubAsync(job.JobId);
            }
            catch
            {
                break;
            }
        }

        var remaining = (await _queue.ListPendingAsync()).Count;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            QueueLabel.Text = $"Pending offline takes: {remaining}";
            if (uploaded > 0)
            {
                StatusLabel.Text = $"Uploaded {uploaded} queued take(s).";
            }
        });
    }
}
