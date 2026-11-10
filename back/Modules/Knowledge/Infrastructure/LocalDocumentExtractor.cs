using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocParse.Modules.Knowledge.Application;
using DocParse.Modules.Knowledge.Contracts;

namespace DocParse.Modules.Knowledge.Infrastructure;

internal sealed class LocalDocumentExtractor : IDocumentExtractor
{
    public async Task<string> ExtractDocumentDataAsync(string base64File, string mimeType, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "docparse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, mimeType == "application/pdf" ? "input.pdf" : "input.image");
            await File.WriteAllBytesAsync(input, Convert.FromBase64String(base64File), cancellationToken);
            var pages = new List<DocumentPage>();
            if (mimeType == "application/pdf")
            {
                var output = Path.Combine(directory, "text.txt");
                await Run("pdftotext", ["-layout", "-enc", "UTF-8", "-f", "1", "-l", "201", input, output], cancellationToken);
                var raw = await Read(output, cancellationToken);
                var parts = raw.Split('\f');
                if (parts.Length > 201 || (parts.Length == 201 && !string.IsNullOrWhiteSpace(parts[^1]))) throw new DocumentUploadRejected("Maximum PDF length is 200 pages.");
                var count = parts.Length > 1 && string.IsNullOrWhiteSpace(parts[^1]) ? parts.Length - 1 : parts.Length;
                var ocrPages = 0;
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = parts[index].Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        if (++ocrPages > 20) throw new DocumentUploadRejected("Maximum scanned PDF length is 20 pages.");
                        var prefix = Path.Combine(directory, "page");
                        await Run("pdftoppm", ["-f", (index + 1).ToString(), "-l", (index + 1).ToString(), "-scale-to", "1800", "-singlefile", "-png", input, prefix], cancellationToken);
                        text = await Ocr(prefix + ".png", directory, cancellationToken);
                    }
                    if (!string.IsNullOrWhiteSpace(text)) pages.Add(new DocumentPage(index + 1, text));
                }
            }
            else pages.Add(new DocumentPage(1, await Ocr(input, directory, cancellationToken)));
            if (pages.Sum(page => page.Text.Length) > 1000000) throw new DocumentUploadRejected("Maximum extracted text length exceeded.");
            if (pages.All(page => string.IsNullOrWhiteSpace(page.Text))) throw new DocumentUploadRejected("No readable text was found.");
            return JsonSerializer.Serialize(new { type = "Document", fields = new { text = string.Join("\n\n", pages.Select(page => $"[Page {page.Number}]\n{page.Text}")) }, pages = pages.Select(page => new { number = page.Number, text = page.Text }) });
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task<string> Ocr(string input, string directory, CancellationToken ct)
    {
        var output = Path.Combine(directory, "ocr");
        await Run("tesseract", [input, output, "-l", "eng+spa"], ct);
        return (await Read(output + ".txt", ct)).Trim();
    }
    private static async Task<string> Read(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new DocumentUploadRejected("Maximum extracted text length exceeded.");
        return await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }
    private static async Task Run(string executable, string[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new DocumentProcessingUnavailable(); }
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await stderr; }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); throw; }
        if (process.ExitCode != 0) throw new DocumentUploadRejected("The document could not be read. Check its format and whether it is password protected.");
    }
}
