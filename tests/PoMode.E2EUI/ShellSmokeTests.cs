using Microsoft.Playwright;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class ShellSmokeTests(AppFixture app)
{
    [Fact]
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
        // Settings is in plain sight in the header's upper right, not only behind the ⋯ menu.
        await Assertions.Expect(page.Locator("header #open-settings")).ToBeVisibleAsync(expectOptions);
        // Diagnostics is an operator's page: off until Settings switches it on, then in the overflow menu.
        await page.GetByRole(AriaRole.Button, new() { Name = "More" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Menuitem, new() { Name = "Settings" })).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.GetByRole(AriaRole.Menuitem, new() { Name = "Diagnostics" })).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("Escape");

        // Ctrl+K goes anywhere: typing a page's name and Enter lands on it. Settings is where the
        // optional choices live, each already at its best value.
        await page.Keyboard.PressAsync("Control+k");
        var palette = page.GetByRole(AriaRole.Combobox);
        await Assertions.Expect(palette).ToBeFocusedAsync(new() { Timeout = AppFixture.ExpectTimeoutMs });
        await palette.FillAsync("settings");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/settings$"), new() { Timeout = AppFixture.ExpectTimeoutMs });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Settings", Level = 1 })).ToBeVisibleAsync(expectOptions);

        // Switching the tool on shows it in the menu at once: the layout listens, no reload.
        await page.Locator("#more-tools .settings-row", new() { HasText = "Diagnostics" }).Locator(".rz-switch").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "More" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Menuitem, new() { Name = "Diagnostics" })).ToBeVisibleAsync(expectOptions);

        // /diag is the page, signed out or not, at a desktop and a phone width (the JSON is /api/diag);
        // the guard fails the test on any console error along the way.
        await page.GotoAsync($"{app.BaseUrl}/diag");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Diagnostics", Level = 1 })).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.Locator("#diag-status")).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Services" })).ToBeVisibleAsync(expectOptions);
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("#diag-status")).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.Locator("header #open-settings")).ToBeVisibleAsync(expectOptions);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"),
            "/diag scrolls sideways at 390px");
    }
}
