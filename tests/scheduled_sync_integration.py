"""Stable snapshot/incremental provider and disposable database acceptance."""
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from http.server import ThreadingHTTPServer
import hashlib
import hmac
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
import uuid
from live_queries_integration import Provider, SECRET

ROOT=Path(__file__).resolve().parents[1]

class ScheduledProvider(Provider):
    mode="normal"
    sync_calls=[]
    release_sync=threading.Event()
    release_page_two=threading.Event()
    records={}
    @classmethod
    def offer(cls,external,version):
        key=(external,version)
        if key not in cls.records:
            now=datetime.now(timezone.utc)-timedelta(seconds=1);instant=now.isoformat().replace("+00:00","Z");until=(now+timedelta(hours=1)).isoformat().replace("+00:00","Z")
            cls.records[key]={"externalId":external,"productId":"product-1","sku":None,"name":"Milk","presentation":"One carton","unitCode":"L","version":version,"updatedAt":instant,"active":True,
                "price":{"state":"known","amount":"1.25","currency":"USD","taxTreatment":"inclusive","observedAt":instant,"validUntil":until},
                "availability":{"tracking":"tracked","available":{"state":"known","quantity":"8.5"},"reserved":{"state":"known","quantity":"2"},"observedAt":instant,"validUntil":until}}
        return {"kind":"upsert","offer":deepcopy(cls.records[key])}
    def do_GET(self):
        parsed=urllib.parse.urlsplit(self.path);query=urllib.parse.parse_qs(parsed.query)
        if parsed.path=="/sync/control":
            if "mode" in query:type(self).mode=query["mode"][0]
            if "release" in query:type(self).release_sync.set()
            if "page_two" in query:type(self).release_page_two.set()
            body=json.dumps({"mode":type(self).mode,"calls":type(self).sync_calls}).encode();self.send_response(200);self.end_headers();self.wfile.write(body);return
        if parsed.path!="/prefix/integrations/v1/operational-data/changes":return super().do_GET()
        assert query["sourceBusinessId"]==["business-demo"] and query["pageSize"]==["100"]
        assert int(self.headers.get("Content-Length","0"))==0
        key=self.headers.get("X-Fyr-Key-Id");stamp=self.headers.get("X-Fyr-Timestamp");nonce=self.headers.get("X-Fyr-Request-Id")
        canonical="\n".join(["FYR-OPDATA-V1","GET",self.path,key,stamp,nonce,hashlib.sha256(b"").hexdigest()])
        assert self.headers.get("X-Fyr-Signature")=="v1="+hmac.new(bytes.fromhex(SECRET),canonical.encode(),hashlib.sha256).hexdigest()
        assert key=="provider-key" and nonce not in type(self).nonces;type(self).nonces.add(nonce)
        mode=type(self).mode;cursor=query.get("cursor",[None])[0]
        if mode=="block":type(self).release_sync.clear()
        type(self).sync_calls.append({"cursor":cursor,"mode":mode})
        if mode=="down":self.send_response(503);self.send_header("Retry-After","17");self.end_headers();return
        if mode=="permanent":self.send_response(403);self.end_headers();return
        if mode=="expired" and cursor is not None:self.send_response(410);self.end_headers();return
        if mode=="block":type(self).release_sync.wait(8)
        if cursor=="snapshot/2+secret":type(self).release_page_two.wait(8)
        snapshot=cursor is None or cursor=="snapshot/2+secret"
        watermark="20" if mode in ("resync","expired") else "10"
        complete=cursor is not None or mode in ("resync","expired")
        next_cursor="snapshot/2+secret" if not complete else "incremental/"+watermark+"+secret"
        changes=[self.offer("present","3")] if cursor is None else [self.offer("second","8")]
        if mode in ("resync","expired") and cursor is None:changes=[self.offer("present","12")]
        if not snapshot:
            complete=True
            next_cursor="incremental/13+secret" if cursor=="incremental/10+secret" else cursor
            changes=[self.offer("present","12"),{"kind":"delete","tombstone":{"externalId":"second","version":"13","deletedAt":self.offer("present","12")["offer"]["updatedAt"]}}] if cursor=="incremental/10+secret" else []
        if mode=="block":changes=[self.offer("obsolete","30")];next_cursor="obsolete/30+secret"
        if mode=="atomic":changes=[self.offer("atomic","21")];next_cursor="atomic/21+secret"
        body={"contractVersion":"1","sourceBusinessId":"business-demo","mode":"snapshot" if snapshot else "incremental","snapshotId":"snapshot-"+watermark if snapshot else None,
            "snapshotWatermark":watermark if snapshot else None,"complete":complete,"nextCursor":next_cursor,"changes":changes}
        if mode=="bad":body["extra"]=True
        if mode=="mismatch" and cursor is not None:body["snapshotId"]="wrong-snapshot"
        raw=json.dumps(body).encode();self.send_response(200);self.send_header("Content-Type","application/json; charset=utf-8");self.end_headers()
        try:self.wfile.write(raw)
        except (BrokenPipeError,ConnectionResetError):pass

