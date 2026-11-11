"""Connection management against disposable local PostgreSQL; no provider is contacted."""

from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
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
DATABASE = "docparse_connections_test_" + SUFFIX
CONTAINER = "docparse-connections-test-" + SUFFIX
IMAGE = "docparse-connections-test:" + SUFFIX
PASSWORD = "Fixture-" + SUFFIX + "!aA1"
BASE = ""


def docker(*args, check=True):
    return subprocess.run(["docker", *args], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", check=check)


def sql(statement):
    return docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE,
                  "-At", "-v", "ON_ERROR_STOP=1", "-c", statement).stdout.strip()


def http(path, data=None, token=None, method=None):
    body = json.dumps(data).encode() if data is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(BASE + path, data=body, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        content = response.read()
        return response.status, json.loads(content) if content else None, dict(response.headers)


def expect(status, response):
    assert response[0] == status, (status, response)
    return response[1]


def healthy():
    try:
        return http("/health")[0] == 200
    except (urllib.error.URLError, ConnectionError):
        return False


def wait_healthy():
    for _ in range(80):
        if healthy():
            return
        time.sleep(0.2)
    raise AssertionError("Temporary connection API did not start.")


def restart():
    docker("restart", CONTAINER)
    wait_healthy()


def definition(name="Inventory"):
    return {"sourceBusinessId": "business-demo", "baseUrl": "https://inventory.example.test/api", "push": True,
            "pullLive": True, "pullScheduled": True, "pushKeyDays": 90,
            "settings": {"name": name, "priceMaxAgeSeconds": 600, "stockMaxAgeSeconds": 60, "clockSkewSeconds": 0, "syncIntervalSeconds": 300}}


created = False
try:
    docker("build", "-t", IMAGE, "back")
    password = docker("compose", "exec", "-T", "postgres", "printenv", "POSTGRES_PASSWORD").stdout.strip()
    postgres = docker("compose", "ps", "-q", "postgres").stdout.strip()
    network = next(iter(json.loads(docker("inspect", postgres).stdout)[0]["NetworkSettings"]["Networks"]))
    docker("compose", "exec", "-T", "postgres", "createdb", "-U", "docparse", DATABASE)
    created = True
    connection = f"Host=postgres;Database={DATABASE};Username=docparse;Password={password}"
    shared = ["--network", network, "-e", "ASPNETCORE_ENVIRONMENT=Development", "-e", "PostgreSQL__ConnectionString=" + connection,
              "-e", "FyrUsage__Enabled=false"]
    docker("run", "--rm", *shared, IMAGE, "--migrate")
    for name in ("a", "b"):
        docker("run", "--rm", *shared, "-e", f"LocalAdmin__Email=connections-{name}-{SUFFIX}@example.test",
               "-e", "LocalAdmin__UserName=connections-" + name, "-e", "LocalAdmin__Password=" + PASSWORD, IMAGE, "--bootstrap-local-admin")
    with socket.socket() as reserve:
        reserve.bind(("127.0.0.1", 0))
        port = reserve.getsockname()[1]
    blob_key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="
    docker("run", "-d", "--name", CONTAINER, *shared, "-p", f"127.0.0.1:{port}:8080", "-e", "ASPNETCORE_URLS=http://+:8080",
           "-e", "Integrations__AllowedProviderOrigins__0=https://inventory.example.test", "-e", "PublicWeb__BaseUrl=http://127.0.0.1:45173",
           "-e", "AzureStorage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=" + blob_key + ";BlobEndpoint=http://azurite:10000/devstoreaccount1;", IMAGE)
    BASE = "http://127.0.0.1:" + str(port)
    wait_healthy()
    a = expect(200, http("/api/auth/login", {"email": "connections-a", "password": PASSWORD}))["accessToken"]
    b = expect(200, http("/api/auth/login", {"email": "connections-b", "password": PASSWORD}))["accessToken"]
    org = expect(200, http("/api/organizations/", token=a))[0]["id"]
    other_org = expect(200, http("/api/organizations/", token=b))[0]["id"]
    root = f"/api/organizations/{org}/connections"
    other_root = f"/api/organizations/{other_org}/connections"
    expect(401, http(root + "/"))
    expect(404, http(root + "/", token=b))
    sql(f'''INSERT INTO access.memberships("OrganizationId","UserId","Role") SELECT '{org}',"Id",'Viewer' FROM access."AspNetUsers" WHERE "UserName"='connections-b' ''')
    expect(404, http(root + "/", token=b))
    expect(404, http(root + "/", {"requestId": str(uuid.uuid4()), "definition": definition()}, b))
    initial = expect(200, http(root + "/", token=a))
    assert initial == {"connections": [], "providerOriginsConfigured": True}
    for change in ({"baseUrl": "https://attacker.invalid/"}, {"baseUrl": "https://user:secret@inventory.example.test/"},
                   {"baseUrl": "https://inventory.example.test/?token=secret"}, {"baseUrl": "http://inventory.example.test/"},
                   {"sourceBusinessId": "other business"}, {"settings": None}, {"pushKeyDays": 0}):
        invalid = {**definition(), **change}
        expect(400, http(root + "/", {"requestId": str(uuid.uuid4()), "definition": invalid}, a))
    missing_policy = definition()
    del missing_policy["settings"]["clockSkewSeconds"]
    expect(400, http(root + "/", {"requestId": str(uuid.uuid4()), "definition": missing_policy}, a))
    assert sql("SELECT count(*) FROM integrations.connections") == "0"
    restart()
    request_id = str(uuid.uuid4())
    original = definition("  Inventory  ")
    response = http(root + "/", {"requestId": request_id, "definition": original}, a)
    first = expect(200, response)
    assert response[2].get("Cache-Control") == "no-store"
    source = first["connection"]
    source_id = source["id"]
    first_secret = first["pushSecretHex"]
    assert re.fullmatch(r"[0-9a-f]{64}", first_secret)
    assert source["baseUrl"] == "https://inventory.example.test/api/" and source["settings"]["name"] == "Inventory"
    assert source["state"] == "needs_credentials" and source["pullKeyId"] is None
    replay = expect(200, http(root + "/", {"requestId": request_id, "definition": original}, a))
    assert replay["connection"]["id"] == source_id and replay["pushSecretHex"] is None
    expect(409, http(root + "/", {"requestId": request_id, "definition": definition("Different")}, a))
    concurrent_request = {"requestId": str(uuid.uuid4()), "definition": definition("Concurrent source")}
    with ThreadPoolExecutor(max_workers=5) as pool:
        replies = list(pool.map(lambda _: expect(200, http(root + "/", concurrent_request, a)), range(5)))
    assert len({reply["connection"]["id"] for reply in replies}) == 1
    assert sum(reply["pushSecretHex"] is not None for reply in replies) == 1
    assert sql("SELECT count(*) FROM integrations.connections") == "2"
    listing = expect(200, http(root + "/", token=a))
    assert first_secret not in json.dumps(listing) and "protectedSecret" not in json.dumps(listing)
    encrypted = sql(f"SELECT protected_secret FROM integrations.credentials WHERE connection_id='{source_id}' AND direction='push' AND revoked_at IS NULL")
    assert encrypted != first_secret and first_secret not in encrypted
    # The database itself must reject a credential linked across organizations.
    cross = docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE, "-v", "ON_ERROR_STOP=1", "-c",
        f"INSERT INTO integrations.credentials(id,organization_id,connection_id,direction,key_id,protected_secret) VALUES('{uuid.uuid4()}','{other_org}','{source_id}','pull','cross-tenant-fixture','dummy')", check=False)
    assert cross.returncode != 0 and "foreign key constraint" in cross.stderr
    restart()
    other = expect(200, http(other_root + "/", {"requestId": request_id, "definition": original}, b))
    assert other["connection"]["id"] != source_id
    assert len(expect(200, http(other_root + "/", token=b))["connections"]) == 1
    path = root + "/" + source_id
    pull_secret = (uuid.uuid4().hex + uuid.uuid4().hex).upper()
    expect(404, http(path + "/pull-key", {"revision": source["revision"], "keyId": "provider-key", "secretHex": pull_secret}, b, "PUT"))
    expect(400, http(path + "/pull-key", {"revision": source["revision"], "keyId": "provider-key", "secretHex": "dp_chat_key"}, a, "PUT"))
    source = expect(200, http(path + "/pull-key", {"revision": source["revision"], "keyId": "provider-key", "secretHex": pull_secret}, a, "PUT"))
    assert source["state"] == "configured" and source["pullKeyId"] == "provider-key"
    assert pull_secret not in json.dumps(source)
    push_revision = source["revision"]
    with ThreadPoolExecutor(max_workers=2) as pool:
        rotations = list(pool.map(lambda _: http(path + "/push-key", {"revision": push_revision, "days": 30}, a), range(2)))
    assert sorted(reply[0] for reply in rotations) == [200, 409]
    rotated = next(reply[1] for reply in rotations if reply[0] == 200)
    assert rotated["secretHex"] != first_secret
    source = rotated["connection"]
    assert sql(f"SELECT count(*) FROM integrations.credentials WHERE connection_id='{source_id}' AND direction='push' AND revoked_at IS NULL") == "1"
    assert sql(f"SELECT count(*) FROM integrations.credentials WHERE connection_id='{source_id}' AND revoked_at IS NOT NULL AND protected_secret IS NOT NULL") == "0"
    expect(409, http(path, {"revision": push_revision, "settings": source["settings"]}, a, "PUT"))
    invalid_settings = {**source["settings"], "stockMaxAgeSeconds": 0}
    expect(400, http(path, {"revision": source["revision"], "settings": invalid_settings}, a, "PUT"))
    changed = {**source["settings"], "name": "Renamed inventory", "stockMaxAgeSeconds": 45}
    source = expect(200, http(path, {"revision": source["revision"], "settings": changed, "sourceBusinessId": "cannot-change-owner"}, a, "PUT"))
    assert source["sourceBusinessId"] == "business-demo" and source["settings"]["stockMaxAgeSeconds"] == 45
    sql(f"UPDATE integrations.credentials SET expires_at=now()-interval '1 second' WHERE connection_id='{source_id}' AND direction='push' AND revoked_at IS NULL")
    fresh = next(item for item in expect(200, http(root + "/", token=a))["connections"] if item["id"] == source_id)
    assert fresh["state"] == "needs_credentials"
    restart()
    before = source["revision"]
    with ThreadPoolExecutor(max_workers=2) as pool:
        race = [pool.submit(http, path + "/disconnect", {"revision": before}, a),
                pool.submit(http, path + "/pull-key", {"revision": before, "keyId": "replacement-key", "secretHex": pull_secret}, a, "PUT")]
        results = [future.result() for future in race]
    assert results[0][0] in (204, 409) and results[1][0] in (200, 404)
    if results[0][0] == 409:
        current = next(item for item in expect(200, http(root + "/", token=a))["connections"] if item["id"] == source_id)
        expect(204, http(path + "/disconnect", {"revision": current["revision"]}, a))
    expect(204, http(path + "/disconnect", {"revision": before}, a))
    expect(404, http(path + "/push-key", {"revision": before, "days": 90}, a))
    assert sql(f"SELECT count(*) FROM integrations.credentials WHERE connection_id='{source_id}' AND (revoked_at IS NULL OR protected_secret IS NOT NULL)") == "0"
    old_replay = expect(200, http(root + "/", {"requestId": request_id, "definition": original}, a))
    assert old_replay["connection"]["state"] == "disconnected" and old_replay["pushSecretHex"] is None
    assert sql(f"SELECT count(*) FROM integrations.connections WHERE organization_id='{org}'") == "2"
    restart()
    persisted = expect(200, http(root + "/", token=a))
    assert len(persisted["connections"]) == 2 and first_secret not in json.dumps(persisted) and pull_secret not in json.dumps(persisted)
    if "--browser" in sys.argv:
        env = dict(os.environ, VITE_API_PROXY_TARGET=BASE, DOCPARSE_REAL_API=BASE,
                   DOCPARSE_TEST_ADMIN_EMAIL="connections-a", DOCPARSE_TEST_ADMIN_PASSWORD=PASSWORD)
        subprocess.run(["npm.cmd" if os.name == "nt" else "npm", "--prefix", "front", "test", "--", "tests/real-connections.spec.ts", "--workers=1"], cwd=ROOT, env=env, check=True)
    print("PASS: connection ownership/Viewer denial, policy validation, idempotent concurrent creation, secret non-disclosure/encryption, credential rotation/replacement, optimistic concurrency, tenant FKs, expiry, disconnect races and restart persistence. No provider traffic.")
except Exception:
    logs = docker("logs", "--tail", "40", CONTAINER, check=False)
    print(logs.stdout[-6000:], logs.stderr[-6000:])
    raise
finally:
    docker("rm", "-f", CONTAINER, check=False)
    if created:
        assert re.fullmatch(r"docparse_connections_test_[a-f0-9]{32}", DATABASE)
        docker("compose", "exec", "-T", "postgres", "dropdb", "--force", "-U", "docparse", DATABASE)
    docker("image", "rm", IMAGE, check=False)
