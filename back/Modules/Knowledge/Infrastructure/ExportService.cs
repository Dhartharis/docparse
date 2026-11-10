using System.Text;
using DocParse.Modules.Knowledge.Domain;
using OfficeOpenXml;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using DocParse.Modules.Knowledge.Application;

namespace DocParse.Modules.Knowledge.Infrastructure;

public class ExportService : IDocumentExporter
{
    public byte[] ToCsv(Document document)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = new CsvWriter(text, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            InjectionOptions = InjectionOptions.Escape,
            InjectionEscapeCharacter = '\'',
            NewLine = "\r\n"
        }))
        {
            void Row(string key, string value)
            {
                csv.WriteField(key);
                csv.WriteField(value);
                csv.NextRecord();
            }
            Row("Field", "Value");
            Row("DocumentType", document.Type);
            Row("CreatedAt", document.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            foreach (var field in document.Fields) Row(field.Key, field.Value);
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    public byte[] ToExcel(Document document)
    {
        ExcelPackage.License.SetNonCommercialPersonal("DocParse");

        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("Document");

        sheet.Cells[1, 1].Value = "Field";
        sheet.Cells[1, 2].Value = "Value";

        sheet.Cells[2, 1].Value = "DocumentType";
        sheet.Cells[2, 2].Value = document.Type;

        sheet.Cells[3, 1].Value = "CreatedAt";
        sheet.Cells[3, 2].Value = document.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

        var row = 4;
        foreach (var field in document.Fields)
        {
            sheet.Cells[row, 1].Value = field.Key;
            sheet.Cells[row, 2].Value = field.Value;
            row++;
        }

        sheet.Cells.AutoFitColumns();

        return package.GetAsByteArray();
    }
}
