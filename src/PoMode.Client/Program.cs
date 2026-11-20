using FluentValidation;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using PoMode.ApiClient;
using PoMode.Client;
using PoMode.Client.Services;
using PoMode.Shared.Analysis;
using PoMode.Shared.Validation;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddRadzenComponents();
builder.Services.AddSingleton<MockDataState>();
// The shared API client; the browser buffers a fetch's whole body unless streaming is switched on per
// request, so the written interpretation would otherwise arrive all at once.
builder.Services.AddScoped(services => new AnalysisClient(services.GetRequiredService<HttpClient>())
{
    PrepareStreaming = request => request.SetBrowserResponseStreamingEnabled(true),
});
builder.Services.AddScoped<ChordFingerings>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<BrowserPrefs>();
builder.Services.AddScoped<FeatureFlags>();
builder.Services.AddScoped<IValidator<SongQuestionRequest>, SongQuestionRequestValidator>();
builder.Services.AddScoped<IValidator<ModeLabDescriptionRequest>, ModeLabDescriptionRequestValidator>();
builder.Services.AddScoped<IValidator<ModalMelodyRequest>, ModalMelodyRequestValidator>();
builder.Services.AddScoped<IValidator<ModeLabDescriptionForm>, ModeLabDescriptionFormValidator>();
builder.Services.AddScoped<IValidator<ChordJamPresetForm>, ChordJamPresetFormValidator>();
builder.Services.AddScoped<IValidator<AudioFileChoice>, AudioFileChoiceValidator>();
builder.Services.AddScoped<IValidator<PoMode.Shared.Account.InitialsRequest>, InitialsRequestValidator>();
builder.Services.AddScoped<DevFakeUserHandler>();
// Same-origin, so the session cookie rides on every request with no header to add; the same is true
// of the JS modules' fetch() calls and the browser's own uploads.
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<DevFakeUserHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});

await builder.Build().RunAsync();
