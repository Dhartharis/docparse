"""Run security and schema checks against an isolated local Docker database/API.

Requires `docker compose up -d --build` first. Uses only loopback HTTP, local
PostgreSQL and Mailpit. Real OpenAI is replaced by a local HTTP double.
"""
import html
import hashlib
import base64
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import csv
import io
from pathlib import Path
import re
import socket
import subprocess
import time
import threading
import urllib.error
import urllib.parse
import urllib.request
import uuid
import os
import sys

ROOT = Path(__file__).resolve().parents[1]
SUFFIX = uuid.uuid4().hex
DATABASE = f"docparse_test_{SUFFIX}"
CONTAINER = f"docparse-test-{SUFFIX}"
MAIL = "http://127.0.0.1:8025"
PASSWORD = f"Test-{uuid.uuid4().hex}!aA1"


class FakeOpenAI(BaseHTTPRequestHandler):
    calls = 0
    extraction_attempts = {}
    blocked = threading.Event()
    release = threading.Event()

    def log_message(self, *args):
        pass

    def do_POST(self):
        type(self).calls += 1
        if self.headers.get("Transfer-Encoding") == "chunked":
            body = bytearray()
            while True:
                size = int(self.rfile.readline().split(b";", 1)[0], 16)
                if size == 0:
                    self.rfile.readline()
                    break
                body.extend(self.rfile.read(size))
                self.rfile.read(2)
        else:
            body = self.rfile.read(int(self.headers["Content-Length"]))
        payload = json.loads(body)
        if self.path == "/api/embed":
            inputs = payload["input"] if isinstance(payload["input"],list) else [payload["input"]]
            result = {"embeddings": [[0.1]*1024 for _ in inputs]}
        elif self.path == "/api/chat":
            question = json.loads(payload["messages"][1]["content"])["question"]
            context = json.loads(payload["messages"][1]["content"])
            assert payload["think"] is False and payload["format"]["required"] == ["answer"]
            assert len({doc["id"] for doc in context["documents"]}) == len(context["documents"])
            if question == "VALID_HISTORY": assert context["conversation"] == [{"Question":"Prior question","Answer":"Prior answer"}]
            content = json.dumps({"answer":"Respuesta final de prueba."})
            if question == "MALFORMED_FINAL": content = "private analysis without a final answer"
            if question == "TAGGED_FINAL": content = json.dumps({"answer":"<think>PRIVATE_CHAIN</think> answer"})
            if question == "EXTRA_FINAL": content = json.dumps({"answer":"answer", "analysis":"PRIVATE_CHAIN"})
            result = {"done":True,"done_reason":"length" if question == "TRUNCATED_FINAL" else "stop", "message":{"content":content,"thinking":"PRIVATE_CHAIN"}}
        elif self.path == "/v1/embeddings":
            result = {"data": [{"embedding": [0.1] * (3 if "BAD_VECTOR" in payload["input"] else 1536)}]}
        elif self.path == "/v1/chat/completions":
            content = payload["messages"][0]["content"]
            if isinstance(content, list):
                file = content[0]["file"]["file_data"].split(",", 1)[1]
                text = base64.b64decode(file).decode()
                attempt = type(self).extraction_attempts.get(text, 0) + 1
                type(self).extraction_attempts[text] = attempt
                if text == "ALWAYS_RETRY" or (text == "RETRY_ONCE" and attempt == 1):
                    self.send_response(503); self.end_headers(); return
                if text == "LEASE_FENCE":
                    if attempt == 1:
                        type(self).blocked.set(); type(self).release.wait(45)
                        text = "STALE_ATTEMPT"
                    else: text = "CURRENT_ATTEMPT"
                answer = "invalid-json" if text == "INVALID" else json.dumps({"type": "Unknown", "fields": {"content": text, "formula": "=SUM(1,1)"}})
            else:
                answer = payload["messages"][1]["content"]
            result = {"choices": [{"message": {"content": answer}}]}
        else:
            self.send_error(404)
            return
        data = json.dumps(result).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        try: self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError): pass


provider = ThreadingHTTPServer(("0.0.0.0", 0), FakeOpenAI)
threading.Thread(target=provider.serve_forever, daemon=True).start()
with socket.socket() as sock:
    sock.bind(("127.0.0.1", 0))
    PORT = sock.getsockname()[1]
BASE = f"http://127.0.0.1:{PORT}"


def docker(*args, success=True):
    result = subprocess.run(["docker", *args], cwd=ROOT, text=True, capture_output=True)
    if success and result.returncode:
        raise RuntimeError("Local Docker operation failed: " + result.stderr[-1200:])
    return result


def http(path, data=None, token=None, method=None, base=BASE, host=None, extra_headers=None):
    headers = {"Content-Type": "application/json", **(extra_headers or {})}
    if host:
        headers["Host"] = host
    if token:
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(base + path, data=None if data is None else json.dumps(data).encode(),
                                     headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        content = response.read().decode()
        try:
            content = json.loads(content)
        except json.JSONDecodeError:
            pass
        return response.status, content


def expect(expected, result):
    status, body = result
    assert status == expected, f"Expected HTTP {expected}, got {status}: {str(body)[:200]}"
    return body


def email_for(address, subject, exclude=None):
    query = urllib.parse.urlencode({"query": f"to:{address}"})
    for _ in range(30):
        data = expect(200, http("/api/v1/search?" + query, base=MAIL))
        for message in data.get("messages", []):
            if message["Subject"] == subject:
                detail = expect(200, http("/api/v1/message/" + message["ID"], base=MAIL))
                body = html.unescape(detail["HTML"])
                if exclude is None or exclude not in body:
                    return body
        time.sleep(0.1)
    raise AssertionError("Expected local email was not delivered.")


def fixture_invitation(address, token, expired=False):
    # Isolated fixture only; no public bootstrap bypass exists on the running API.
    token_hash = hashlib.sha256(token.encode()).hexdigest().upper()
    expires = "now() - interval '1 day'" if expired else "now() + interval '1 day'"
    assert re.fullmatch(r"[a-zA-Z0-9@.\-]+", address)
    docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE, "-v", "ON_ERROR_STOP=1", "-c",
           f'''INSERT INTO access.invitations ("Id", "OrganizationId", "Email", "Role", "TokenHash", "ExpiresAt") VALUES ('{uuid.uuid4()}', '{fixture_org}', '{address.upper()}', 'Viewer', '{token_hash}', {expires})''')


