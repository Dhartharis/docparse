using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocParse.Modules.Integrations.Application;

namespace DocParse.Modules.Integrations.Infrastructure;

public static class SpreadsheetReader
{
    public const int MaximumFileBytes=10*1024*1024,MaximumJsonBytes=16*1024*1024;
    private static string CellText(OpenXmlCompositeElement value) => string.Concat(value.ChildElements.Select(element => element is Text text ? text.Text : element is Run run ? run.Text?.Text ?? "" : ""));
    private static CatalogFailure Invalid()=>new("catalog_file");
    private static void CheckText(string value)
    {if(value.EnumerateRunes().Count()>SpreadsheetCatalog.MaximumCellLength||value.Contains('\0'))throw new CatalogFailure("catalog_limit");}
    public static CatalogTable Sheets(byte[] bytes,string sheet,IReadOnlyList<string> sheets)
    {
        if(bytes.Length>MaximumJsonBytes)throw new CatalogFailure("catalog_limit");
        if(!sheets.Contains(sheet,StringComparer.Ordinal))throw Invalid();
        try
        {
            _=new UTF8Encoding(false,true).GetString(bytes);using var document=JsonDocument.Parse(bytes);var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)throw Invalid();
            var properties=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in root.EnumerateObject())if(!properties.Add(property.Name)||property.Name is not("range" or "majorDimension" or "values"))throw Invalid();
            if(root.TryGetProperty("majorDimension",out var dimension)&&(dimension.ValueKind!=JsonValueKind.String||dimension.GetString()!="ROWS"))throw Invalid();
            if(!root.TryGetProperty("values",out var values))return new(sheet,sheets,[]);
            if(values.ValueKind!=JsonValueKind.Array||values.GetArrayLength()>SpreadsheetCatalog.MaximumRows+50)throw new CatalogFailure("catalog_limit");
            var rows=new List<CatalogRow>();var number=0;long characters=0;
            foreach(var row in values.EnumerateArray())
            {
                number++;if(row.ValueKind!=JsonValueKind.Array||row.GetArrayLength()>SpreadsheetCatalog.MaximumColumns)throw new CatalogFailure("catalog_limit");
                var cells=new List<CatalogCell>();
                foreach(var cell in row.EnumerateArray())
                {
                    var parsed=cell.ValueKind switch {JsonValueKind.String=>new CatalogCell("text",cell.GetString()!),JsonValueKind.Number=>new("number",cell.GetRawText()),
                        JsonValueKind.True=>new("boolean","true"),JsonValueKind.False=>new("boolean","false"),JsonValueKind.Null=>new("empty",""),_=>throw Invalid()};
                    CheckText(parsed.Value);characters+=parsed.Value.Length;if(characters>8*1024*1024)throw new CatalogFailure("catalog_limit");cells.Add(parsed);
                }
                rows.Add(new(number,cells));
            }
            return new(sheet,sheets,rows);
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or DecoderFallbackException){throw Invalid();}
    }
    public static CatalogTable Xlsx(byte[] bytes,string? selectedSheet=null,CancellationToken ct=default)
    {
        if(bytes.Length==0||bytes.Length>MaximumFileBytes)throw new CatalogFailure("catalog_limit");
        try
        {
            using(var archive=new ZipArchive(new MemoryStream(bytes,false),ZipArchiveMode.Read))
            {
                if(archive.Entries.Count>512||archive.Entries.Sum(entry=>entry.Length)>64L*1024*1024||archive.Entries.Any(entry=>entry.Length>32L*1024*1024||entry.FullName.Contains("..")||entry.FullName.Contains('\\'))||
                    archive.Entries.Select(entry=>entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=archive.Entries.Count)throw new CatalogFailure("catalog_limit");
            }
            using var stream=new MemoryStream(bytes,false);
            using var document=SpreadsheetDocument.Open(stream,false,new OpenSettings{AutoSave=false,MaxCharactersInPart=32L*1024*1024});
            var workbook=document.WorkbookPart??throw Invalid();
            if(document.DocumentType!=SpreadsheetDocumentType.Workbook||workbook.VbaProjectPart is not null)throw Invalid();
            var book=workbook.Workbook??throw Invalid();var sheets=book.Sheets?.Elements<Sheet>().ToArray()??[];
            if(sheets.Length is 0 or >100||sheets.Any(sheet=>sheet.Name?.Value is not {Length:>0 and <=128})||sheets.Select(sheet=>sheet.Name!.Value!).Distinct(StringComparer.Ordinal).Count()!=sheets.Length)throw Invalid();
            var selected=selectedSheet is null?sheets.FirstOrDefault(sheet=>sheet.State is null||sheet.State.Value==SheetStateValues.Visible)??sheets[0]:sheets.SingleOrDefault(sheet=>sheet.Name?.Value==selectedSheet);
            if(selected?.Id?.Value is not {} relation||workbook.GetPartById(relation) is not WorksheetPart worksheet)throw Invalid();
            var shared=new List<string>();long characters=0;
            if(workbook.SharedStringTablePart is {} sharedPart)
            {
                using var reader=OpenXmlReader.Create(sharedPart);
                while(reader.Read())
                {
                    ct.ThrowIfCancellationRequested();if(!reader.IsStartElement||reader.ElementType!=typeof(SharedStringItem))continue;
                    var value=CellText(reader.LoadCurrentElement() as SharedStringItem??throw Invalid());CheckText(value);characters+=value.Length;
                    if(shared.Count>=100000||characters>8*1024*1024)throw new CatalogFailure("catalog_limit");shared.Add(value);
                }
            }
            var rows=new List<CatalogRow>();var rowNumbers=new HashSet<int>();
            using(var reader=OpenXmlReader.Create(worksheet))
            while(reader.Read())
            {
                ct.ThrowIfCancellationRequested();if(!reader.IsStartElement||reader.ElementType!=typeof(Row))continue;
                var row=reader.LoadCurrentElement() as Row??throw Invalid();var cells=new SortedDictionary<int,CatalogCell>();
                if(row.RowIndex?.Value is not {} rowIndex||rowIndex is 0 or >1048576||!rowNumbers.Add((int)rowIndex))throw Invalid();
                foreach(var cell in row.Elements<Cell>())
                {
                    var reference=cell.CellReference?.Value??throw Invalid();var column=0;var position=0;
                    while(position<reference.Length&&reference[position] is >= 'A' and <= 'Z')column=checked(column*26+(reference[position++]-'A'+1));
                    if(column<1||column>SpreadsheetCatalog.MaximumColumns)throw new CatalogFailure("catalog_limit");
                    if(!uint.TryParse(reference[position..],NumberStyles.None,CultureInfo.InvariantCulture,out var suffix)||suffix!=rowIndex||cells.ContainsKey(column))throw Invalid();
                    var value=cell.CellValue?.Text??"";var kind="number";
                    if(cell.DataType?.Value==CellValues.SharedString)
                    {if(!int.TryParse(value,out var index)||index<0||index>=shared.Count)throw Invalid();value=shared[index];kind="text";}
                    else if(cell.DataType?.Value==CellValues.InlineString){value=cell.InlineString is {} inline?CellText(inline):"";kind="text";}
                    else if(cell.DataType?.Value==CellValues.String||cell.DataType?.Value==CellValues.Date)kind="text";
                    else if(cell.DataType?.Value==CellValues.Boolean){kind="boolean";if(value is not("0" or "1"))throw Invalid();value=value=="1"?"true":"false";}
                    else if(cell.DataType?.Value==CellValues.Error)kind="error";
                    else if(cell.DataType?.Value is {} type&&type!=CellValues.Number)throw Invalid();
                    CheckText(value);characters+=value.Length;if(characters>16*1024*1024)throw new CatalogFailure("catalog_limit");
                    cells.Add(column,new(value.Length==0?"empty":kind,value,cell.CellFormula is not null));
                }
                if(cells.Count==0||cells.Values.All(cell=>string.IsNullOrWhiteSpace(cell.Value)&&!cell.CachedFormula))continue;
                if(rows.Count>=SpreadsheetCatalog.MaximumRows+50)throw new CatalogFailure("catalog_limit");
                var array=Enumerable.Repeat(new CatalogCell("empty",""),cells.Keys.Max()).ToArray();foreach(var(column,value)in cells)array[column-1]=value;
                rows.Add(new((int)rowIndex,array));
            }
            return new(selected.Name!.Value!,sheets.Select(sheet=>sheet.Name!.Value!).ToArray(),rows);
        }
        catch(Exception error)when(error is InvalidDataException or IOException or OpenXmlPackageException or FormatException or OverflowException or System.Xml.XmlException or ArgumentException or KeyNotFoundException)
        {throw Invalid();}
    }
}
