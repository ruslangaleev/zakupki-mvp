using System.Text;
using System.Xml;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace Zakupki.Indexer.Extraction;

public sealed class DocumentTextExtractor(ILogger<DocumentTextExtractor> logger)
{
    private readonly HtmlParser _htmlParser = new();

    public ExtractionAttempt ExtractFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".html" or ".htm" => Success(path, ExtractHtml(File.ReadAllText(path))),
                ".docx" => Success(path, ExtractDocx(path)),
                ".xlsx" => Success(path, ExtractXlsx(path)),
                ".pdf" => ExtractPdf(path),
                ".txt" or ".csv" or ".json" or ".xml" => Success(path, File.ReadAllText(path)),
                _ => Skip(path, $"unsupported extension {extension}")
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract text from {Path}", path);
            return Skip(path, ex.Message);
        }
    }

    public string ExtractHtml(string html)
    {
        var document = _htmlParser.ParseDocument(html);
        return document.Body?.TextContent ?? document.DocumentElement?.TextContent ?? string.Empty;
    }

    private static string ExtractDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        // InnerText concatenates runs without spaces ("БЛ0003966"+"9" -> "БЛ00039669").
        var sb = new StringBuilder();
        foreach (var text in body.Descendants<Text>())
        {
            if (string.IsNullOrEmpty(text.Text))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(text.Text);
        }

        return sb.ToString();
    }

    private static string ExtractXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var sb = new StringBuilder();
        var workbookPart = doc.WorkbookPart;
        if (workbookPart is null)
        {
            return string.Empty;
        }

        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            using var reader = XmlReader.Create(worksheetPart.GetStream());
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                {
                    var value = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        sb.AppendLine(value);
                    }
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "v")
                {
                    var value = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        sb.AppendLine(value);
                    }
                }
            }
        }

        return sb.ToString();
    }

    private ExtractionAttempt ExtractPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(page.Text);
        }

        var text = TextChunker.NormalizeWhitespace(sb.ToString());
        if (text.Length < 40)
        {
            return Skip(path, "pdf has no usable text layer");
        }

        return Success(path, text);
    }

    private static ExtractionAttempt Success(string path, string text) =>
        new(Path.GetFileName(path), TextChunker.NormalizeWhitespace(text), Skipped: false, null);

    private static ExtractionAttempt Skip(string path, string reason) =>
        new(Path.GetFileName(path), string.Empty, Skipped: true, reason);
}

public sealed record ExtractionAttempt(string FileName, string Text, bool Skipped, string? Reason);