def account(label, check_unconfirmed=False):
    address = f"{label}-{SUFFIX}@example.test"
    token = uuid.uuid4().hex + uuid.uuid4().hex
    fixture_invitation(address, token)
    payload = {"email": address, "userName": label + SUFFIX[:8], "password": PASSWORD, "token": token}
    if check_unconfirmed:
        expect(403, http("/api/auth/register", {"email": address, "password": PASSWORD}))
        expect(403, http("/API/AUTH/REGISTER/", {"email": address, "password": PASSWORD}))
        expect(400, http("/api/invitations/register", dict(payload, email="wrong@example.test")))
        expect(400, http("/api/invitations/register", dict(payload, password="1234")))
        expect(400, http("/api/invitations/register", dict(payload, token="0" * 64)))
        with ThreadPoolExecutor(max_workers=2) as pool:
            statuses = list(pool.map(lambda _: http("/api/invitations/register", payload)[0], range(2)))
        assert sorted(statuses) == [200, 400], "An invitation can register exactly one account."
    else:
        expect(200, http("/api/invitations/register", payload))
    expect(400, http("/api/invitations/register", payload))
    if check_unconfirmed:
        expect(401, http("/api/auth/login", {"email": address, "password": PASSWORD}))
    body = email_for(address, "Confirm your DocParse email")
    link = re.search(r'href="([^"]+)"', body).group(1)
    assert link.startswith(BASE + "/api/auth/confirmEmail?")
    path = link.removeprefix(BASE)
    expect(200, http(path))
    tokens = expect(200, http("/api/auth/login", {"email": address, "password": PASSWORD}))
    return address, tokens


