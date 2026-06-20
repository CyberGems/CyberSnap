using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using CyberSnap.Models;

namespace CyberSnap.Services;

public class ProjectData
{
    public List<Annotation> Annotations { get; set; } = new();
    public List<float> HorizontalGuides { get; set; } = new();
    public List<float> VerticalGuides { get; set; } = new();
}

public class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (str != null && str.StartsWith("#"))
            {
                try
                {
                    if (str.Length == 9) // #RRGGBBAA
                    {
                        int r = Convert.ToInt32(str.Substring(1, 2), 16);
                        int g = Convert.ToInt32(str.Substring(3, 2), 16);
                        int b = Convert.ToInt32(str.Substring(5, 2), 16);
                        int a = Convert.ToInt32(str.Substring(7, 2), 16);
                        return Color.FromArgb(a, r, g, b);
                    }
                    else if (str.Length == 7) // #RRGGBB
                    {
                        int r = Convert.ToInt32(str.Substring(1, 2), 16);
                        int g = Convert.ToInt32(str.Substring(3, 2), 16);
                        int b = Convert.ToInt32(str.Substring(5, 2), 16);
                        return Color.FromArgb(255, r, g, b);
                    }
                }
                catch
                {
                    // Fallback to translator if manual parsing fails
                }
                return ColorTranslator.FromHtml(str);
            }
            return Color.FromName(str ?? "Black");
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            return Color.FromArgb(reader.GetInt32());
        }
        return Color.Black;
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
    {
        if (value.IsNamedColor)
        {
            writer.WriteStringValue(value.Name);
        }
        else
        {
            writer.WriteStringValue("#" + value.R.ToString("X2") + value.G.ToString("X2") + value.B.ToString("X2") + value.A.ToString("X2"));
        }
    }
}

public static class CanvasProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new ColorJsonConverter() }
    };

    public static void SaveProject(
        string filePath,
        Bitmap baseBitmap,
        List<Annotation> annotations,
        List<float> horizontalGuides,
        List<float> verticalGuides)
    {
        // Delete file if it already exists to overwrite cleanly
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        // 1. Save base image
        var imageEntry = archive.CreateEntry("background.png");
        using (var entryStream = imageEntry.Open())
        {
            baseBitmap.Save(entryStream, ImageFormat.Png);
        }

        // 2. Save metadata JSON
        var metadataEntry = archive.CreateEntry("project.json");
        using (var entryStream = metadataEntry.Open())
        using (var writer = new StreamWriter(entryStream))
        {
            var data = new ProjectData
            {
                Annotations = annotations,
                HorizontalGuides = horizontalGuides,
                VerticalGuides = verticalGuides
            };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            writer.Write(json);
        }
    }

    public static (Bitmap BaseBitmap, ProjectData Data) LoadProject(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Project file not found", filePath);
        }

        Bitmap baseBitmap;
        ProjectData data;

        using (var archive = ZipFile.OpenRead(filePath))
        {
            // 1. Load base image
            var imageEntry = archive.GetEntry("background.png");
            if (imageEntry == null)
            {
                throw new FileNotFoundException("background.png entry not found in the project archive.");
            }

            using (var entryStream = imageEntry.Open())
            using (var temp = new Bitmap(entryStream))
            {
                // Create a clone to release the file lock on stream
                baseBitmap = new Bitmap(temp);
            }

            // 2. Load JSON data
            var metadataEntry = archive.GetEntry("project.json");
            if (metadataEntry == null)
            {
                throw new FileNotFoundException("project.json entry not found in the project archive.");
            }

            using (var entryStream = metadataEntry.Open())
            using (var reader = new StreamReader(entryStream))
            {
                var json = reader.ReadToEnd();
                data = JsonSerializer.Deserialize<ProjectData>(json, JsonOptions) ?? new ProjectData();
            }
        }

        return (baseBitmap, data);
    }
}
