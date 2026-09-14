using System.Text.Json;
using StrangeSharpTerm.App.Assistant;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.Tests;

public class AssistSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"strangesharpterm-assist-{Guid.NewGuid():N}");

    private string Path_ => System.IO.Path.Combine(_directory, Preferences.FileName);

    public AssistSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void TheDefaultsAreWhatAFreshInstallGets()
    {
        var settings = AssistPreferences.Load(Path_);

        settings.Provider.ShouldBe(AssistProviderId.Claude);
        settings.SendMetrics.ShouldBeTrue();
        settings.SendTerminalTail.ShouldBeTrue();
        settings.AllowCommandsByDefault.ShouldBeFalse();
    }

    [Fact]
    public void AChoiceSurvivesARelaunch()
    {
        AssistPreferences.Save(
            Path_,
            new AssistSettings
            {
                Provider = AssistProviderId.DeepSeek,
                Model = "deepseek-v3",
                SendTerminalTail = false,
            });

        var settings = AssistPreferences.Load(Path_);

        settings.Provider.ShouldBe(AssistProviderId.DeepSeek);
        settings.ModelName.ShouldBe("deepseek-v3");
        settings.SendTerminalTail.ShouldBeFalse();
        settings.SendMetrics.ShouldBeTrue();
    }

    [Fact]
    public void WritingOneSectionDoesNotDeleteAnother()
    {
        // Two versions of this app on one synced directory is the case that
        // needs it, and the theme is the other section in the same file.
        new Preferences { Theme = "dracula" }.Save(Path_);

        AssistPreferences.Save(Path_, new AssistSettings { Provider = AssistProviderId.DeepSeek });

        Preferences.Load(Path_).Theme.ShouldBe("dracula");
        AssistPreferences.Load(Path_).Provider.ShouldBe(AssistProviderId.DeepSeek);
    }

    [Fact]
    public void APreferenceThisVersionDoesNotKnowSurvivesToo()
    {
        File.WriteAllText(Path_, """{"theme":"dracula","somethingElse":{"a":1}}""");

        AssistPreferences.Save(Path_, new AssistSettings());

        JsonDocument.Parse(File.ReadAllText(Path_)).RootElement
            .TryGetProperty("somethingElse", out _).ShouldBeTrue();
    }

    [Fact]
    public void AnUnreadableSectionFallsBackRatherThanFailingALaunch()
    {
        File.WriteAllText(Path_, """{"assist":"not an object"}""");

        var settings = AssistPreferences.Load(Path_);

        settings.Provider.ShouldBe(AssistProviderId.Claude);
        settings.SendMetrics.ShouldBeTrue();
    }

    [Fact]
    public void TheApiKeyIsNeverInTheFile()
    {
        AssistPreferences.Save(Path_, new AssistSettings());

        // An API key is not a server secret and has no business in a file beside
        // the inventory. It goes to the platform store.
        File.ReadAllText(Path_).ShouldNotContain("key", Case.Insensitive);
    }

    [Fact]
    public void TheSheetSaysWhereEachProvidersDataGoes()
    {
        var sheet = Sheet();

        var providers = sheet.Providers.ToArray();
        providers.Length.ShouldBe(2);
        providers[0].DataGoesTo.ShouldBe("Data goes to Anthropic (United States)");
        providers[1].DataGoesTo.ShouldBe("Data goes to DeepSeek (China)");
        providers[0].IsChosen.ShouldBeTrue();
    }

    [Fact]
    public void ChoosingAProviderSavesItAndClearsTheModel()
    {
        AssistSettings? saved = null;
        var sheet = Sheet(save: settings => saved = settings);

        sheet.ChooseProviderCommand.Execute(sheet.Providers[1]);

        saved.ShouldNotBeNull().Provider.ShouldBe(AssistProviderId.DeepSeek);
        // The model was Claude's; keeping it would send a Claude name to DeepSeek.
        saved.Model.ShouldBeNull();
        sheet.DefaultModel.ShouldBe("deepseek-v4-pro");
        sheet.KnownModels.ShouldContain("deepseek-v4-pro");
    }

    [Fact]
    public void TheTickMovesToTheProviderJustChosen()
    {
        var sheet = Sheet();

        sheet.ChooseProviderCommand.Execute(sheet.Providers[1]);

        // Saving it is not enough: the sheet in front of the user has to agree,
        // and a card still marked chosen is one that cannot be chosen again.
        sheet.Providers[0].IsChosen.ShouldBeFalse();
        sheet.Providers[1].IsChosen.ShouldBeTrue();

        sheet.ChooseProviderCommand.Execute(sheet.Providers[0]);

        sheet.Providers[0].IsChosen.ShouldBeTrue();
        sheet.Providers[1].IsChosen.ShouldBeFalse();
    }

    [Fact]
    public void AKeyGoesToTheStoreUnderTheChosenProvidersOwnAccount()
    {
        var keys = new InMemorySecretStore();
        var sheet = Sheet(keys);

        sheet.NewKey = "sk-ant-something";
        sheet.SaveKeyCommand.Execute(null);

        keys.Secret(AssistProvider.Claude.KeyAccount).ShouldBe("sk-ant-something");
        // Configuring a second must not overwrite the first.
        keys.Secret(AssistProvider.DeepSeek.KeyAccount).ShouldBeNull();
        // Nothing to gain from leaving a key on screen.
        sheet.NewKey.ShouldBeEmpty();
        sheet.Providers[0].HasKey.ShouldBeTrue();
    }

    [Fact]
    public void AKeyCanBeForgotten()
    {
        var keys = new InMemorySecretStore();
        keys.SetSecret(AssistProvider.Claude.KeyAccount, "sk-ant-something");
        var sheet = Sheet(keys);

        sheet.ForgetKeyCommand.Execute(null);

        keys.HasSecret(AssistProvider.Claude.KeyAccount).ShouldBeFalse();
        sheet.Providers[0].HasKey.ShouldBeFalse();
    }

    [Fact]
    public void BothContextSwitchesAreSavedAsTheyAreChanged()
    {
        AssistSettings? saved = null;
        var sheet = Sheet(save: settings => saved = settings);

        sheet.SendTerminalTail = false;

        // Applied as it is changed rather than on Done, as the theme is.
        saved.ShouldNotBeNull().SendTerminalTail.ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyModelFieldMeansTheProvidersDefault()
    {
        AssistSettings? saved = null;
        var sheet = Sheet(save: settings => saved = settings);

        sheet.Model = "claude-released-tomorrow";
        saved.ShouldNotBeNull().ModelName.ShouldBe("claude-released-tomorrow");

        sheet.Model = "   ";
        saved.ShouldNotBeNull().Model.ShouldBeNull();
        saved.ModelName.ShouldBe("claude-opus-5");
    }

    private static SettingsViewModel Sheet(ISecretStore? keys = null, Action<AssistSettings>? save = null) =>
        new(new AppTheme(),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            new AssistSettings(),
            keys ?? new InMemorySecretStore(),
            save);
}
