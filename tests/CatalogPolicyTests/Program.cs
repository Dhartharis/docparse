using System.Text;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Infrastructure;
using DocParse.Modules.OperationalData.Infrastructure;
using Microsoft.Extensions.Configuration;

var count=0;void Check(bool value){if(!value)throw new Exception("Catalog assertion failed.");count++;}
void Reject(Action action,string? code=null){try{action();}catch(CatalogFailure error)when(code is null||error.Code==code){count++;return;}throw new Exception("Expected catalog rejection.");}
var now=DateTimeOffset.Parse("2026-11-12T12:00:00Z");var projection=new OperationalProjection(new ConfigurationBuilder().Build());
var settings=new ConnectionSettings("Sheet",600,60,0,null);
var mapping=new CatalogMapping(1,new("ID","Name",null,null,null,null,"Price",null,"Stock",null,null,"Updated"),new(true,"One carton","L","USD",true,"inclusive"),"column",false);
var json="""{"range":"Inventory!A1:E2","majorDimension":"ROWS","values":[["ID","Name","Price","Stock","Updated"],["0001","Milk","999999999999999999.123456",0,"2026-11-12T11:59:30Z"]]}""";
CatalogTable Table(string value)=>SpreadsheetReader.Sheets(Encoding.UTF8.GetBytes(value),"Inventory",["Inventory"]);
CatalogPreview Preview(string value,CatalogMapping? map=null)=>SpreadsheetCatalog.Map(Table(value),map??mapping,now,now.AddMinutes(-1),settings,projection);
var valid=Preview(json);Check(valid.CanImport&&valid.Offers[0].ExternalId=="0001"&&valid.Offers[0].ProductId=="0001");
Check(valid.Offers[0].Price.Amount=="999999999999999999.123456"&&valid.Offers[0].Availability.Available.Quantity=="0");
Check(valid.Offers[0].Availability.Reserved is {Known:false,Reason:"not_provided"});
Check(valid.Offers[0].Price.ObservedAt==now.AddSeconds(-30));
Check(Preview(json,mapping with{FreshnessBasis="read_at"}).Offers[0].Price.ObservedAt==now);
Check(!Preview(json.Replace("\"0001\"","1")).CanImport);
Check(!Preview(json.Replace("\"999999999999999999.123456\"","999999999999999999")).CanImport);
Check(!Preview(json.Replace("\"999999999999999999.123456\"","\"12,50\"")).CanImport);
Check(!Preview(json.Replace("\"999999999999999999.123456\"","-1")).CanImport);
Check(!Preview(json.Replace("\"2026-11-12T11:59:30Z\"","\"12/11/2026\"")).CanImport);
Check(!Preview(json.Replace("\"2026-11-12T11:59:30Z\"","\"2026-11-12T12:00:01Z\"")).CanImport);
Check(!Preview(json.Replace("\"2026-11-12T11:59:30Z\"","\"2026-02-30T00:00:00Z\"")).CanImport);
Check(Preview(json.Replace("\"999999999999999999.123456\"","1.25e2")).Offers[0].Price.Amount=="125");
Check(!Preview(json.Replace("\"999999999999999999.123456\"","1e-100")).CanImport);
Check(!Preview(json.Replace("\"999999999999999999.123456\"","1e-7")).CanImport);
Check(Preview(json.Replace("\"999999999999999999.123456\"","1e-6")).Offers[0].Price.Amount=="0.000001");
var emptyPrice=Preview(json.Replace("\"999999999999999999.123456\"","\"\""));Check(emptyPrice.CanImport&&!emptyPrice.Offers[0].Price.Known);
Reject(()=>Preview(json,mapping with{Columns=mapping.Columns with{Name="Absent"}}),"catalog_mapping");
Reject(()=>Preview(json.Replace("\"Stock\",\"Updated\"","\"Name\",\"Updated\"")),"catalog_headers");
Reject(()=>Table(json.Replace("\"values\":","\"values\":[],\"values\":")));
Reject(()=>SpreadsheetReader.Sheets([0xff],"Inventory",["Inventory"]));
var duplicate=Table(json);duplicate=duplicate with{Rows=[..duplicate.Rows,new CatalogRow(3,duplicate.Rows[1].Cells)]};
Check(!SpreadsheetCatalog.Map(duplicate,mapping,now,now.AddMinutes(-1),settings,projection).CanImport);
var formula=Table(json);formula=formula with{Rows=[formula.Rows[0],formula.Rows[1] with{Cells=formula.Rows[1].Cells.Select((cell,i)=>i==2?cell with{CachedFormula=true}:cell).ToArray()}]};
Check(!SpreadsheetCatalog.Map(formula,mapping,now,now.AddMinutes(-1),settings,projection).CanImport);
Check(SpreadsheetCatalog.Map(formula,mapping with{AllowCachedFormulas=true},now,now.AddMinutes(-1),settings,projection).CanImport);
var blankFormula=Table(json);blankFormula=blankFormula with{Rows=[blankFormula.Rows[0],new CatalogRow(3,[new("empty","",true)])]};
Check(!SpreadsheetCatalog.Map(blankFormula,mapping,now,now.AddMinutes(-1),settings,projection).CanImport);
byte[] Workbook(bool cached=false,bool phonetic=false)
{
    using var stream=new MemoryStream();
    using(var document=SpreadsheetDocument.Create(stream,SpreadsheetDocumentType.Workbook,true))
    {
        var book=document.AddWorkbookPart();book.Workbook=new Workbook();var part=book.AddNewPart<WorksheetPart>();
        var rows=new SheetData();
        Cell Text(string reference,string value)=>new(){CellReference=reference,DataType=CellValues.InlineString,InlineString=new InlineString(new Text(value))};
        rows.Append(new Row(Text("A1","ID"),Text("B1","Name"),Text("C1","Price"),Text("D1","Stock"),Text("E1","Updated")){RowIndex=1});
        var price=Text("C2","999999999999999999.123456");if(cached)price=new Cell(new CellFormula("1+1"),new CellValue("2")){CellReference="C2",DataType=CellValues.Number};
        var identity=Text("A2","0001");if(phonetic)identity.InlineString!.Append(new PhoneticRun(new Text("annotation")){BaseTextStartIndex=0,EndingBaseIndex=4});
        rows.Append(new Row(identity,Text("B2","Milk"),price,new Cell(new CellValue("0")){CellReference="D2",DataType=CellValues.Number},Text("E2","2026-11-12T11:59:30Z")){RowIndex=2});
        part.Worksheet=new Worksheet(rows);book.Workbook.Append(new Sheets(new Sheet(){Name="Inventory",SheetId=1,Id=book.GetIdOfPart(part)}));book.Workbook.Save();
    }
    return stream.ToArray();
}
var xlsx=SpreadsheetReader.Xlsx(Workbook());Check(xlsx.Sheet=="Inventory"&&xlsx.Rows.Count==2);
var excel=SpreadsheetCatalog.Map(xlsx,mapping,now,now.AddMinutes(-1),settings,projection);Check(excel.CanImport&&excel.Offers[0].ExternalId=="0001"&&excel.Offers[0].Price.Amount=="999999999999999999.123456");
Check(!SpreadsheetCatalog.Map(SpreadsheetReader.Xlsx(Workbook(true)),mapping,now,now.AddMinutes(-1),settings,projection).CanImport);
Check(SpreadsheetCatalog.Map(SpreadsheetReader.Xlsx(Workbook(phonetic:true)),mapping,now,now.AddMinutes(-1),settings,projection).Offers[0].ExternalId=="0001");
Reject(()=>SpreadsheetReader.Xlsx(Workbook(),"Other"));Reject(()=>SpreadsheetReader.Xlsx([1,2,3,4]));
using(var stream=new MemoryStream())
{
    using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true)){foreach(var name in new[]{"duplicate.xml","duplicate.xml"}){using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write("<x/>");}}
    Reject(()=>SpreadsheetReader.Xlsx(stream.ToArray()),"catalog_limit");
}
Console.WriteLine($"PASS: {count} spreadsheet catalog checks (Sheets/XLSX, exact values, identities, dates, unknowns, duplicate rows/headers, formulas, invalid archives and mapping bounds).");