def exercise(h):
    http,expect,sql=h["http"],h["expect"],h["sql"];root,a,b=h["root"],h["a"],h["b"]
    origin=os.environ["DOCPARSE_LIVE_PROVIDER_ORIGIN"];control=origin.replace("host.docker.internal","127.0.0.1")+"/sync/control"
    def mode(value=None,release=False):
        with urllib.request.urlopen(control+("?mode="+value if value else "")+("&release=1" if value and release else "?release=1" if release else ""),timeout=3) as response:return json.loads(response.read())
    def wait(predicate,message):
        for _ in range(140):
            if predicate():return
            time.sleep(.1)
        raise AssertionError(message)
    definition={"sourceBusinessId":"business-demo","baseUrl":origin+"/prefix/","push":True,"pullLive":True,"pullScheduled":True,"pushKeyDays":90,
        "settings":{"name":"Scheduled inventory","priceMaxAgeSeconds":600,"stockMaxAgeSeconds":60,"clockSkewSeconds":0,"syncIntervalSeconds":60}}
    source=expect(200,http(root+"/",{"requestId":str(uuid.uuid4()),"definition":definition},a));connection=source["connection"];cid=connection["id"];path=root+"/"+cid
    def status():return expect(200,http(path+"/sync",token=a))
    def due():sql(f"UPDATE integrations.sync_jobs SET due_at=now() WHERE connection_id='{cid}'")
    def send(value,outcome="applied"):
        result=expect(202,h["signed"](f"/api/integrations/v1/connections/{cid}/events",source,h["encode"](value)));h["completed"](result,outcome)
    def records():return {item["offer"]["externalId"]:item for item in expect(200,http(path+"/records",token=a))["records"]}
    expect(401,http(path+"/sync"));expect(404,http(path+"/sync",token=b))
    expect(404,http(path+"/sync/restart",{"revision":connection["revision"]},b))
    wait(lambda:status()["state"]=="failed","Missing provider key should stop without network traffic")
    for external,version in [("absent","2"),("present","1"),("newer","25")]:send(h["event"](version,external))
    connection=expect(200,http(path+"/pull-key",{"revision":connection["revision"],"keyId":"provider-key","secretHex":SECRET},a,"PUT"))
    # Snapshot page one must not remove records absent from a partial page.
    wait(lambda:sql(f"SELECT coalesce(protected_cursor,'')<>'' FROM integrations.sync_jobs WHERE connection_id='{cid}'")=="t","First snapshot page not committed")
    assert "absent" in records() and not status()["snapshotCompleted"]
    urllib.request.urlopen(control+"?page_two=1",timeout=3).close()
    wait(lambda:status()["snapshotCompleted"],"Snapshot did not complete")
    rows=records();assert "absent" not in rows and "present" in rows and "second" in rows and rows["newer"]["offer"]["version"]=="25"
    encrypted=sql(f"SELECT protected_cursor FROM integrations.sync_jobs WHERE connection_id='{cid}'");assert "secret" not in encrypted and "incremental" not in encrypted
    assert "cursor" not in json.dumps(status()).lower()
    send(h["event"]("9","absent"),"ignored_older_version")
    expect(502,http(path+"/query",{"fields":["catalog","price","availability"],"selectorKind":"externalIds","values":["absent"]},a))
    due();wait(lambda:sql(f"SELECT last_version::text FROM operational_data.sync_state WHERE connection_id='{cid}'")=="13","Incremental page not applied")
    assert records()["present"]["offer"]["version"]=="12" and "second" not in records()
    restart_cursor=sql(f"SELECT protected_cursor FROM integrations.sync_jobs WHERE connection_id='{cid}'")
    h["docker"]("restart",h["CONTAINER"]);h["healthy"]()
    assert sql(f"SELECT protected_cursor FROM integrations.sync_jobs WHERE connection_id='{cid}'")==restart_cursor
    # Retry scheduling honors Retry-After without advancing a cursor or losing state across restart.
    mode("down");due()
    wait(lambda:status()["attempts"]==1 and status()["state"]=="waiting" and status()["errorCode"]=="provider_unavailable","Transient failure not persisted")
    assert sql(f"SELECT extract(epoch FROM due_at-updated_at)::int FROM integrations.sync_jobs WHERE connection_id='{cid}'")=="17"
    before=sql(f"SELECT protected_cursor FROM integrations.sync_jobs WHERE connection_id='{cid}'")
    for attempt,delay in [(2,30),(3,120),(4,300)]:
        due();wait(lambda:status()["attempts"]==attempt and status()["state"]=="waiting","Retry did not persist")
        assert sql(f"SELECT extract(epoch FROM due_at-updated_at)::int FROM integrations.sync_jobs WHERE connection_id='{cid}'")==str(delay)
    due();wait(lambda:status()["state"]=="failed","Retry limit did not terminate")
    assert status()["attempts"]==5 and sql(f"SELECT protected_cursor FROM integrations.sync_jobs WHERE connection_id='{cid}'")==before
    # Restart is explicit and full; key/config recovery and cursor expiry preserve existing records.
    mode("resync");expect(204,http(path+"/sync/restart",{"revision":connection["revision"]},a))
    wait(lambda:status()["snapshotCompleted"] and status()["state"]=="waiting","Full resync failed")
    assert "newer" in records() and sql(f"SELECT version::text FROM operational_data.snapshot_floors WHERE connection_id='{cid}'")=="20"
    mode("expired");due();wait(lambda:status()["errorCode"]=="cursor_expired" and not status()["snapshotCompleted"],"Cursor expiry did not schedule resynchronization")
    due();wait(lambda:status()["snapshotCompleted"],"Expired cursor resync failed")
    # An old in-flight generation must not commit after explicit resynchronization.
    mode("block");previous=len(mode()["calls"]);due()
    wait(lambda:len(mode()["calls"])>previous,"Blocked provider page did not start")
    expect(204,http(path+"/sync/restart",{"revision":connection["revision"]},a));mode("resync",True)
    wait(lambda:status()["snapshotCompleted"] and status()["state"]=="waiting","New generation did not complete")
    assert "obsolete" not in records()
    # A failed cursor/outcome write rolls back both record application and sync position.
    before=sql(f"SELECT protected_cursor FROM integrations.sync_jobs WHERE connection_id='{cid}'")
    sql("""CREATE FUNCTION integrations.fixture_sync_outcome() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN IF NEW.state='waiting' AND NEW.last_success_at IS DISTINCT FROM OLD.last_success_at THEN
            RAISE EXCEPTION 'fixture' USING ERRCODE='40P01'; END IF; RETURN NEW; END $$;
        CREATE TRIGGER fixture_sync_outcome BEFORE UPDATE ON integrations.sync_jobs FOR EACH ROW EXECUTE FUNCTION integrations.fixture_sync_outcome();""")
    mode("atomic");due();wait(lambda:status()["attempts"]==1 and status()["state"]=="waiting","Failed outcome did not retry")
    assert "atomic" not in records() and sql(f"SELECT protected_cursor FROM integrations.sync_jobs WHERE connection_id='{cid}'")==before
    sql("DROP TRIGGER fixture_sync_outcome ON integrations.sync_jobs;DROP FUNCTION integrations.fixture_sync_outcome()")
    due();wait(lambda:"atomic" in records(),"Atomic page did not recover")
    mode("resync")
    last_success=status()["lastSuccessAt"]
    sql(f"UPDATE integrations.sync_jobs SET state='processing',attempts=1,lease_id='{uuid.uuid4()}',lease_until=now()-interval '1 second' WHERE connection_id='{cid}'")
    wait(lambda:status()["state"]=="waiting" and status()["attempts"]==0 and status()["lastSuccessAt"]!=last_success,"Expired lease did not resume its cursor")
    sql(f"UPDATE integrations.sync_jobs SET state='processing',attempts=5,lease_id='{uuid.uuid4()}',lease_until=now()-interval '1 second' WHERE connection_id='{cid}'")
    wait(lambda:status()["state"]=="failed" and status()["errorCode"]=="sync_failed","Expired final lease did not terminate")
    # Permanent provider errors stop on the first attempt.
    mode("permanent");sql(f"UPDATE integrations.sync_jobs SET state='waiting',attempts=0,due_at=now() WHERE connection_id='{cid}'");wait(lambda:status()["state"]=="failed","Permanent error did not terminate")
    assert status()["attempts"]==1 and status()["errorCode"]=="provider_scope"
    other_org=next(item["id"] for item in expect(200,http("/api/organizations/",token=b)) if item["role"]=="Admin")
    other=expect(200,http(f"/api/organizations/{other_org}/connections/",{"requestId":str(uuid.uuid4()),"definition":definition},b))["connection"]["id"]
    wait(lambda:sql(f"SELECT count(*) FROM integrations.sync_jobs WHERE connection_id='{other}'")=="1","Second tenant's job was not created")
    credential=sql(f"SELECT credential_id FROM integrations.sync_jobs WHERE connection_id='{cid}'")
    try:sql(f"UPDATE integrations.sync_jobs SET credential_id='{credential}' WHERE connection_id='{other}'")
    except subprocess.CalledProcessError as error:assert "foreign key" in (error.stderr or "").lower()
    else:raise AssertionError("Cross-tenant sync credential binding was accepted")
    if "--browser" in sys.argv:
        env=dict(os.environ,VITE_API_PROXY_TARGET=h["BASE"],DOCPARSE_REAL_API=h["BASE"],DOCPARSE_TEST_ADMIN_EMAIL="push-a",DOCPARSE_TEST_ADMIN_PASSWORD=h["PASSWORD"])
        subprocess.run(["npm.cmd" if os.name=="nt" else "npm","--prefix","front","test","--","tests/real-scheduled-sync.spec.ts","--workers=1","--output="+str(ROOT/".local"/"sync-qa"/h["SUFFIX"])],cwd=ROOT,env=env,check=True)
    expect(204,http(path+"/disconnect",{"revision":connection["revision"]},a));expect(404,http(path+"/sync",token=a))
    print("PASS: signed empty-body GET/prefix/cursor encoding, partial/full snapshot semantics, incremental versions/deletes, Push preservation, absence floors/live denial, encrypted cursor atomicity, restart, retries/Retry-After/exhaustion, cursor expiry, generation fencing, permanent errors and tenant status scope.")

if __name__=="__main__":
    server=ThreadingHTTPServer(("0.0.0.0",0),ScheduledProvider);threading.Thread(target=server.serve_forever,daemon=True).start()
    env=dict(os.environ,DOCPARSE_LIVE_PROVIDER_ORIGIN=f"http://host.docker.internal:{server.server_port}")
    try:subprocess.run([sys.executable,"tests/push_integration.py","--live","--scheduled",*sys.argv[1:]],cwd=ROOT,env=env,check=True)
    finally:server.shutdown();server.server_close()
