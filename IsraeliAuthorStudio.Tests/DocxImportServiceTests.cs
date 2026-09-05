using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using IsraeliAuthorStudio.Services;

namespace IsraeliAuthorStudio.Tests;

public sealed class DocxImportServiceTests
{
    [Fact]
    public async Task HeadingsAndSeparatorsCreateChaptersAndScenes()
    {
        await using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                Heading("פרק ראשון", "Heading1"),
                Paragraph("פתיחת הסיפור"),
                Paragraph("***"),
                Paragraph("המשך הפרק"),
                Heading("פרק שני", "Heading1"),
                Heading("סצנה בשם", "Heading2"),
                Paragraph("טקסט נוסף")));
        }
        stream.Position = 0;

        var result = await new DocxImportService().ExtractDocumentAsync(stream, "ספר");

        Assert.Equal(2, result.Chapters.Count);
        Assert.Equal(3, result.SceneCount);
        Assert.Equal("פרק ראשון", result.Chapters[0].Name);
        Assert.Equal("סצנה בשם", result.Chapters[1].Scenes[0].Title);
    }

    private static Paragraph Paragraph(string text) => new(new Run(new Text(text)));

    private static Paragraph Heading(string text, string styleId) => new(
        new ParagraphProperties(new ParagraphStyleId { Val = styleId }),
        new Run(new Text(text)));
}
