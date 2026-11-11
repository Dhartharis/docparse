"""Local signed provider double plus the disposable Push harness; no external provider traffic."""
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hashlib
import hmac
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import urllib.request
import uuid

SECRET = "b"*64
ROOT = Path(__file__).resolve().parents[1]

class Provider(BaseHTTPRequestHandler):
    calls = []
    nonces = set()
    release = threading.Event()
    def log_message(self,*args): pass
    def do_GET(self):
        if self.path == "/release": type(self).release.set()
        body=json.dumps(type(self).calls).encode();self.send_response(200);self.end_headers();self.wfile.write(body)
    def do_POST(self):
        raw=self.rfile.read(int(self.headers.get("Content-Length","0")))
        stamp=self.headers.get("X-Fyr-Timestamp","");nonce=self.headers.get("X-Fyr-Request-Id","");key=self.headers.get("X-Fyr-Key-Id","")
        canonical="\n".join(["FYR-OPDATA-V1","POST",self.path,key,stamp,nonce,hashlib.sha256(raw).hexdigest()])
        expected="v1="+hmac.new(bytes.fromhex(SECRET),canonical.encode(),hashlib.sha256).hexdigest()
        assert key=="provider-key" and self.headers.get("X-Fyr-Signature")==expected and abs(int(stamp)-int(time.time()))<=300
        assert nonce not in type(self).nonces;type(self).nonces.add(nonce)
        assert self.path=="/prefix/integrations/v1/operational-data/query"
        request=json.loads(raw);assert request["sourceBusinessId"]=="business-demo"
        kind,values=next(iter(request["selector"].items())); values=[values] if kind=="text" else values
        mode=values[0]
        if mode=="delay":type(self).release.clear()
        type(self).calls.append(mode)
        if mode=="delay":type(self).release.wait(4)
        if mode=="timeout":time.sleep(6)
        if mode in ("credentials","scope","rate","down","redirect"):
            self.send_response({"credentials":401,"scope":403,"rate":429,"down":503,"redirect":302}[mode]);self.send_header("Retry-After","17");self.send_header("Location","http://127.0.0.1/private");self.end_headers();return
        now=datetime.now(timezone.utc)-timedelta(seconds=1)
        instant=now.isoformat().replace("+00:00","Z");until=(now+timedelta(hours=1)).isoformat().replace("+00:00","Z")
        results=[]
        for value in values:
            offer={"externalId":value if kind=="externalIds" else "remote","productId":"product-1","sku":value if kind=="skus" else None,
                "name":"Milk","presentation":"One carton","unitCode":"L","version":"1","updatedAt":instant,"active":True,
                "price":{"state":"known","amount":"999999999999999999.123456","currency":"USD","taxTreatment":"inclusive","observedAt":instant,"validUntil":until},
                "availability":{"tracking":"tracked","available":{"state":"known","quantity":"8.5"},"reserved":{"state":"known","quantity":"2"},"observedAt":instant,"validUntil":until}}
            if mode in ("equal","conflict"):offer.update(externalId="milk",version="10",name="Changed" if mode=="conflict" else "Milk")
            if mode in ("deleted","newer"):offer.update(externalId="deleted",version="5" if mode=="deleted" else "6")
            if mode=="stale":offer["price"]["observedAt"]=offer["availability"]["observedAt"]="2020-01-01T00:00:00Z"
            if mode=="unknown":offer["price"]={"state":"unknown","reason":"not_provided"}
            if "price" not in request["fields"]:offer["price"]={"state":"unknown","reason":"not_requested"}
            if "availability" not in request["fields"]:offer["availability"].update(tracking="unknown",available={"state":"unknown","reason":"not_requested"},reserved={"state":"unknown","reason":"not_requested"})
            status="not_found" if mode=="missing" else "unavailable" if mode=="unavailable" else "ambiguous" if mode=="ambiguous" else "found"
            offers=[] if status in ("not_found","unavailable") else [offer]
            if status=="ambiguous":offers.append({**deepcopy(offer),"externalId":"remote-2"})
            results.append({"selectorValue":value,"status":status,"offers":offers})
        response={"contractVersion":"1","sourceBusinessId":"other" if mode=="business" else "business-demo","results":results}
        raw=json.dumps(response).encode()
        if mode=="duplicate":raw=raw.replace(b'"contractVersion": "1"',b'"contractVersion":"1","contractVersion":"1"')
        if mode=="numeric":raw=raw.replace(b'"amount": "999999999999999999.123456"',b'"amount":1')
        if mode=="huge":raw=b" "*1048577+raw
        if mode=="order":response["results"][0]["selectorValue"]="wrong";raw=json.dumps(response).encode()
        self.send_response(200);self.send_header("Content-Type","text/html" if mode=="mime" else "application/json; charset=utf-8")
        if mode=="compression":self.send_header("Content-Encoding","gzip")
        self.end_headers()
        try:self.wfile.write(raw)
        except (BrokenPipeError,ConnectionResetError):pass

