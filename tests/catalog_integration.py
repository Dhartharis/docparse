"""Catalog mapping acceptance with local XLSX fixtures; no Google credentials or network."""
from datetime import datetime,timedelta,timezone
from http.server import ThreadingHTTPServer
from io import BytesIO
from pathlib import Path
from xml.sax.saxutils import escape
import json
import os
import subprocess
import sys
import threading
import urllib.request
import urllib.error
import uuid
import zipfile
from scheduled_sync_integration import ScheduledProvider

ROOT=Path(__file__).resolve().parents[1]
def workbook(duplicate=False):
    instant=(datetime.now(timezone.utc)-timedelta(seconds=5)).isoformat().replace("+00:00","Z")
    rows=[["ID","Name","Price","Stock","Updated"],["0001","Milk","999999999999999999.123456","0",instant]]
    if duplicate:rows.append(rows[1])
    content=[]
    for index,values in enumerate(rows,1):
        cells=''.join(f'<c r="{chr(65+column)}{index}" t="inlineStr"><is><t>{escape(value)}</t></is></c>'for column,value in enumerate(values))
        content.append(f'<row r="{index}">{cells}</row>')
    output=BytesIO()
    with zipfile.ZipFile(output,"w",zipfile.ZIP_DEFLATED)as archive:
        archive.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>')
        archive.writestr('_rels/.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>')
        archive.writestr('xl/workbook.xml','<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Inventory" sheetId="1" r:id="s1"/></sheets></workbook>')
        archive.writestr('xl/_rels/workbook.xml.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="s1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>')
        archive.writestr('xl/worksheets/sheet1.xml','<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>'+''.join(content)+'</sheetData></worksheet>')
    return output.getvalue()

def exercise(h):
    h["docker"]("restart",h["CONTAINER"]);h["healthy"]()
    expect=h["expect"];root=f"/api/organizations/{h['org']}/spreadsheet-catalog";a,b=h["a"],h["b"]
    def upload(path,data,token=None,fields=None,filename="catalog.xlsx"):
        boundary="fixture"+uuid.uuid4().hex;body=bytearray()
        for name,value in (fields or {}).items():body.extend((f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n').encode())
        body.extend((f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\nContent-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet\r\n\r\n').encode()+data+f'\r\n--{boundary}--\r\n'.encode())
        headers={"Content-Type":"multipart/form-data; boundary="+boundary}
        if token:headers["Authorization"]="Bearer "+token
        try:response=urllib.request.urlopen(urllib.request.Request(h["BASE"]+path,data=body,headers=headers),timeout=15)
        except urllib.error.HTTPError as error:response=error
        with response:
            raw=response.read();return response.status,json.loads(raw)if raw else None
    data=workbook();expect(401,upload(root+"/inspect",data));expect(404,upload(root+"/inspect",data,b))
    found=expect(200,upload(root+"/inspect",data,a,{"headerRow":"1"}));assert found["headers"]==["ID","Name","Price","Stock","Updated"] and found["rowCount"]==1
    mapping={"headerRow":1,"columns":{"externalId":"ID","name":"Name","productId":None,"sku":None,"presentation":None,"unit":None,"price":"Price","currency":None,"available":"Stock","reserved":None,"active":None,"observedAt":"Updated"},
        "defaults":{"productIdFromExternalId":True,"presentation":"One carton","unit":"L","currency":"USD","active":True,"taxTreatment":"inclusive"},"freshnessBasis":"column","allowCachedFormulas":False}
    before=h["sql"]("SELECT count(*) FROM operational_data.records")
    result=expect(200,upload(root+"/preview",data,a,{"mapping":json.dumps(mapping)}));assert result["canImport"] and result["offers"][0]["externalId"]=="0001" and result["offers"][0]["price"]["amount"]=="999999999999999999.123456"
    duplicate=expect(200,upload(root+"/preview",workbook(True),a,{"mapping":json.dumps(mapping)}));assert not duplicate["canImport"] and duplicate["issues"][0]["code"]=="duplicate_identifier"
    expect(400,upload(root+"/inspect",data,a,{"sheet":"Missing"}));expect(400,upload(root+"/inspect",b"bad",a));expect(400,upload(root+"/inspect",data,a,filename="macro.xlsm"))
    expect(400,upload(root+"/preview",data,a,{"mapping":"{}"}));expect(400,upload(root+"/preview",data,a,{"mapping":"invalid-json"}))
    assert h["sql"]("SELECT count(*) FROM operational_data.records")==before
    if "--browser"in sys.argv:
        fixture=ROOT/".local"/("catalog-fixture-"+h["SUFFIX"]+".xlsx");fixture.parent.mkdir(parents=True,exist_ok=True);fixture.write_bytes(data)
        env=dict(os.environ,VITE_API_PROXY_TARGET=h["BASE"],DOCPARSE_REAL_API=h["BASE"],DOCPARSE_TEST_ADMIN_EMAIL="push-a",DOCPARSE_TEST_ADMIN_PASSWORD=h["PASSWORD"],DOCPARSE_CATALOG_FIXTURE=str(fixture))
        try:subprocess.run(["npm.cmd"if os.name=="nt"else"npm","--prefix","front","test","--","tests/real-catalog.spec.ts","--workers=1","--output="+str(ROOT/".local"/"catalog-qa"/h["SUFFIX"])],cwd=ROOT,env=env,check=True)
        finally:fixture.unlink(missing_ok=True)
    print("PASS: authenticated XLSX inspection/mapping, preserved IDs/exact decimals, duplicate/error reporting, missing sheets, malformed/macro rejection, tenant/Viewer denial and no catalog writes.")

if __name__=="__main__":
    server=ThreadingHTTPServer(("0.0.0.0",0),ScheduledProvider);threading.Thread(target=server.serve_forever,daemon=True).start()
    env=dict(os.environ,DOCPARSE_LIVE_PROVIDER_ORIGIN=f"http://host.docker.internal:{server.server_port}")
    try:subprocess.run([sys.executable,"tests/push_integration.py","--live","--scheduled","--catalog",*sys.argv[1:]],cwd=ROOT,env=env,check=True)
    finally:server.shutdown();server.server_close()
