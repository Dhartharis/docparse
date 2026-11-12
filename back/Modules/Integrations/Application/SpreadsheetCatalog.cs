using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocParse.Modules.OperationalData.Contracts;

namespace DocParse.Modules.Integrations.Application;

public sealed class CatalogFailure(string code):Exception("The spreadsheet catalog could not be read.")
{public string Code{get;}=code;}
public sealed record CatalogCell(string Kind,string Value,bool CachedFormula=false);
public sealed record CatalogRow(int Number,IReadOnlyList<CatalogCell> Cells);
public sealed record CatalogTable(string Sheet,IReadOnlyList<string> Sheets,IReadOnlyList<CatalogRow> Rows);
public sealed record CatalogColumnMap(string ExternalId,string Name,string? ProductId,string? Sku,string? Presentation,string? Unit,
    string? Price,string? Currency,string? Available,string? Reserved,string? Active,string? ObservedAt);
public sealed record CatalogDefaults(bool ProductIdFromExternalId,string? Presentation,string? Unit,string? Currency,bool Active,string TaxTreatment);
public sealed record CatalogMapping(int HeaderRow,CatalogColumnMap Columns,CatalogDefaults Defaults,string FreshnessBasis,bool AllowCachedFormulas);
public sealed record CatalogIssue(int Row,string Field,string Code);
public sealed record CatalogPreview(string Sheet,IReadOnlyList<string> Sheets,IReadOnlyList<string> Headers,int RowCount,
    IReadOnlyList<OperationalOffer> Offers,IReadOnlyList<CatalogIssue> Issues,bool CanImport);
public sealed record CatalogInspection(string Sheet,IReadOnlyList<string> Sheets,IReadOnlyList<string> Headers,int RowCount);

