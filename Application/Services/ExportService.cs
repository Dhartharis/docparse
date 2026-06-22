using System.Text;
using DocParse.Domain.Models;
using OfficeOpenXml;

namespace DocParse.Application.Services;

public class ExportService
{
    public byte[] ToCsv(Document document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Field,Value");
        sb.AppendLine($"DocumentType,{document.Type}");
        sb.AppendLine($"CreatedAt,{document.CreatedAt:yyyy-MM-dd HH:mm:ss}");

        foreach (var field in document.Fields)
        {
            sb.AppendLine($"{field.Key},{field.Value}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
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