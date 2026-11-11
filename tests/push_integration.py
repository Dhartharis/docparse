"""Signed operational Push against a disposable local PostgreSQL database; no external providers."""
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
SUFFIX = uuid.uuid4().hex
DATABASE = "docparse_push_test_" + SUFFIX
CONTAINER = "docparse-push-test-" + SUFFIX
IMAGE = "docparse-push-test:" + SUFFIX
PASSWORD = "Fixture-" + SUFFIX + "!aA1"
BASE = ""

def docker(*args, check=True):
    return subprocess.run(["docker", *args], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", check=check)

def sql(statement):
    return docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE,
                  "-At", "-v", "ON_ERROR_STOP=1", "-c", statement).stdout.strip()

def http(path, data=None, token=None, method=None, headers=None):
    body = json.dumps(data).encode() if isinstance(data, dict) else data
    values = {"Content-Type": "application/json", **(headers or {})}
    if token:
        values["Authorization"] = "Bearer " + token
    try:
        response = urllib.request.urlopen(urllib.request.Request(BASE + path, data=body, headers=values, method=method), timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        content = response.read()
        return response.status, json.loads(content) if content else None

def expect(status, response):
    assert response[0] == status, (status, response)
    return response[1]

def healthy():
    for _ in range(100):
        try:
            if http("/health")[0] == 200:
                return
        except (urllib.error.URLError, ConnectionError):
            pass
        time.sleep(.2)
    raise AssertionError("Temporary Push API did not start.")

def signed(path, source, body=b"", method="POST", nonce=None, overrides=None):
    nonce = nonce or str(uuid.uuid4())
    timestamp = str(int(time.time()))
    key_id = source["connection"]["pushKey"]["keyId"]
    canonical = "\n".join(["FYR-OPDATA-V1", method, path, key_id, timestamp, nonce, hashlib.sha256(body).hexdigest()])
    signature = hmac.new(bytes.fromhex(source["pushSecretHex"]), canonical.encode(), hashlib.sha256).hexdigest()
    return http(path, body if method == "POST" else None, method=method, headers={
        "X-Fyr-Key-Id": key_id, "X-Fyr-Timestamp": timestamp, "X-Fyr-Request-Id": nonce,
        "X-Fyr-Signature": "v1="+signature, **(overrides or {})})

def event(version="1", external="milk"):
    now = datetime.now(timezone.utc)-timedelta(seconds=2)
    instant = now.isoformat().replace("+00:00", "Z")
    until = (now+timedelta(hours=1)).isoformat().replace("+00:00", "Z")
    return {"contractVersion": "1", "sourceBusinessId": "business-demo", "eventId": str(uuid.uuid4()), "occurredAt": instant,
            "change": {"kind": "upsert", "offer": {"externalId": external, "productId": "product-1", "sku": None,
                "name": "Milk", "presentation": "One carton", "unitCode": "L", "version": version, "updatedAt": instant, "active": True,
                "price": {"state": "known", "amount": "999999999999999999.123456", "currency": "USD", "taxTreatment": "inclusive", "observedAt": instant, "validUntil": until},
                "availability": {"tracking": "tracked", "available": {"state": "known", "quantity": "8.5"},
                    "reserved": {"state": "known", "quantity": "2"}, "observedAt": instant, "validUntil": until}}}}

def encode(value):
    return json.dumps(value, separators=(",", ":")).encode()

def completed(receipt, expected="applied"):
    receipt_id = receipt["receiptId"]
    for _ in range(80):
        row = sql(f"SELECT state||':'||coalesce(error_code,'') FROM integrations.push_inbox WHERE id='{receipt_id}'")
        if row.split(":")[0] in ("applied", "ignored_older_version", "failed"):
            assert row.split(":")[0] == expected, row
            return row
        time.sleep(.15)
    raise AssertionError("Receipt did not finish: " + row)

created = False
try:
    docker("build", "-t", IMAGE, "back")
    password = docker("compose", "exec", "-T", "postgres", "printenv", "POSTGRES_PASSWORD").stdout.strip()
    postgres = docker("compose", "ps", "-q", "postgres").stdout.strip()
    network = next(iter(json.loads(docker("inspect", postgres).stdout)[0]["NetworkSettings"]["Networks"]))
    docker("compose", "exec", "-T", "postgres", "createdb", "-U", "docparse", DATABASE)
    created = True
    connection = f"Host=postgres;Database={DATABASE};Username=docparse;Password={password}"
    shared = ["--network", network, "--add-host", "host.docker.internal:host-gateway", "-e", "ASPNETCORE_ENVIRONMENT=Development", "-e", "PostgreSQL__ConnectionString=" + connection, "-e", "FyrUsage__Enabled=false"]
    if "--live" in sys.argv:
        shared += ["-e", "Integrations__AllowedProviderOrigins__0="+os.environ["DOCPARSE_LIVE_PROVIDER_ORIGIN"]]
    docker("run", "--rm", *shared, IMAGE, "--migrate")
    for name in ("a", "b"):
        docker("run", "--rm", *shared, "-e", f"LocalAdmin__Email=push-{name}-{SUFFIX}@example.test",
               "-e", "LocalAdmin__UserName=push-"+name, "-e", "LocalAdmin__Password="+PASSWORD, IMAGE, "--bootstrap-local-admin")
    with socket.socket() as reserve:
        reserve.bind(("127.0.0.1", 0))
        port = reserve.getsockname()[1]
    blob_key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="
    docker("run", "-d", "--name", CONTAINER, *shared, "-p", f"127.0.0.1:{port}:8080", "-e", "ASPNETCORE_URLS=http://+:8080",
           "-e", "PublicWeb__BaseUrl=http://127.0.0.1:45173", "-e", "AzureStorage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey="+blob_key+";BlobEndpoint=http://azurite:10000/devstoreaccount1;", IMAGE)
    BASE = "http://127.0.0.1:"+str(port)
    healthy()
    a = expect(200,http("/api/auth/login",{"email":"push-a","password":PASSWORD}))["accessToken"]
    b = expect(200,http("/api/auth/login",{"email":"push-b","password":PASSWORD}))["accessToken"]
    org = expect(200,http("/api/organizations/",token=a))[0]["id"]
    root = f"/api/organizations/{org}/connections"
    def create(name):
        return expect(200,http(root+"/",{"requestId":str(uuid.uuid4()),"definition":{
            "sourceBusinessId":"business-demo","baseUrl":None,"push":True,"pullLive":False,"pullScheduled":False,"pushKeyDays":90,
            "settings":{"name":name,"priceMaxAgeSeconds":600,"stockMaxAgeSeconds":60,"clockSkewSeconds":0,"syncIntervalSeconds":None}}},a))
    source = create("Push inventory")
    second = create("Other inventory")
    source_id = source["connection"]["id"]
    path = f"/api/integrations/v1/connections/{source_id}/events"
    records = root+"/"+source_id+"/records"
    payload = event()
    raw = encode(payload)
    request_id = str(uuid.uuid4())
    receipt = expect(202,signed(path,source,raw,nonce=request_id))
    expect(409,signed(path,source,raw,nonce=request_id))
    completed(receipt)
    assert expect(200,signed(path,source,raw))["receiptId"] == receipt["receiptId"]
    changed = deepcopy(payload); changed["change"]["offer"]["name"] = "Changed"
    expect(409,signed(path,source,encode(changed)))
    receipt_path = f"/api/integrations/v1/connections/{source_id}/receipts/{receipt['receiptId']}?z=%2F&a=1"
    assert expect(200,signed(receipt_path,source,method="GET"))["status"] == "applied"
    expect(404,signed(receipt_path.replace(source_id,second["connection"]["id"]),second,method="GET"))
    expect(401,signed(path,source,raw,overrides={"X-Fyr-Signature":"v1="+"0"*64}))
    expect(401,signed(path,source,raw,overrides={"X-Fyr-Timestamp":str(int(time.time())-301)}))
    invalid = event(); invalid["sourceBusinessId"] = "other-business"
    expect(403,signed(path,source,encode(invalid)))
    for bad in [raw.replace(b'"amount":"999999999999999999.123456"',b'"amount":12'),
                raw.replace(b'"version":"1"',b'"version":"1","version":"1"'),
                raw.replace(b'"name":"Milk"',b'"name":"Mi\\u0000lk"'),
                raw.replace(b'"contractVersion":"1"',b'"contractVersion":"1","extra":1')]:
        expect(400,signed(path,source,bad))
    expect(415,signed(path,source,raw,overrides={"Content-Type":"application/json; charset=latin1"}))
    expect(413,signed(path,source,b" "*1048577))
    padded = event(external="padded")
    completed(expect(202,signed(path,source,b" "*70000+encode(padded))))
    assert int(sql("SELECT max(octet_length(payload::text)) FROM integrations.push_inbox")) < 10000
    snapshot = expect(200,http(records,token=a))
    offer = snapshot["records"][0]
    assert offer["offer"]["price"]["amount"] == "999999999999999999.123456" and offer["priceCurrent"] and offer["availabilityCurrent"]
    expect(401,http(records))
    expect(404,http(records,token=b))
    sql(f'''INSERT INTO access.memberships("OrganizationId","UserId","Role") SELECT '{org}',"Id",'Viewer' FROM access."AspNetUsers" WHERE "UserName"='push-b' ''')
    expect(404,http(records,token=b))
    def send(value,status="applied"):
        result = expect(202,signed(path,source,encode(value)))
        completed(result,status)
        return result
    send(event("3"))
    send(event("2"),"ignored_older_version")
    conflict = event("3"); conflict["change"]["offer"]["name"]="Conflicting version"
    assert completed(expect(202,signed(path,source,encode(conflict))),"failed").endswith("record_version_conflict")
    same = event("4")
    send(same)
    same["eventId"] = str(uuid.uuid4())
    send(same)
    tombstone = event(); tombstone["change"]={"kind":"delete","tombstone":{"externalId":"milk","version":"5","deletedAt":tombstone["occurredAt"]}}
    send(tombstone)
    send(event("4"),"ignored_older_version")
    assert not any(item["offer"]["externalId"] == "milk" for item in expect(200,http(records,token=a))["records"])
    send(event("99999999999999999999999999999999999999"))
    unknown = event(external="unknown")
    unknown["change"]["offer"]["price"]={"state":"unknown","reason":"not_provided"}
    unknown["change"]["offer"]["availability"].update(tracking="untracked",available={"state":"unknown","reason":"not_tracked"},reserved={"state":"unknown","reason":"not_tracked"})
    send(unknown)
    stale = event(external="stale")
    old = (datetime.now(timezone.utc)-timedelta(days=1)).isoformat().replace("+00:00","Z")
    stale["change"]["offer"]["price"]["observedAt"]=old
    stale["change"]["offer"]["availability"]["observedAt"]=old
    send(stale)
    rows = {item["offer"]["externalId"]:item for item in expect(200,http(records,token=a))["records"]}
    assert not rows["unknown"]["priceCurrent"] and rows["unknown"]["offer"]["price"]["amount"] is None
    assert not rows["stale"]["priceCurrent"] and not rows["stale"]["availabilityCurrent"]
    # Concurrent deliveries serialize by the authenticated connection, never duplicate durable acceptance.
    concurrent = encode(event(external="concurrent"))
    with ThreadPoolExecutor(max_workers=4) as pool:
        responses = list(pool.map(lambda _: signed(path,source,concurrent),range(4)))
    assert sorted(item[0] for item in responses) == [200,200,200,202]
    assert len({item[1]["receiptId"] for item in responses}) == 1
    completed(responses[0][1])
    docker("restart",CONTAINER); healthy()
    expect(409,signed(path,source,raw,nonce=request_id))
    assert expect(200,signed(path,source,raw))["receiptId"] == receipt["receiptId"]
    # Durable transient retries: force deadlock classification; advance only fixture due times.
    sql("""CREATE FUNCTION operational_data.fixture_failure() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN IF NEW.external_id='retry' THEN RAISE EXCEPTION 'fixture' USING ERRCODE='40P01'; END IF; RETURN NEW; END $$;
        CREATE TRIGGER fixture_failure BEFORE INSERT OR UPDATE ON operational_data.records FOR EACH ROW EXECUTE FUNCTION operational_data.fixture_failure();""")
    retried = expect(202,signed(path,source,encode(event(external="retry"))))
    rid = retried["receiptId"]
    for attempt,delay in enumerate([10,30,120,300],1):
        for _ in range(100):
            row = sql(f"SELECT attempts||':'||state||':'||round(extract(epoch FROM due_at-updated_at)) FROM integrations.push_inbox WHERE id='{rid}'")
            if row == f"{attempt}:accepted:{delay}": break
            time.sleep(.1)
        else: raise AssertionError("Retry schedule: "+row)
        sql(f"UPDATE integrations.push_inbox SET due_at=now() WHERE id='{rid}'")
    completed(retried,"failed")
    assert sql(f"SELECT attempts FROM integrations.push_inbox WHERE id='{rid}'") == "5"
    sql("DROP TRIGGER fixture_failure ON operational_data.records; DROP FUNCTION operational_data.fixture_failure()")
    sql("""CREATE FUNCTION integrations.fixture_outcome_failure() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN IF NEW.state='applied' AND NEW.payload->'change'->'offer'->>'externalId'='atomic' THEN
            RAISE EXCEPTION 'fixture' USING ERRCODE='40P01'; END IF; RETURN NEW; END $$;
        CREATE TRIGGER fixture_outcome_failure BEFORE UPDATE ON integrations.push_inbox FOR EACH ROW EXECUTE FUNCTION integrations.fixture_outcome_failure();""")
    atomic = expect(202,signed(path,source,encode(event(external="atomic"))))
    for _ in range(80):
        state = sql(f"SELECT state FROM integrations.push_inbox WHERE id='{atomic['receiptId']}' AND attempts=1")
        if state == "accepted": break
        time.sleep(.1)
    else: raise AssertionError("Atomic outcome retry did not persist")
    assert sql("SELECT count(*) FROM operational_data.records WHERE external_id='atomic'") == "0", "Projection committed without its receipt outcome"
    sql("DROP TRIGGER fixture_outcome_failure ON integrations.push_inbox; DROP FUNCTION integrations.fixture_outcome_failure()")
    sql(f"UPDATE integrations.push_inbox SET due_at=now() WHERE id='{atomic['receiptId']}'")
    completed(atomic)
    sql("""CREATE FUNCTION operational_data.fixture_permanent() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN IF NEW.external_id='permanent' THEN RAISE EXCEPTION 'fixture' USING ERRCODE='23514'; END IF; RETURN NEW; END $$;
        CREATE TRIGGER fixture_permanent BEFORE INSERT OR UPDATE ON operational_data.records FOR EACH ROW EXECUTE FUNCTION operational_data.fixture_permanent();""")
    permanent = expect(202,signed(path,source,encode(event(external="permanent"))))
    completed(permanent,"failed")
    assert sql(f"SELECT attempts FROM integrations.push_inbox WHERE id='{permanent['receiptId']}'") == "1"
    sql("DROP TRIGGER fixture_permanent ON operational_data.records; DROP FUNCTION operational_data.fixture_permanent()")
    # Expired final leases terminate; a crash after projection replays idempotently.
    send(event(external="lease"))
    lid = sql("SELECT id FROM integrations.push_inbox WHERE payload->'change'->'offer'->>'externalId'='lease'")
    sql(f"UPDATE integrations.push_inbox SET state='processing',lease_id='{uuid.uuid4()}',lease_until=now()-interval '1 second',attempts=1 WHERE id='{lid}'")
    completed({"receiptId":lid})
    assert sql(f"SELECT attempts FROM integrations.push_inbox WHERE id='{lid}'") == "2"
    sql(f"UPDATE integrations.push_inbox SET state='processing',lease_id='{uuid.uuid4()}',lease_until=now()-interval '1 second',attempts=5 WHERE id='{lid}'")
    completed({"receiptId":lid},"failed")
    docker("stop",CONTAINER)
    published = docker("compose","port","postgres","5432").stdout.strip().rsplit(":",1)[1]
    fence_env = dict(os.environ,DOCPARSE_PUSH_TEST_CONNECTION=f"Host=127.0.0.1;Port={published};Database={DATABASE};Username=docparse;Password={password}",
                     DOCPARSE_PUSH_TEST_ORG=org,DOCPARSE_PUSH_TEST_SOURCE=source_id)
    subprocess.run(["dotnet","run","--project","tests/PushPolicyTests/PushPolicyTests.csproj","--","--fencing"],cwd=ROOT,env=fence_env,check=True)
    docker("start",CONTAINER); healthy()
    if "--live" in sys.argv:
        from live_queries_integration import exercise
        exercise(globals())
    if "--scheduled" in sys.argv:
        from scheduled_sync_integration import exercise
        exercise(globals())
    if "--browser" in sys.argv:
        env = dict(os.environ,VITE_API_PROXY_TARGET=BASE,DOCPARSE_REAL_API=BASE,DOCPARSE_TEST_ADMIN_EMAIL="push-a",DOCPARSE_TEST_ADMIN_PASSWORD=PASSWORD)
        subprocess.run(["npm.cmd" if os.name == "nt" else "npm","--prefix","front","test","--","tests/real-push.spec.ts","--workers=1"],cwd=ROOT,env=env,check=True)
    sql(f"INSERT INTO integrations.push_windows(connection_id,window_start,count) VALUES('{source_id}',date_trunc('minute',now()),120) ON CONFLICT(connection_id,window_start) DO UPDATE SET count=120")
    expect(429,signed(path,source,raw))
    sql(f"DELETE FROM integrations.push_windows WHERE connection_id='{source_id}'")
    # Encryption purposes prevent swapping a different connection's protected secret.
    encrypted = sql(f"SELECT protected_secret FROM integrations.credentials WHERE connection_id='{source_id}' AND revoked_at IS NULL")
    sql(f"UPDATE integrations.credentials SET protected_secret=(SELECT protected_secret FROM integrations.credentials WHERE connection_id='{second['connection']['id']}' AND revoked_at IS NULL) WHERE connection_id='{source_id}' AND revoked_at IS NULL")
    expect(401,signed(path,source,raw))
    sql(f"UPDATE integrations.credentials SET protected_secret='{encrypted}' WHERE connection_id='{source_id}' AND revoked_at IS NULL")
    sql(f"UPDATE integrations.credentials SET expires_at=now()-interval '1 second' WHERE connection_id='{second['connection']['id']}' AND revoked_at IS NULL")
    expect(401,signed(path.replace(source_id,second['connection']['id']),second,raw))
    rotated = expect(200,http(root+"/"+source_id+"/push-key",{"revision":source["connection"]["revision"],"days":90},a))
    expect(401,signed(path,source,raw))
    source = {"connection":rotated["connection"],"pushSecretHex":rotated["secretHex"]}
    expect(409,signed(path,source,raw,nonce=request_id))
    expect(204,http(root+"/"+source_id+"/disconnect",{"revision":source["connection"]["revision"]},a))
    expect(401,signed(path,source,raw))
    expect(404,http(records,token=a))
    print("PASS: signed Push, raw body/target, strict JSON/size, durable receipt/replay/idempotency, tenant authorization, versions/tombstones/conflicts, unknown/freshness, concurrency/restart, exact retries/max attempts, permanent failures, atomic outcome rollback, lease fencing/recovery, rate limits and credential expiry/context binding.")
except Exception:
    logs = docker("logs","--tail","35",CONTAINER,check=False)
    print(logs.stdout[-6000:],logs.stderr[-6000:])
    raise
finally:
    docker("rm","-f",CONTAINER,check=False)
    if created:
        assert re.fullmatch(r"docparse_push_test_[a-f0-9]{32}",DATABASE)
        docker("compose","exec","-T","postgres","dropdb","--force","-U","docparse",DATABASE)
    docker("image","rm",IMAGE,check=False)