public static class SpreadsheetCatalog
{
    public const int MaximumRows=5000,MaximumColumns=128,MaximumCellLength=4096;
    private static readonly CatalogCell Empty=new("empty","");
    private sealed class BadCell(string code):Exception {public string Code{get;}=code;}
    public static CatalogInspection Inspect(CatalogTable table,int headerRow)
    {
        if(headerRow is <1 or >50)throw new CatalogFailure("catalog_headers");
        var header=table.Rows.SingleOrDefault(row=>row.Number==headerRow)??throw new CatalogFailure("catalog_headers");
        var names=header.Cells.Select(cell=>cell.Value.Trim()).ToArray();
        if(header.Cells.Any(cell=>!string.IsNullOrWhiteSpace(cell.Value)&&(cell.Kind!="text"||cell.CachedFormula))||
            names.Length is 0 or >MaximumColumns||names.Where(value=>value.Length>0).Distinct(StringComparer.Ordinal).Count()!=names.Count(value=>value.Length>0)||
            names.Any(value=>value.EnumerateRunes().Count()>128||value.Any(char.IsControl)))throw new CatalogFailure("catalog_headers");
        var count=table.Rows.Count(row=>row.Number>headerRow&&row.Cells.Any(cell=>!string.IsNullOrWhiteSpace(cell.Value)||cell.CachedFormula||cell.Kind=="error"));
        if(count>MaximumRows)throw new CatalogFailure("catalog_limit");
        return new(table.Sheet,table.Sheets,names,count);
    }
    public static CatalogPreview Map(CatalogTable table,CatalogMapping mapping,DateTimeOffset readAt,DateTimeOffset modifiedAt,
        ConnectionSettings settings,IOperationalProjection projection,bool includeAll=false)
    {
        if(mapping is null||mapping.Columns is null||mapping.Defaults is null||mapping.HeaderRow is <1 or >50||mapping.FreshnessBasis is not("column" or "read_at")||
            modifiedAt.Offset!=TimeSpan.Zero||modifiedAt>readAt.AddSeconds(settings.ClockSkewSeconds)||table.Rows.Count>MaximumRows+50)
            throw new CatalogFailure("catalog_mapping");
        settings.Validate(settings.SyncIntervalSeconds is not null);
        var names=Inspect(table,mapping.HeaderRow).Headers.ToArray();
        var columns=mapping.Columns;var defaults=mapping.Defaults;
        var bindings=new[]{columns.ExternalId,columns.Name,columns.ProductId,columns.Sku,columns.Presentation,columns.Unit,columns.Price,columns.Currency,columns.Available,columns.Reserved,columns.Active,columns.ObservedAt};
        if(string.IsNullOrEmpty(columns.ExternalId)||string.IsNullOrEmpty(columns.Name)||bindings.Any(value=>value is not null&&(value.Length==0||!names.Contains(value,StringComparer.Ordinal)))||
            !defaults.ProductIdFromExternalId&&columns.ProductId is null||columns.Presentation is null&&string.IsNullOrWhiteSpace(defaults.Presentation)||
            columns.Unit is null&&string.IsNullOrWhiteSpace(defaults.Unit)||mapping.FreshnessBasis=="column"&&columns.ObservedAt is null&&(columns.Price is not null||columns.Available is not null||columns.Reserved is not null)||
            defaults.TaxTreatment is not("inclusive" or "exclusive" or "unspecified"))throw new CatalogFailure("catalog_mapping");
        var offers=new List<OperationalOffer>();var issues=new List<CatalogIssue>();var ids=new HashSet<string>(StringComparer.Ordinal);var rows=0;
        void Issue(int row,string field,string code){if(issues.Count<100)issues.Add(new(row,field,code));}
        foreach(var row in table.Rows.Where(row=>row.Number>mapping.HeaderRow))
        {
            if(row.Cells.All(cell=>string.IsNullOrWhiteSpace(cell.Value)&&!cell.CachedFormula&&cell.Kind!="error"))continue;
            rows++;
            if(rows>MaximumRows)throw new CatalogFailure("catalog_limit");
            CatalogCell Cell(string? name)=>name is null?Empty:row.Cells.ElementAtOrDefault(Array.IndexOf(names,name))??Empty;
            string? Text(string? name,string? fallback=null)
            {
                var cell=Cell(name);Check(cell,mapping.AllowCachedFormulas);
                return string.IsNullOrWhiteSpace(cell.Value)?fallback:cell.Value.Trim();
            }
            string Identity(string? name)
            {
                var cell=Cell(name);Check(cell,mapping.AllowCachedFormulas);
                if(cell.Kind!="text")throw new BadCell("text_identifier");
                var value=cell.Value.Trim();if(!Regex.IsMatch(value,@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z"))throw new BadCell("identifier");return value;
            }
            string? Decimal(string? name)
            {
                var cell=Cell(name);Check(cell,mapping.AllowCachedFormulas);
                if(string.IsNullOrWhiteSpace(cell.Value))return null;
                var value=cell.Value.Trim();
                if(cell.Kind=="number")
                {
                    value=ExactNumber(value);
                }
                if(!Regex.IsMatch(value,@"\A(0|[1-9][0-9]{0,17})(\.[0-9]{1,6})?\z"))throw new BadCell("decimal");
                if(cell.Kind=="number"&&value.Replace(".","").TrimStart('0').TrimEnd('0').Length>15)throw new BadCell("precision");
                if(cell.Kind is not("text" or "number"))throw new BadCell("decimal");return value;
            }
            var field="externalId";
            try
            {
                var id=Identity(columns.ExternalId);if(!ids.Add(id))throw new BadCell("duplicate_identifier");
                field="productId";var product=defaults.ProductIdFromExternalId?id:Identity(columns.ProductId);
                field="name";var name=Text(columns.Name)??throw new BadCell("required");
                field="sku";var sku=Text(columns.Sku);
                if(sku is not null&&(Cell(columns.Sku).Kind!="text"||sku.EnumerateRunes().Count()>128))throw new BadCell("text_identifier");
                field="presentation";var presentation=Text(columns.Presentation,defaults.Presentation)??throw new BadCell("required");
                field="unit";var unit=Text(columns.Unit,defaults.Unit)??throw new BadCell("required");
                field="price";var amount=Decimal(columns.Price);
                field="available";var available=Decimal(columns.Available);
                field="reserved";var reserved=Decimal(columns.Reserved);
                field="observedAt";var observed=modifiedAt;
                if(amount is not null||available is not null||reserved is not null)
                {
                    if(mapping.FreshnessBasis=="read_at")observed=readAt;
                    else
                    {
                        var text=Text(columns.ObservedAt);
                        if(text is null||!Regex.IsMatch(text,@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?Z\z")||
                            !DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out observed)||observed>readAt.AddSeconds(settings.ClockSkewSeconds))throw new BadCell("utc_timestamp");
                    }
                }
                field="currency";var currency=amount is null?null:Text(columns.Currency,defaults.Currency);
                if(amount is not null&&(currency is null||!Regex.IsMatch(currency,@"\A[A-Z]{3}\z")))throw new BadCell("currency");
                field="active";var active=defaults.Active;
                if(columns.Active is not null)
                {
                    var value=Text(columns.Active);active=value is "1"||string.Equals(value,"true",StringComparison.OrdinalIgnoreCase);
                    if(!active&&value is not "0"&&!string.Equals(value,"false",StringComparison.OrdinalIgnoreCase))throw new BadCell("boolean");
                }
                var price=amount is null?new OperationalPrice(false,"not_provided",null,null,null,null,null):new(true,null,amount,currency,defaults.TaxTreatment,observed,observed.AddSeconds(settings.PriceMaxAgeSeconds));
                var tracking=columns.Available is null&&columns.Reserved is null?"unknown":"tracked";
                var stock=new OperationalAvailability(tracking,new(available is not null,available is null?"not_provided":null,available),new(reserved is not null,reserved is null?"not_provided":null,reserved),observed,observed.AddSeconds(settings.StockMaxAgeSeconds));
                var offer=new OperationalOffer(id,product,sku,name,presentation,unit,"1",modifiedAt,active,price,stock);
                field="offer";projection.Validate(new(offer,null),readAt,settings.ClockSkewSeconds);
                if(includeAll||offers.Count<10)offers.Add(offer);
            }
            catch(BadCell error){Issue(row.Number,field,error.Code);}
            catch(OperationalDataInvalid){Issue(row.Number,field,"invalid_value");}
            catch(ArgumentOutOfRangeException){Issue(row.Number,field,"invalid_value");}
        }
        return new(table.Sheet,table.Sheets,names,rows,offers,issues,issues.Count==0);
    }
    private static void Check(CatalogCell cell,bool allowCached)
    {
        if(cell.Kind=="error")throw new BadCell("cell_error");
        if(cell.CachedFormula&&!allowCached)throw new BadCell("cached_formula");
        if(cell.Value.EnumerateRunes().Count()>MaximumCellLength||cell.Value.Contains('\0'))throw new BadCell("cell_limit");
    }
    private static string ExactNumber(string value)
    {
        if(value.Length>128)throw new BadCell("decimal");
        var match=Regex.Match(value,@"\A(?<sign>[+-]?)(?<integer>[0-9]*)(?:\.(?<fraction>[0-9]*))?(?:[eE](?<exponent>[+-]?[0-9]{1,4}))?\z");
        var integer=match.Groups["integer"].Value;var fraction=match.Groups["fraction"].Value;
        if(!match.Success||integer.Length+fraction.Length==0)throw new BadCell("decimal");
        var digits=(integer+fraction).TrimStart('0');if(digits.Length==0)return "0";
        if(match.Groups["sign"].Value=="-")throw new BadCell("decimal");
        var scale=fraction.Length-(match.Groups["exponent"].Success?int.Parse(match.Groups["exponent"].Value,CultureInfo.InvariantCulture):0);
        while(digits.EndsWith('0')){digits=digits[..^1];scale--;}
        var integerLength=digits.Length-scale;
        if(integerLength>18||scale>6)throw new BadCell("decimal");
        if(digits.Length>15)throw new BadCell("precision");
        return scale<=0?digits+new string('0',-scale):integerLength>0?digits.Insert(integerLength,"."):"0."+new string('0',-integerLength)+digits;
    }
}
