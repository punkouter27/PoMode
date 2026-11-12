using FluentValidation;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
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
builder.Services.AddScoped<AnalysisClient>();
builder.Services.AddScoped<ChordFingerings>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<BrowserPrefs>();
builder.Services.AddScoped<ThemeState>();
builder.Services.AddScoped<FeatureFlags>();
builder.Services.AddScoped<DialogNotificationHelper>();
builder.Services.AddScoped<IValidator<SongQuestionRequest>, SongQuestionRequestValidator>();
builder.Services.AddScoped<IValidator<ModeLabDescriptionRequest>, ModeLabDescriptionRequestValidator>();
builder.Services.AddScoped<IValidator<ModalMelodyRequest>, ModalMelodyRequestValidator>();
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