def exercise(h):
    http,expect,sql=h["http"],h["expect"],h["sql"]
    origin=os.environ["DOCPARSE_LIVE_PROVIDER_ORIGIN"]
    root,a,b=h["root"],h["a"],h["b"]
    definition={"sourceBusinessId":"business-demo","baseUrl":origin+"/prefix/","push":True,"pullLive":True,"pullScheduled":False,"pushKeyDays":90,
                "settings":{"name":"Live inventory","priceMaxAgeSeconds":600,"stockMaxAgeSeconds":60,"clockSkewSeconds":0,"syncIntervalSeconds":None}}
    source=expect(200,http(root+"/",{"requestId":str(uuid.uuid4()),"definition":definition},a));connection=source["connection"]
    path=root+"/"+connection["id"]
    request={"fields":["catalog","price","availability"],"selectorKind":"text","values":["Milk"]}
    expect(409,http(path+"/query",request,a))
    connection=expect(200,http(path+"/pull-key",{"revision":connection["revision"],"keyId":"provider-key","secretHex":SECRET},a,"PUT"))
    expect(401,http(path+"/query",request))
    expect(404,http(path+"/query",request,b))
    def query(value="Milk",fields=None,kind="text",status=200):
        sql(f"DELETE FROM integrations.live_query_windows WHERE connection_id='{connection['id']}'")
        return expect(status,http(path+"/query",{"fields":fields or request["fields"],"selectorKind":kind,"values":value if isinstance(value,list) else [value]},a))
    result=query();record=result["results"][0]["records"][0]
    assert record["priceCurrent"] and record["availabilityCurrent"] and record["offer"]["price"]["amount"]=="999999999999999999.123456"
    assert sql(f"SELECT count(*) FROM operational_data.records WHERE connection_id='{connection['id']}'")=="0"
    assert query(["offer-1","offer-2"],kind="externalIds")["results"][1]["selectorValue"]=="offer-2"
    assert query("sku-1",kind="skus")["results"][0]["records"][0]["offer"]["sku"]=="sku-1"
    assert query(fields=["catalog"])["results"][0]["records"][0]["offer"]["price"]["reason"]=="not_requested"
    assert not query("stale")["results"][0]["records"][0]["priceCurrent"]
    assert query("unknown")["results"][0]["records"][0]["offer"]["price"]["amount"] is None
    for value,expected in [("missing","not_found"),("ambiguous","ambiguous"),("unavailable","unavailable")]:assert query(value)["results"][0]["status"]==expected
    for value in ["business","duplicate","numeric","huge","mime","compression","order"]:query(value,status=502)
    for value,code,status in [("credentials","provider_credentials",409),("scope","provider_scope",409),("rate","provider_rate",429),("down","provider_unavailable",503),("redirect","provider_unavailable",503),("timeout","provider_unavailable",503)]:
        assert query(value,status=status)["code"]==code
    for invalid in [{**request,"values":[]},{**request,"fields":["price","price"]},{**request,"selectorKind":"url"},{**request,"values":["x\n"]}]:expect(400,http(path+"/query",invalid,a))
    # Push and live evidence share revision/tombstone rules, but live reads never change the projection.
    event=h["event"]("10");event_path=f"/api/integrations/v1/connections/{connection['id']}/events"
    h["completed"](expect(202,h["signed"](event_path,source,h["encode"](event))))
    query("milk",kind="externalIds",status=502);query("equal");query("conflict",status=502)
    event=h["event"]();event["change"]={"kind":"delete","tombstone":{"externalId":"deleted","version":"5","deletedAt":event["occurredAt"]}}
    h["completed"](expect(202,h["signed"](event_path,source,h["encode"](event))))
    query("deleted",status=502);query("newer")
    assert sql(f"SELECT version FROM operational_data.records WHERE connection_id='{connection['id']}' AND external_id='deleted'")=="5"
    if "--browser" in sys.argv:
        env=dict(os.environ,VITE_API_PROXY_TARGET=h["BASE"],DOCPARSE_REAL_API=h["BASE"],DOCPARSE_TEST_ADMIN_EMAIL="push-a",DOCPARSE_TEST_ADMIN_PASSWORD=h["PASSWORD"])
        subprocess.run(["npm.cmd" if os.name=="nt" else "npm","--prefix","front","test","--","tests/real-live-query.spec.ts","--workers=1","--output="+str(ROOT/".local"/"live-query-qa"/h["SUFFIX"])],cwd=ROOT,env=env,check=True)
    sql(f"INSERT INTO integrations.live_query_windows(connection_id,window_start,count) VALUES('{connection['id']}',date_trunc('minute',now()),30) ON CONFLICT(connection_id,window_start) DO UPDATE SET count=30")
    expect(429,http(path+"/query",request,a))
    # Replacing credentials while the provider is responding invalidates that in-flight result.
    control=origin.replace("host.docker.internal","127.0.0.1")
    def calls():
        with urllib.request.urlopen(control+"/stats",timeout=2) as response:return json.loads(response.read())
    def wait_call(previous):
        for _ in range(60):
            current=calls()
            if len(current)>previous and current[-1]=="delay":return
            time.sleep(.02)
        raise AssertionError("Provider did not receive the blocked query")
    with ThreadPoolExecutor(max_workers=1) as pool:
        previous=len(calls())
        pending=pool.submit(query,"delay",status=409)
        wait_call(previous)
        connection=expect(200,http(path+"/pull-key",{"revision":connection["revision"],"keyId":"provider-key","secretHex":SECRET},a,"PUT"))
        urllib.request.urlopen(control+"/release",timeout=2).close()
        assert pending.result()["code"]=="connection_conflict"
    with ThreadPoolExecutor(max_workers=1) as pool:
        previous=len(calls())
        pending=pool.submit(query,"delay",status=409)
        wait_call(previous)
        expect(204,http(path+"/disconnect",{"revision":connection["revision"]},a))
        urllib.request.urlopen(control+"/release",timeout=2).close()
        assert pending.result()["code"]=="connection_conflict"
    expect(404,http(path+"/query",request,a))
    print("PASS: real signed provider queries, prefix/signatures, selectors/scopes, strict bounded responses, errors/deadline, precision/freshness, read-only revision/tombstone checks, tenant denial, rate limits and in-flight credential/disconnection fences.")

if __name__=="__main__":
    server=ThreadingHTTPServer(("0.0.0.0",0),Provider)
    threading.Thread(target=server.serve_forever,daemon=True).start()
    env=dict(os.environ,DOCPARSE_LIVE_PROVIDER_ORIGIN=f"http://host.docker.internal:{server.server_port}")
    try:subprocess.run([sys.executable,"tests/push_integration.py","--live",*sys.argv[1:]],cwd=ROOT,env=env,check=True)
    finally:server.shutdown();server.server_close()
