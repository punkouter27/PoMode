using PoMode.Mobile.Pages;

namespace PoMode.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("song", typeof(SongPage));
        Routing.RegisterRoute("record", typeof(RecordSongPage));
        Routing.RegisterRoute("diag", typeof(DiagPage));
    }
}