def upload(path, text, token, expected=200, wait=True):
    boundary = uuid.uuid4().hex
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"test.pdf\"\r\n"
            f"Content-Type: application/pdf\r\n\r\n{text}\r\n--{boundary}--\r\n").encode()
    request = urllib.request.Request(BASE + path, data=body, headers={
        "Authorization": "Bearer " + token, "Content-Type": "multipart/form-data; boundary=" + boundary})
    try:
        response = urllib.request.urlopen(request, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        result = json.loads(response.read())
        expect(202, (response.status, result))
    if not wait: return result
    endpoint = path.removesuffix("/extract") + "/" + result["id"]
    for _ in range(300):
        detail = expect(200, http(endpoint, token=token))
        state = detail["document"]["processingStatus"]
        if state in ("ready", "failed"):
            if expected == 200: assert state == "ready", detail
            elif expected == 503: assert state == "failed" and detail["document"]["processingError"] == "processing_unavailable", detail
            elif expected == 502: assert state == "failed" and detail["document"]["processingError"] == "invalid_result", detail
            return {"id": result["id"]}
        time.sleep(.1)
    raise AssertionError("Background processing did not finish.")


def upload_logo(path, data, token, expected=200):
    boundary = uuid.uuid4().hex
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="logo.png"\r\nContent-Type: image/png\r\n\r\n').encode() + data + f"\r\n--{boundary}--\r\n".encode()
    request = urllib.request.Request(BASE + path, data=body, headers={"Authorization": "Bearer " + token, "Content-Type": "multipart/form-data; boundary=" + boundary})
    try:
        response = urllib.request.urlopen(request, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read().decode()
        return expect(expected, (response.status, json.loads(raw) if raw else {}))


created = False
try:
    db_password = docker("compose", "exec", "-T", "postgres", "printenv", "POSTGRES_PASSWORD").stdout.strip()
    connection = f"Host=postgres;Database={DATABASE};Username=docparse;Password={db_password}"
    docker("compose", "exec", "-T", "postgres", "createdb", "-U", "docparse", DATABASE)
    created = True
    with ThreadPoolExecutor(max_workers=2) as pool:
        list(pool.map(lambda _: docker("compose", "run", "--rm", "--no-deps", "-e", "FyrUsage__Enabled=false", "-e", "PostgreSQL__ConnectionString=" + connection, "migrate"), range(2)))
    count = docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE,
                   "-Atc", "SELECT count(*) FROM schema_migrations").stdout.strip()
    assert count == str(len(list((ROOT / "back/Infrastructure/PostgreSQL/Migrations").glob("*.sql")))), "Migrations did not apply exactly once."
    docker("compose", "run", "-d", "--no-deps", "--name", CONTAINER,
           "-p", f"127.0.0.1:{PORT}:8080", "-e", "FyrUsage__Enabled=false", "-e", "PostgreSQL__ConnectionString=" + connection,
           "-e", "OpenAI__ApiKey=", "-e", "PublicWeb__BaseUrl=http://127.0.0.1:45173",
           "-e", "AI__Provider=OpenAI", "-e", "PublicApi__BaseUrl=" + BASE,
           "-e", f"OpenAI__Endpoint=http://host.docker.internal:{provider.server_port}/v1/", "api")
    for _ in range(60):
        try:
            if http("/health")[0] == 200:
                break
        except (urllib.error.URLError, ConnectionError):
            pass
        time.sleep(0.2)
    else:
        raise AssertionError("Test API did not become ready.")

    fixture_org = str(uuid.uuid4())
    docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE, "-c",
           f'''INSERT INTO access.organizations ("Id", "Name", "CreatedAt") VALUES ('{fixture_org}', 'Registration fixture', now())''')
    expired_token = uuid.uuid4().hex + uuid.uuid4().hex
    expired_email = "expired-" + SUFFIX + "@example.test"
    fixture_invitation(expired_email, expired_token, expired=True)
    expect(400, http("/api/invitations/register", {"email": expired_email, "userName": "expired", "password": PASSWORD, "token": expired_token}))
    expect(401, http("/api/organizations/"))
    # Legacy global endpoints must never expose document data.
    expect(404, http("/api/document/all"))
    a_email, a = account("admin-a", True)
    expect(200, http("/api/auth/login", {"email": "admin-a" + SUFFIX[:8], "password": PASSWORD}))
    # Start a fresh rate-limit window after adversarial registration tests, without relaxing the API policy.
    docker("restart", CONTAINER)
    for _ in range(60):
        try:
            if http("/health")[0] == 200:
                break
        except (urllib.error.URLError, ConnectionError):
            pass
        time.sleep(0.2)
    b_email, b = account("admin-b")
    viewer_email, viewer = account("viewer")
    at, bt, vt = a["accessToken"], b["accessToken"], viewer["accessToken"]
    expect(400, http("/api/auth/login?useCookies=true", {"email": a_email, "password": PASSWORD}))
    org_a = expect(201, http("/api/organizations/", {"name": "Organization A"}, at))["id"]
    org_b = expect(201, http("/api/organizations/", {"name": "Organization B"}, bt))["id"]
    pa = f"/api/organizations/{org_a}"
    pb = f"/api/organizations/{org_b}"
    ka = expect(201, http(pa + "/knowledge-bases/", {"name": "Knowledge A"}, at))["id"]
    kb = expect(201, http(pb + "/knowledge-bases/", {"name": "Knowledge B"}, bt))["id"]
    expect(404, http(pa + f"/knowledge-bases/{ka}", {"name": "Denied"}, bt, "PATCH"))
    expect(400, http(pa + f"/knowledge-bases/{ka}", {"name": " "}, at, "PATCH"))
    expect(204, http(pa + f"/knowledge-bases/{ka}", {"name": "Renamed knowledge A"}, at, "PATCH"))
    assert expect(200, http(pa + "/knowledge-bases/", token=at))[0]["name"] == "Renamed knowledge A"
    bot_a = expect(201, http(pa + "/bots/", {"name": "Bot A"}, at))["id"]
    bot_b = expect(201, http(pb + "/bots/", {"name": "Bot B"}, bt))["id"]
    empty_bot = expect(201, http(pa + "/bots/", {"name": "Empty bot"}, at))["id"]
    hidden_bot = expect(201, http(pa + "/bots/", {"name": "Unassigned bot"}, at))["id"]
    expect(204, http(pa + f"/bots/{bot_a}/knowledge-bases", {"knowledgeBaseIds": [ka]}, at, "PUT"))
    assert expect(200, http(pa + f"/bots/{bot_a}/knowledge-bases", token=at))["knowledgeBaseIds"] == [ka]
    expect(404, http(pa + f"/bots/{bot_a}/knowledge-bases", token=bt))
    expect(404, http(pa + f"/bots/{bot_a}", {"name": "Denied"}, bt, "PATCH"))
    expect(400, http(pa + f"/bots/{bot_a}", {"name": " "}, at, "PATCH"))
    expect(400, http(pa + f"/bots/{bot_a}", {"name": "Bot A", "accentColor": "url(https://example.test)"}, at, "PATCH"))
    expect(204, http(pa + f"/bots/{bot_a}", {"name": "Bot A", "accentColor": "#2457c5"}, at, "PATCH"))
    expect(204, http(pa + f"/bots/{bot_a}", {"name": "Bot A", "welcomeMessage": "Welcome"}, at, "PATCH"))
    expect(404, http(pa + f"/bots/{bot_a}/knowledge-bases", {"knowledgeBaseIds": [kb]}, at, "PUT"))
    expect(404, http(pa + "/knowledge-bases/", token=bt))
    expect(404, http(pa + f"/knowledge-bases/{ka}/documents/all", token=bt))
    expect(404, http(pb + f"/bots/{bot_b}/messages", {"question": "Question"}, at))

    # The database must reject a cross-organization link even if an adapter is bypassed.
    invalid = docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE,
                     "-v", "ON_ERROR_STOP=1", "-c",
                     f"INSERT INTO bots.bot_knowledge_bases VALUES ('{org_a}', '{bot_a}', '{kb}')", success=False)
    assert invalid.returncode != 0 and "foreign key" in invalid.stderr.lower()
    unscoped = docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE,
                      "-v", "ON_ERROR_STOP=1", "-c",
                      f"INSERT INTO knowledge.documents (id, type, fields, created_at) VALUES ('{uuid.uuid4()}', 'Unknown', '{{}}', now())", success=False)
    assert unscoped.returncode != 0 and "documents_require_scope" in unscoped.stderr

    expect(201, http(pa + "/invitations", {"email": viewer_email, "role": "Viewer"}, at))
    invite = re.search(r"[A-F0-9]{64}", email_for(viewer_email, "DocParse organization invitation")).group(0)
    expect(404, http("/api/invitations/accept", {"token": invite}, bt))
    expect(204, http("/api/invitations/accept", {"token": invite}, vt))
    expect(204, http("/api/invitations/accept", {"token": invite}, vt))
    expect(404, http(pa + "/knowledge-bases/", token=vt))
    expect(404, http(pa + f"/knowledge-bases/{ka}", {"name": "Denied"}, vt, "PATCH"))
    expect(404, http(pa + f"/knowledge-bases/{ka}/documents/all", token=vt))
    expect(404, http(pa + "/members", token=vt))
    expect(404, http(pa + "/bots/", {"name": "Unauthorized"}, vt))
    expect(404, http(pa + f"/bots/{bot_a}/knowledge-bases", token=vt))
    expect(404, http(pa + f"/bots/{bot_a}", {"name": "Denied"}, vt, "PATCH"))
    assert expect(200, http(pa + "/bots/", token=vt)) == []
    members = expect(200, http(pa + "/members", token=at))
    viewer_id = next(member["id"] for member in members if member["email"] == viewer_email)
    access_path = pa + f"/bots/{bot_a}/access/"
    expect(404, http(access_path, token=vt))
    expect(404, http(access_path, token=bt))
    expect(400, http(access_path, {"email": "invalid", "allBots": False}, at))
    expect(409, http(access_path, {"email": a_email, "allBots": False}, at))
    pending_email = f"pending-{SUFFIX}@example.test"
    pending_result = expect(200, http(access_path, {"email": pending_email, "allBots": False}, at))
    assert pending_result["pending"] is True
    assert expect(200, http(access_path, token=at))[0]["pending"] is True
    assert expect(200, http(pa + "/bots/", token=vt)) == []
    expect(200, http(access_path, {"email": viewer_email.upper(), "allBots": False}, at))
    assert {bot["id"] for bot in expect(200, http(pa + "/bots/", token=vt))} == {bot_a}
    expect(204, http(access_path + "?" + urllib.parse.urlencode({"email": viewer_email}), token=at, method="DELETE"))
    result = expect(200, http(access_path, {"email": viewer_email, "allBots": True}, at))
    assert result["affectedBots"] == 3 and result["pending"] is False
    future_bot = expect(201, http(pa + "/bots/", {"name": "Future bot"}, at))["id"]
    assert {bot["id"] for bot in expect(200, http(pa + "/bots/", token=vt))} == {bot_a, empty_bot, hidden_bot}
    for assigned_bot in [bot_a, empty_bot, hidden_bot]:
        expect(204, http(pa + f"/bots/{assigned_bot}/access/?" + urllib.parse.urlencode({"email": viewer_email}), token=at, method="DELETE"))
    expect(204, http(access_path + "?" + urllib.parse.urlencode({"email": pending_email}), token=at, method="DELETE"))
    assert expect(200, http(access_path, token=at)) == []
    b_members = expect(200, http(pb + "/members", token=bt))
    foreign_user_id = next(member["id"] for member in b_members if member["email"] == b_email)
    expect(404, http(pa + f"/bots/{empty_bot}/messages", {"question": "Question"}, vt))
    expect(404, http(pa + f"/bots/{bot_a}/messages", {"question": "Question"}, vt))
    expect(404, http(pa + f"/bots/{empty_bot}/viewers", {"userIds": [viewer_id]}, vt, "PUT"))
    expect(404, http(pa + f"/bots/{empty_bot}/viewers", token=vt))
    expect(404, http(pa + f"/bots/{empty_bot}/viewers", token=bt))
    expect(404, http(pa + f"/bots/{empty_bot}/viewers", {"userIds": [viewer_id, foreign_user_id]}, at, "PUT"))
    invalid_viewer = docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE,
                            "-v", "ON_ERROR_STOP=1", "-c",
                            f"INSERT INTO bots.bot_viewers VALUES ('{org_a}', '{bot_a}', '{foreign_user_id}')", success=False)
    assert invalid_viewer.returncode != 0 and "bot_viewer_membership" in invalid_viewer.stderr
    expect(204, http(pa + f"/bots/{empty_bot}/viewers", {"userIds": [viewer_id, viewer_id]}, at, "PUT"))
    assert expect(200, http(pa + f"/bots/{empty_bot}/viewers", token=at))["userIds"] == [viewer_id]
    assert {bot["id"] for bot in expect(200, http(pa + "/bots/", token=vt))} == {empty_bot}
    answer = expect(200, http(pa + f"/bots/{empty_bot}/messages", {"question": "Question"}, vt))
    assert answer["sources"] == []
    expect(204, http(pa + f"/bots/{bot_a}/viewers", {"userIds": [viewer_id]}, at, "PUT"))
    png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/mwoAAAAASUVORK5CYII=")
    logo_path = pa + f"/bots/{bot_a}/logo"
    expect(401, http(logo_path))
    upload_logo(logo_path, b"<svg><script>bad</script></svg>" * 2, at, expected=400)
    upload_logo(logo_path, png[:33], at, expected=400)
    upload_logo(logo_path, png + b"<svg/>", at, expected=400)
    upload_logo(logo_path, png, vt, expected=404)
    upload_logo(logo_path, png, bt, expected=404)
    oversized_dimensions = bytearray(png); oversized_dimensions[16:20] = (4096).to_bytes(4, "big")
    upload_logo(logo_path, oversized_dimensions, at, expected=400)
    upload_logo(logo_path, png, at)
    expect(404, http(logo_path, token=bt))
    for logo_user in [at, vt]:
        logo_request = urllib.request.Request(BASE + logo_path, headers={"Authorization": "Bearer " + logo_user})
        with urllib.request.urlopen(logo_request) as response:
            assert response.read() == png and response.headers["Content-Type"] == "image/png"
            assert response.headers["Cache-Control"] == "no-store"
    configured_bot = next(bot for bot in expect(200, http(pa + "/bots/", token=at)) if bot["id"] == bot_a)
    assert configured_bot["accentColor"] == "#2457c5" and configured_bot["logoVersion"]
    original_a = 'A_PRIVATE_CONTENT, "quoted"\nnext'
    first_failed=upload(pa + f"/knowledge-bases/{ka}/documents/extract", original_a, at, expected=503)["id"]
    pending_documents = expect(200, http(pa + f"/knowledge-bases/{ka}/documents/all", token=at))
    assert len(pending_documents) == 1 and pending_documents[0]["processingStatus"] == "failed"
    assert pending_documents[0]["hasOriginal"] is True
    assert FakeOpenAI.calls == 0, "Unconfigured extraction must not call the provider."
    docker("rm", "-f", CONTAINER)
    docker("compose", "run", "-d", "--no-deps", "--name", CONTAINER,
           "-p", f"127.0.0.1:{PORT}:8080", "-e", "FyrUsage__Enabled=false", "-e", "PostgreSQL__ConnectionString=" + connection,
           "-e", "PublicWeb__BaseUrl=http://127.0.0.1:45173", "-e", "OpenAI__ApiKey=integration-dummy", "-e", "AI__Provider=OpenAI", "-e", "PublicApi__BaseUrl=" + BASE,
           "-e", f"OpenAI__Endpoint=http://host.docker.internal:{provider.server_port}/v1/", "api")
    for _ in range(60):
        try:
            if http("/health")[0] == 200:
                break
        except (urllib.error.URLError, ConnectionError):
            pass
        time.sleep(0.2)
    duplicate_failed=upload(pa + f"/knowledge-bases/{ka}/documents/extract", original_a, at, expected=503)["id"]
    assert duplicate_failed==first_failed
    expect(202,http(pa+f"/knowledge-bases/{ka}/documents/{first_failed}/retry",{},at))
    for _ in range(300):
        if expect(200,http(pa+f"/knowledge-bases/{ka}/documents/{first_failed}",token=at))["document"]["processingStatus"]=="ready":break
        time.sleep(.1)
    else:raise AssertionError("Retried original did not become ready.")
    doc_a=upload(pa + f"/knowledge-bases/{ka}/documents/extract", original_a, at)["id"]
    assert doc_a==first_failed
    assert len(expect(200,http(pa+f"/knowledge-bases/{ka}/documents/all",token=at)))==1
    doc_b = upload(pb + f"/knowledge-bases/{kb}/documents/extract", "B_PRIVATE_CONTENT", bt)["id"]
    summary = expect(200, http(pa + f"/knowledge-bases/{ka}/documents/all", token=at))[0]
    assert summary["id"] == doc_a and summary["fileName"] == "test.pdf" and summary["hasOriginal"] is True
    assert "fields" not in summary and "originalBlobName" not in summary and "organizationId" not in summary
    detail = expect(200, http(pa + f"/knowledge-bases/{ka}/documents/{doc_a}", token=at))
    assert detail["document"] == summary and detail["fields"]["content"] == original_a
    assert "originalBlobName" not in detail["document"]
    original = urllib.request.Request(BASE + pa + f"/knowledge-bases/{ka}/documents/{doc_a}/file", headers={"Authorization": "Bearer " + at})
    with urllib.request.urlopen(original) as response:
        assert response.read() == original_a.encode()
    exported = expect(200, http(pa + f"/knowledge-bases/{ka}/documents/{doc_a}/export/csv", token=at))
    cells = {row["Field"]: row["Value"] for row in csv.DictReader(io.StringIO(exported))}
    assert cells["content"] == original_a and cells["formula"].startswith("'=SUM")
    expect(404, http(pb + f"/knowledge-bases/{kb}/documents/{doc_a}", token=bt))
    expect(404, http(pa + f"/knowledge-bases/{ka}/documents/{doc_a}/file", token=vt))
    expect(404, http(pa + f"/knowledge-bases/{ka}/documents/{doc_a}/export/csv", token=vt))
    answer = expect(200, http(pa + f"/bots/{bot_a}/messages", {"question": "Question"}, vt))
    assert "A_PRIVATE_CONTENT" in answer["answer"] and "B_PRIVATE_CONTENT" not in answer["answer"]
    assert answer["sources"] == [{"documentId": doc_a, "title": "test.pdf", "pageNumber": None}]
    channels = pa + f"/bots/{bot_a}/channels"
    expect(404, http(channels + "/keys", token=vt))
    expect(404, http(channels + "/keys", {"name":"Denied"}, bt))
    expect(400, http(channels + "/keys", {"name":"bad","expiresInDays":366}, at))
    issued = expect(200, http(channels + "/keys", {"name":"Integration","expiresInDays":30}, at))
    secret = issued["secret"]; key_id = issued["key"]["id"]
    assert secret not in json.dumps(expect(200,http(channels + "/keys",token=at)))
    external = f"/api/v1/organizations/{org_a}/bots/{bot_a}/messages"
    key_headers = {"X-DocParse-Key":secret}
    expect(401,http(external,{"question":"Question"}))
    external_answer=expect(200,http(external,{"question":"Question"},extra_headers=key_headers))
    assert "A_PRIVATE_CONTENT" in external_answer["answer"] and "B_PRIVATE_CONTENT" not in external_answer["answer"]
    expect(401,http(f"/api/v1/organizations/{org_b}/bots/{bot_a}/messages",{"question":"Question"},extra_headers=key_headers))
    expect(401,http(pa+f"/knowledge-bases/{ka}/documents/{doc_a}/file",token=secret))
    rotated=expect(200,http(channels+"/keys",{"name":"Rotated","rotateKeyId":key_id},at))
    expect(401,http(external,{"question":"Question"},extra_headers=key_headers))
    expect(200,http(external,{"question":"Question"},extra_headers={"X-DocParse-Key":rotated["secret"]}))
    expect(204,http(channels+"/keys/"+rotated["key"]["id"],token=at,method="DELETE"))
    expect(401,http(external,{"question":"Question"},extra_headers={"X-DocParse-Key":rotated["secret"]}))
    rate_key=expect(200,http(pa+f"/bots/{empty_bot}/channels/keys",{"name":"Rate test"},at))["secret"]
    with ThreadPoolExecutor(max_workers=8) as pool:
        statuses=list(pool.map(lambda _:http(f"/api/v1/organizations/{org_a}/bots/{empty_bot}/messages",{"question":"Question"},extra_headers={"X-DocParse-Key":rate_key})[0],range(21)))
    assert statuses.count(200)==20 and statuses.count(429)==1,statuses
    expect(400,http(channels+"/embed",{"enabled":True,"origins":["https://site.test/path"]},at,"PUT"))
    expect(400,http(channels+"/embed",{"enabled":True,"origins":["http://site.test"]},at,"PUT"))
    expect(400,http(channels+"/embed",{"enabled":True,"origins":[]},at,"PUT"))
    expect(204,http(channels+"/embed",{"enabled":True,"origins":["https://site.test"]},at,"PUT"))
    frame=f"/api/embed/{bot_a}/frame"
    expect(404,http(frame))
    expect(404,http(frame,extra_headers={"Referer":"https://evil.test/page"}))
    frame_html=expect(200,http(frame,extra_headers={"Referer":"https://site.test/page"}))
    widget=json.loads(re.search(r"window.__DOCPARSE_EMBED__=(.*?);</script>",frame_html).group(1))
    widget_answer=expect(200,http(f"/api/embed/{bot_a}/messages",{"question":"Question"},extra_headers={"X-DocParse-Session":widget["token"]}))
    assert "A_PRIVATE_CONTENT" in widget_answer["answer"] and "B_PRIVATE_CONTENT" not in widget_answer["answer"]
    expect(204,http(channels+"/embed",{"enabled":False,"origins":["https://site.test"]},at,"PUT"))
    expect(401,http(f"/api/embed/{bot_a}/messages",{"question":"Question"},extra_headers={"X-DocParse-Session":widget["token"]}))
    estimate_model={"monthlyPriceUsd":10,"targetCostFraction":0.4,"freeBudgetUsd":0,"scenarios":[{"name":"small","queries":100,"inputTokensPerQuery":1000,"outputTokensPerQuery":100,"queryEmbeddingTokens":100,"documentEmbeddingTokens":10000,"inputUsdPerMillion":1,"outputUsdPerMillion":2,"embeddingUsdPerMillion":0.1,"retrievalUsdPerQuery":0,"storageUsd":1,"egressUsd":0,"processingUsd":0.5}]}
    estimate=expect(200,http(pa+"/usage/estimate",estimate_model,at))[0]
    assert abs(estimate["queryCostUsd"]-.00121)<1e-10 and abs(estimate["totalCostUsd"]-1.622)<1e-10,estimate
    assert estimate["maximumQueriesWithinBudget"]==2065 and estimate["withinBudget"]
    expect(404,http(pa+"/usage/estimate",estimate_model,vt))
    before_denied = FakeOpenAI.calls
    expect(204, http(pa + f"/bots/{bot_a}/viewers", {"userIds": []}, at, "PUT"))
    expect(404, http(pa + f"/bots/{bot_a}/messages", {"question": "Question"}, vt))
    expect(404, http(logo_path, token=vt))
    assert FakeOpenAI.calls == before_denied, "Denied bot requests must not call AI."
    assert {bot["id"] for bot in expect(200, http(pa + "/bots/", token=vt))} == {empty_bot}
    expect(204, http(pa + f"/bots/{bot_a}/viewers", {"userIds": [viewer_id]}, at, "PUT"))
    upload(pa + f"/knowledge-bases/{ka}/documents/extract", "INVALID", at, expected=502)
    upload(pa + f"/knowledge-bases/{ka}/documents/extract", "BAD_VECTOR", at, expected=502)
    documents = expect(200, http(pa + f"/knowledge-bases/{ka}/documents/all", token=at))
    assert len(documents) == 3 and sum(item["processingStatus"] == "ready" for item in documents) == 1
    assert sum(item["processingStatus"] == "failed" for item in documents) == 2
    assert FakeOpenAI.calls >= 6
    members = expect(200, http(pa + "/members", token=at))
    admin_id = next(member["id"] for member in members if member["email"] == a_email)
    viewer_id = next(member["id"] for member in members if member["email"] == viewer_email)
    def job_state(document_id):
        return expect(200, http(pa + f"/knowledge-bases/{ka}/documents/{document_id}", token=at))["document"]

    def wait_job(document_id, predicate):
        for _ in range(150):
            state = job_state(document_id)
            if predicate(state): return state
            time.sleep(.1)
        raise AssertionError("Job did not reach expected durable state: " + str(state))

    def sql(statement):
        return docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE, "-Atc", statement).stdout.strip()

    retry_doc = upload(pa + f"/knowledge-bases/{ka}/documents/extract", "RETRY_ONCE", at, wait=False)["id"]
    wait_job(retry_doc, lambda row: row["processingStatus"] == "queued" and row["processingError"] == "provider_error")
    assert sql(f"SELECT processing_attempts, extract(epoch FROM next_attempt_at-processing_updated_at)::integer FROM knowledge.documents WHERE id='{retry_doc}'") == "1|10"
    # Retry metadata must survive a backend restart; advance only this fixture's due time.
    docker("restart", CONTAINER)
    for _ in range(60):
        try:
            if http("/health")[0] == 200: break
        except (urllib.error.URLError, ConnectionError): pass
        time.sleep(.2)
    sql(f"UPDATE knowledge.documents SET next_attempt_at=now() WHERE id='{retry_doc}'")
    wait_job(retry_doc, lambda row: row["processingStatus"] == "ready")
    assert FakeOpenAI.extraction_attempts["RETRY_ONCE"] == 2

    exhausted = upload(pa + f"/knowledge-bases/{ka}/documents/extract", "ALWAYS_RETRY", at, wait=False)["id"]
    for attempt, delay in [(1,10),(2,30)]:
        wait_job(exhausted, lambda row: row["processingStatus"] == "queued" and row["processingError"] == "provider_error")
        assert sql(f"SELECT processing_attempts, extract(epoch FROM next_attempt_at-processing_updated_at)::integer FROM knowledge.documents WHERE id='{exhausted}'") == f"{attempt}|{delay}"
        sql(f"UPDATE knowledge.documents SET next_attempt_at=now() WHERE id='{exhausted}'")
        if attempt == 1:
            for _ in range(60):
                if sql(f"SELECT processing_attempts FROM knowledge.documents WHERE id='{exhausted}'") == "2": break
                time.sleep(.1)
    wait_job(exhausted, lambda row: row["processingStatus"] == "failed")
    assert FakeOpenAI.extraction_attempts["ALWAYS_RETRY"] == 3
    expect(404, http(pa + f"/knowledge-bases/{ka}/documents/{exhausted}/retry", {}, vt))
    expect(404, http(pa + f"/knowledge-bases/{ka}/documents/{exhausted}/retry", {}, bt))

    fenced = upload(pa + f"/knowledge-bases/{ka}/documents/extract", "LEASE_FENCE", at, wait=False)["id"]
    assert FakeOpenAI.blocked.wait(10)
    sql(f"UPDATE knowledge.documents SET lease_until=now()-interval '1 second' WHERE id='{fenced}'")
    extra_worker = CONTAINER + "-worker"
    try:
        docker("compose", "run", "-d", "--no-deps", "--name", extra_worker,
            "-e", "FyrUsage__Enabled=false", "-e", "PostgreSQL__ConnectionString=" + connection, "-e", "AI__Provider=OpenAI", "-e", "PublicWeb__BaseUrl=http://127.0.0.1:45173", "-e", "OpenAI__ApiKey=integration-dummy",
            "-e", f"OpenAI__Endpoint=http://host.docker.internal:{provider.server_port}/v1/", "api")
        wait_job(fenced, lambda row: row["processingStatus"] == "ready")
        docker("rm", "-f", extra_worker)
        sentinel = upload(pa + f"/knowledge-bases/{ka}/documents/extract", "AFTER_STALE_ATTEMPT", at, wait=False)["id"]
        FakeOpenAI.release.set()
        # Only the original worker remains; finishing this job proves it finished its stale attempt first.
        wait_job(sentinel, lambda row: row["processingStatus"] == "ready")
        assert expect(200, http(pa + f"/knowledge-bases/{ka}/documents/{fenced}", token=at))["fields"]["content"] == "CURRENT_ATTEMPT"
        assert sql(f"SELECT count(*) FROM knowledge.document_embeddings WHERE document_id='{fenced}'") == "1"
        expect(409, http(pa + f"/knowledge-bases/{ka}/documents/{fenced}/retry", {}, at))
    finally:
        FakeOpenAI.release.set()
        docker("rm", "-f", extra_worker, success=False)

    # Validate the local answer adapter independently of model prose.
    answer_api = CONTAINER + "-answers"
    with socket.socket() as free:
        free.bind(("127.0.0.1",0)); answer_port = free.getsockname()[1]
    answer_base = f"http://127.0.0.1:{answer_port}"
    try:
        sql(f"INSERT INTO knowledge.document_embeddings(id,document_id,content,embedding,created_at,embedding_profile,page_number) VALUES ('{uuid.uuid4()}','{doc_a}','Adapter fixture',array_fill(0.1::real,ARRAY[1024])::vector,now(),'ollama:qwen3-embedding:0.6b:1024:v1',1)")
        docker("compose", "run", "-d", "--no-deps", "--name", answer_api, "-p", f"127.0.0.1:{answer_port}:8080",
            "-e", "FyrUsage__Enabled=false", "-e", "PostgreSQL__ConnectionString=" + connection, "-e", "AI__Provider=Ollama",
            "-e", f"Ollama__Endpoint=http://host.docker.internal:{provider.server_port}/", "api")
        for _ in range(60):
            try:
                if http("/health",base=answer_base)[0] == 200: break
            except (urllib.error.URLError,ConnectionError): pass
            time.sleep(.2)
        result = expect(200,http(pa+f"/bots/{bot_a}/messages",{"question":"VALID_FINAL"},at,base=answer_base))
        assert result["answer"] == "Respuesta final de prueba." and "PRIVATE_CHAIN" not in json.dumps(result)
        expect(200,http(pa+f"/bots/{bot_a}/messages",{"question":"VALID_HISTORY","history":[{"question":"Prior question","answer":"Prior answer"}]},at,base=answer_base))
        for bad_history in [[{"question":"q","answer":"a"}]*5,[{"question":"q"*401,"answer":"a"}],[{"question":"q","answer":"a"*801}],[{"question":"","answer":"a"}],[None]]:
            expect(400,http(pa+f"/bots/{bot_a}/messages",{"question":"Question","history":bad_history},at,base=answer_base))
        expect(404,http(pa+f"/bots/{bot_a}/messages",{"question":"Question","history":[{"question":"q","answer":"a"}]},bt,base=answer_base))
        for question in ["TRUNCATED_FINAL","MALFORMED_FINAL","TAGGED_FINAL","EXTRA_FINAL"]:
            result = expect(502,http(pa+f"/bots/{bot_a}/messages",{"question":question},at,base=answer_base))
            assert "PRIVATE_CHAIN" not in json.dumps(result)
    finally:
        docker("rm","-f",answer_api,success=False)

    expect(409, http(pa + f"/members/{admin_id}", {"role": "Viewer"}, at, "PATCH"))
    expect(204, http(pa + f"/members/{viewer_id}", {"role": "Admin"}, at, "PATCH"))
    expect(200, http(pa + "/knowledge-bases/", token=vt))
    assert hidden_bot in {bot["id"] for bot in expect(200, http(pa + "/bots/", token=vt))}
    expect(204, http(pa + f"/members/{viewer_id}", {"role": "Viewer"}, at, "PATCH"))
    expect(404, http(pa + "/knowledge-bases/", token=vt))
    assert hidden_bot not in {bot["id"] for bot in expect(200, http(pa + "/bots/", token=vt))}
    expect(201, http(pa + "/invitations", {"email": b_email, "role": "Admin"}, at))
    invite_b = re.search(r"[A-F0-9]{64}", email_for(b_email, "DocParse organization invitation")).group(0)
    expect(204, http("/api/invitations/accept", {"token": invite_b}, bt))
    members = expect(200, http(pa + "/members", token=at))
    admin_b_id = next(member["id"] for member in members if member["email"] == b_email)
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda args: http(pa + f"/members/{args[0]}", {"role": "Viewer"}, args[1], "PATCH")[0],
                                [(admin_id, at), (admin_b_id, bt)]))
    assert sorted(results) == [204, 409], "Concurrent demotions must preserve at least one Admin."
    remaining_admin = at if results[0] == 409 else bt
    expect(204, http(pa + f"/members/{viewer_id}", token=remaining_admin, method="DELETE"))
    expect(404, http(pa + "/bots/", token=vt))
    assert expect(200, http(pa + f"/bots/{bot_a}/viewers", token=remaining_admin))["userIds"] == []
    expect(201, http(pa + "/invitations", {"email": viewer_email, "role": "Viewer"}, remaining_admin))
    rejoin = re.search(r"[A-F0-9]{64}", email_for(viewer_email, "DocParse organization invitation", exclude=invite)).group(0)
    assert rejoin != invite
    expect(404, http("/api/invitations/accept", {"token": invite}, vt))
    expect(204, http("/api/invitations/accept", {"token": rejoin}, vt))
    assert expect(200, http(pa + "/bots/", token=vt)) == []
    expect(404, http(pa + f"/bots/{bot_a}/messages", {"question": "Question"}, vt))
    docker("restart", CONTAINER)
    for _ in range(60):
        try:
            if http("/health")[0] == 200:
                break
        except (urllib.error.URLError, ConnectionError):
            pass
        time.sleep(0.2)
    expect(200, http("/api/auth/me", token=at))
    expect(200, http("/api/auth/refresh", {"refreshToken": a["refreshToken"]}))
    expect(204, http("/api/auth/logout-all", {}, at))
    expect(403, http("/api/auth/me", token=at))
    expect(401, http("/api/auth/refresh", {"refreshToken": a["refreshToken"]}))

    expect(200, http("/api/auth/forgotPassword", {"email": viewer_email}))
    reset_mail = email_for(viewer_email, "Reset your DocParse password")
    reset_code = re.search(r"Reset code: ([^<\s]+)", reset_mail).group(1)
    new_password = PASSWORD + "New"
    expect(200, http("/api/auth/resetPassword", {"email": viewer_email, "resetCode": reset_code, "newPassword": new_password}))
    expect(403, http("/api/auth/me", token=vt))
    expect(401, http("/api/auth/login", {"email": viewer_email, "password": PASSWORD}))
    new_viewer = expect(200, http("/api/auth/login", {"email": viewer_email, "password": new_password}))
    for _ in range(5):
        expect(401, http("/api/auth/login", {"email": b_email, "password": "wrong-password"}))
    expect(401, http("/api/auth/login", {"email": b_email, "password": PASSWORD}))
    expect(403, http("/api/auth/me", token=bt))
    for _ in range(21):
        if http("/api/auth/me", token=new_viewer["accessToken"])[0] == 429:
            break
    else:
        raise AssertionError("Authentication rate limit was not enforced.")
    docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE,
           "-c", "UPDATE schema_migrations SET checksum = 'tampered' WHERE name LIKE '%001_initial.sql'")
    changed = docker("compose", "run", "--rm", "--no-deps", "-e", "FyrUsage__Enabled=false", "-e", "PostgreSQL__ConnectionString=" + connection, "migrate", success=False)
    assert changed.returncode != 0 and "Applied migration has changed" in changed.stderr
    if "--browser" in sys.argv:
        docker("restart", CONTAINER)
        for _ in range(60):
            try:
                if http("/health")[0] == 200:
                    break
            except (urllib.error.URLError, ConnectionError):
                pass
            time.sleep(0.2)
        ui_admin_email = "ui-admin-" + SUFFIX + "@example.test"
        docker("exec", "-e", "LocalAdmin__Email=" + ui_admin_email, "-e", "LocalAdmin__UserName=ui-admin",
               "-e", "LocalAdmin__Password=" + PASSWORD, CONTAINER, "dotnet", "DocParse.dll", "--bootstrap-local-admin")
        # Isolated fixture: an existing Viewer can be assigned by the UI Admin without expanding invitation scope.
        docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE, "-v", "ON_ERROR_STOP=1", "-c",
               f'''INSERT INTO access.memberships ("OrganizationId", "UserId", "Role")
                   SELECT member."OrganizationId", viewer."Id", 'Viewer' FROM access.memberships member
                   JOIN access."AspNetUsers" admin ON admin."Id" = member."UserId"
                   CROSS JOIN access."AspNetUsers" viewer
                   WHERE admin."Email" = '{ui_admin_email}' AND viewer."Email" = '{viewer_email}' ''')
        environment = dict(os.environ, DOCPARSE_REAL_API=BASE, VITE_API_PROXY_TARGET=BASE,
                           DOCPARSE_TEST_ADMIN_EMAIL=ui_admin_email, DOCPARSE_TEST_ADMIN_PASSWORD=PASSWORD,
                           DOCPARSE_TEST_VIEWER_EMAIL=viewer_email, DOCPARSE_TEST_VIEWER_PASSWORD=new_password)
        for browser_suite in ["tests/real-access.spec.ts", "tests/real-knowledge.spec.ts", "tests/real-channels.spec.ts", "tests/real-owner.spec.ts"]:
            # Keep each journey within the real rate limit without weakening its production defaults.
            docker("restart", CONTAINER)
            for _ in range(60):
                try:
                    if http("/health")[0] == 200:
                        break
                except (urllib.error.URLError, ConnectionError):
                    pass
                time.sleep(0.2)
            subprocess.run(["npm.cmd" if os.name == "nt" else "npm", "--prefix", "front", "test", "--", browser_suite, "--workers=1"],
                           cwd=ROOT, env=environment, check=True)
    print("PASS: migrations, login/reset/revocation, restart persistence, tenant isolation, invitations, roles, concurrent last-Admin protection, scoped RAG, Blob roundtrip, provider failures and FK constraints.")
except Exception:
    logs = docker("logs", "--tail", "70", CONTAINER, success=False)
    print(logs.stdout[-8000:], logs.stderr[-8000:])
    raise
finally:
    provider.shutdown()
    provider.server_close()
    docker("rm", "-f", CONTAINER, success=False)
    if created:
        assert re.fullmatch(r"docparse_test_[a-f0-9]{32}", DATABASE)
        docker("compose", "exec", "-T", "postgres", "dropdb", "--force", "-U", "docparse", DATABASE)
