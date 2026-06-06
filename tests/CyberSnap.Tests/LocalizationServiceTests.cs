using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CyberSnap.Services;
using Xunit;

namespace CyberSnap.Tests;

public sealed class LocalizationServiceTests
{
    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("auto", "auto")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "fr")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("de-DE", "de")]
    [InlineData("zz", "auto")]
    public void NormalizeLanguageSetting_ReturnsSupportedSetting(string? input, string expected)
    {
        Assert.Equal(expected, LocalizationService.NormalizeLanguageSetting(input));
    }

    [Fact]
    public void ResolveLanguageCode_UsesSystemCultureForAuto()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");

        Assert.Equal("fr", LocalizationService.ResolveLanguageCode("auto", culture));
    }

    [Fact]
    public void ResolveLanguageCode_UsesSupportedSystemCultureForAuto()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");

        Assert.Equal("fr", LocalizationService.ResolveLanguageCode("auto", culture));
    }

    [Fact]
    public void Translate_LoadsFrenchJsonDictionary()
    {
        Assert.Equal("Général", LocalizationService.Translate("fr", "General"));
        Assert.Equal("Center Select", LocalizationService.Translate("fr", "Center Select"));
        Assert.Equal("Center aspect ratio", LocalizationService.Translate("fr", "Center aspect ratio"));
        Assert.Equal("Untranslated", LocalizationService.Translate("fr", "Untranslated"));
    }

    [Fact]
    public void ResolveContentLanguageCode_UsesSystemCultureWhenInterfaceLanguageIsAuto()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");

        Assert.Equal("fr-FR", LocalizationService.ResolveContentLanguageCode("auto", culture));
    }

    [Fact]
    public void ResolveContentLanguageCode_UsesSpecificInterfaceLanguageWhenSelected()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");

        Assert.Equal("fr", LocalizationService.ResolveContentLanguageCode("fr", culture));
    }

    [Fact]
    public void BuiltInLanguages_HaveInterfaceTranslationFiles()
    {
        foreach (var language in LocalizationService.Languages)
            Assert.True(LocalizationService.HasInterfaceTranslations(language.Code), $"Missing {language.Code}.json");
    }

    [Fact]
    public void BuiltInLocalizationFiles_MatchEnglishKeySet()
    {
        var localizationDir = Path.Combine(AppContext.BaseDirectory, "Localization");
        var english = ReadLocalizationFile(Path.Combine(localizationDir, "en.json"));

        foreach (var file in Directory.EnumerateFiles(localizationDir, "*.json"))
        {
            var current = ReadLocalizationFile(file);
            var missing = english.Keys.Where(key => !current.ContainsKey(key)).ToArray();
            var empty = current.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToArray();

            Assert.True(missing.Length == 0, $"{Path.GetFileName(file)} missing: {string.Join(", ", missing)}");
            Assert.True(empty.Length == 0, $"{Path.GetFileName(file)} has empty values: {string.Join(", ", empty)}");
        }
    }

    [Fact]
    public void ApplyTo_CanTranslateTextBlockInlinesRepeatedly()
    {
        Exception? thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                var textBlock = new TextBlock();
                textBlock.Inlines.Add(new Run("General"));
                textBlock.Inlines.Add(new Run(" â€” "));
                textBlock.Inlines.Add(new Run("Language"));

                LocalizationService.ApplyTo(textBlock, "fr");
                LocalizationService.ApplyTo(textBlock, "en");
                LocalizationService.ApplyTo(textBlock, "fr");
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(thrown);
    }

    [Fact]
    public void ApplyTo_DoesNotTranslateOrFlipIconFontTextBlocks()
    {
        Exception? thrown = null;
        string? text = null;
        FlowDirection? flowDirection = null;

        var thread = new Thread(() =>
        {
            try
            {
                var icon = new TextBlock
                {
                    Text = "\uE713",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
                };

                LocalizationService.ApplyTo(icon, "fr");
                text = icon.Text;
                flowDirection = icon.FlowDirection;
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(thrown);
        Assert.Equal("\uE713", text);
        Assert.Equal(FlowDirection.LeftToRight, flowDirection);
    }

    [Fact]
    public void ApplyTo_DoesNotTranslatePrivateUseGlyphs()
    {
        Exception? thrown = null;
        string? text = null;

        var thread = new Thread(() =>
        {
            try
            {
                var icon = new TextBlock { Text = "\uE930" };

                LocalizationService.ApplyTo(icon, "fr");
                text = icon.Text;
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(thrown);
        Assert.Equal("\uE930", text);
    }

    private static Dictionary<string, string> ReadLocalizationFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var duplicates = document.RootElement.EnumerateObject()
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(property => property.Name)))
            .ToArray();

        if (duplicates.Length > 0)
            throw new InvalidOperationException($"{Path.GetFileName(path)} has duplicate localization keys: {string.Join("; ", duplicates)}");

        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.Ordinal);
    }
}
