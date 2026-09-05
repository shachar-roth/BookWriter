using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using IsraeliAuthorStudio.Models;

namespace IsraeliAuthorStudio.Services;

public sealed class DocxImportService
{
    private const int PreferredSceneLength = 12_000;

    public async Task<DocxImportDocument> ExtractDocumentAsync(Stream docxStream, string sourceName)
    {
        await using var memory = new MemoryStream();
        await docxStream.CopyToAsync(memory);
        memory.Position = 0;

        using var document = WordprocessingDocument.Open(memory, false);
        var body = document.MainDocumentPart?.Document?.Body;
        var result = new DocxImportDocument { SourceName = sourceName };
        if (body is null)
        {
            return result;
        }

        var defaultChapterName = string.IsNullOrWhiteSpace(sourceName) ? "פרק מיובא" : sourceName.Trim();
        var currentChapter = new DocxImportChapter { Name = defaultChapterName };
        result.Chapters.Add(currentChapter);
        var sceneTitle = "";
        var scene = new StringBuilder();

        void FlushScene()
        {
            var content = scene.ToString().Trim();
            if (content.Length == 0)
            {
                scene.Clear();
                return;
            }

            currentChapter.Scenes.Add(new DocxImportScene
            {
                Title = string.IsNullOrWhiteSpace(sceneTitle) ? "סצנה מיובאת" : sceneTitle,
                Content = content
            });
            scene.Clear();
            sceneTitle = "";
        }

        foreach (var paragraph in body.Elements<Paragraph>())
        {
            var text = ReadParagraphText(paragraph).Trim();
            var headingLevel = GetHeadingLevel(paragraph);

            if (headingLevel == 1 && text.Length > 0)
            {
                FlushScene();
                if (currentChapter.Scenes.Count == 0 && result.Chapters.Count == 1)
                {
                    currentChapter.Name = text;
                }
                else
                {
                    currentChapter = new DocxImportChapter { Name = text };
                    result.Chapters.Add(currentChapter);
                }
                continue;
            }

            if (headingLevel >= 2 && text.Length > 0)
            {
                FlushScene();
                sceneTitle = text;
                continue;
            }

            if (IsSceneSeparator(text))
            {
                FlushScene();
                continue;
            }

            if (text.Length == 0)
            {
                if (scene.Length > 0 && !scene.ToString().EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal))
                {
                    scene.AppendLine();
                }
                continue;
            }

            if (scene.Length > 0)
            {
                scene.AppendLine().AppendLine();
            }
            scene.Append(text);

            if (scene.Length >= PreferredSceneLength)
            {
                FlushScene();
            }
        }

        FlushScene();
        result.Chapters.RemoveAll(chapter => chapter.Scenes.Count == 0);
        return result;
    }

    public async Task<string> ExtractTextAsync(Stream docxStream)
    {
        var document = await ExtractDocumentAsync(docxStream, "ייבוא DOCX");
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            document.Chapters.SelectMany(chapter => chapter.Scenes).Select(scene => scene.Content));
    }

    private static int GetHeadingLevel(Paragraph paragraph)
    {
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrWhiteSpace(style) && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = style["Heading".Length..];
            if (int.TryParse(suffix, out var level))
            {
                return level;
            }
        }

        var outlineLevel = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        return outlineLevel is null ? 0 : outlineLevel.Value + 1;
    }

    private static bool IsSceneSeparator(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        return compact is "***" or "---" or "###" or "•••" or "*#*";
    }

    private static string ReadParagraphText(Paragraph paragraph)
    {
        var builder = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case Text text:
                    builder.Append(text.Text);
                    break;
                case TabChar:
                    builder.Append('\t');
                    break;
                case Break:
                    builder.AppendLine();
                    break;
            }
        }

        return builder.ToString();
    }
}
