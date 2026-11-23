using Microsoft.Playwright;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class ShellSmokeTests(AppFixture app)
{
    [BrowserFact]
    public async Task Shell_renders_header_without_mock_banner_on_cold_load()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1366, Height = 900 } })).NewPageAsync();
        await using var guard = PlaywrightErrorGuard.Watch(page);

        await page.GotoAsync(app.BaseUrl);

        // First WASM load downloads the framework and can be slow; raise only the assertion
        // timeout (not a Task.Delay) so Playwright's auto-wait tolerates it.
        var expectOptions = new LocatorAssertionsToBeVisibleOptions { Timeout = AppFixture.ExpectTimeoutMs };
        // No job has run yet — there's nothing on screen, mock or otherwise, so the banner must stay
        // hidden. It only flips on when a completed job's plan actually touched a fake/placeholder
        // executor (see MockDataState.SetMock in Song.razor).
        var hiddenOptions = new LocatorAssertionsToBeHiddenOptions { Timeout = AppFixture.ExpectTimeoutMs };
        await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeHiddenAsync(hiddenOptions);
        await Assertions.Expect(page.GetByText("PoMode").First).ToBeVisibleAsync(expectOptions);
        // The Po shell: the name upper-left, the one settings gear upper-right, the vYYMMDD stamp, and
        // no theme toggle or More menu (the account and Diagnostics moved to /settings).
        await Assertions.Expect(page.Locator("header.po-header .po-header__title")).ToHaveTextAsync("PoMode", new() { Timeout = AppFixture.ExpectTimeoutMs });
        await Assertions.Expect(page.Locator("header #open-settings.po-header__settings")).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.Locator(".po-version")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^v[0-9]{6}$"));
        // The stamp is the page's footer landmark (axe "region": no text outside a landmark).
        await Assertions.Expect(page.GetByRole(AriaRole.Contentinfo)).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^v[0-9]{6}$"));
        // Radzen's CSS loads once, before every sheet of the app's own, so the app's rules win the cascade
        // (a <RadzenTheme> would inject a second copy after app.css and flip that).
        var sheets = await page.EvaluateAsync<string[]>(
            "[...document.querySelectorAll('link[rel=stylesheet], style')].map(s => s.href ? new URL(s.href).pathname : 'style')");
        var radzen = sheets.Select((href, i) => (href, i)).Where(s => s.href.Contains("/Radzen.Blazor/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(radzen);
        Assert.Equal(radzen.Length, radzen.Select(s => s.href).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, radzen.Length), radzen.Select(s => s.i));
        Assert.Contains("/css/app.css", sheets);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "More" })).ToHaveCountAsync(0);
        Assert.Equal("Inter", (await page.EvaluateAsync<string>("getComputedStyle(document.body).fontFamily")).Split(',')[0].Trim('"', ' '));

        // Ctrl+K goes anywhere: typing a page's name and Enter lands on it. Settings is where the
        // optional choices live, each already at its best value.
        // The shortcut listener attaches after the layout's first render (palette.js marks <html>);
        // a key pressed before that is lost, which made this step flaky.
        await page.WaitForSelectorAsync("html[data-palette=ready]", new() { State = WaitForSelectorState.Attached, Timeout = AppFixture.ExpectTimeoutMs });
        await page.Keyboard.PressAsync("Control+k");
        var palette = page.GetByRole(AriaRole.Combobox);
        await Assertions.Expect(palette).ToBeFocusedAsync(new() { Timeout = AppFixture.ExpectTimeoutMs });
        await palette.FillAsync("settings");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/settings$"), new() { Timeout = AppFixture.ExpectTimeoutMs });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Settings", Level = 1 })).ToBeVisibleAsync(expectOptions);

        // Diagnostics is an operator's page: off until Settings switches it on, then linked from the
        // account section at once (the page listens, no reload).
        await Assertions.Expect(page.Locator("#open-diag")).ToHaveCountAsync(0);
        await page.Locator("#more-tools .settings-row", new() { HasText = "Diagnostics" }).Locator(".rz-switch").ClickAsync();
        await Assertions.Expect(page.Locator("#open-diag")).ToBeVisibleAsync(expectOptions);

        // /diag is the page, signed out or not, at a desktop and a phone width (the JSON is /api/diag);
        // the guard fails the test on any console error along the way.
        await page.GotoAsync($"{app.BaseUrl}/diag");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "System status", Level = 1 })).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.Locator("[data-testid=diag-banner]")).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Services" })).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.Locator(".po-diag__card .po-badge").First).ToBeVisibleAsync(expectOptions);
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("[data-testid=diag-banner]")).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.Locator("header #open-settings")).ToBeVisibleAsync(expectOptions);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"),
            "/diag scrolls sideways at 390px");
    }
}
