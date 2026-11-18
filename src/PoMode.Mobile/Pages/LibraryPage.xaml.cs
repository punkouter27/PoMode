using PoMode.Mobile.Services;

namespace PoMode.Mobile.Pages;

public partial class LibraryPage : ContentPage
{
    private readonly PoModeMobileClient _client;

    public LibraryPage(PoModeMobileClient client)
    {
        _client = client;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        SongsList.Clear();
        try
        {
            var entries = await _client.GetLibraryAsync();
            StatusLabel.Text = $"Songs: {entries.Count} · Network: {Connectivity.Current.NetworkAccess}";
            foreach (var entry in entries)
            {
                var keyInfo = string.IsNullOrWhiteSpace(entry.PrimaryMode)
                    ? entry.Stage.ToString()
                    : $"{entry.TonicName} {entry.PrimaryMode} · {entry.TempoBpm:F0} BPM";
                SongsList.Add(new Label { Text = $"{entry.FileName} — {keyInfo}" });
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
        }
    }
}
