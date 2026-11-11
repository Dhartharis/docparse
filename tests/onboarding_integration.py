"""Public owner onboarding, invitation generations and durable local SMTP."""
from concurrent.futures import ThreadPoolExecutor
import html
import json
import re
import socket
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SUFFIX = uuid.uuid4().hex
DATABASE = "docparse_access_" + SUFFIX
CONTAINER = "docparse-access-" + SUFFIX
with socket.socket() as sock:
    sock.bind(("127.0.0.1", 0))
    PORT = sock.getsockname()[1]
BASE = f"http://127.0.0.1:{PORT}"
PASSWORD = "Owner-" + SUFFIX + "!aA1"
MAIL = "http://127.0.0.1:8025"


def docker(*args, check=True):
    return subprocess.run(["docker", *args], cwd=ROOT, capture_output=True,
                          text=True, encoding="utf-8", check=check).stdout


def request(path, data=None, token=None, method=None, expected=200, base=BASE):
    req = urllib.request.Request(base + path, data=json.dumps(data).encode() if data is not None else None,
                                 headers={"Content-Type": "application/json",
                                          **({"Authorization": "Bearer " + token} if token else {})},
                                 method=method)
    try:
        response = urllib.request.urlopen(req, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read()
        assert response.status == expected, (response.status, expected, raw[:160])
        return (json.loads(raw) if "json" in response.headers.get("Content-Type", "") else raw.decode()) if raw else None


def sql(query):
    return docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse",
                  "-d", DATABASE, "-Atc", query).strip()


def wait(predicate):
    for _ in range(100):
        if predicate():
            return
        time.sleep(.1)
    raise AssertionError("Expected durable state was not reached.")


def ready():
    def healthy():
        try:
            return request("/health")["status"] == "ok"
        except (AssertionError, urllib.error.URLError, ConnectionError):
            return False
    wait(healthy)


def restart():
    docker("restart", CONTAINER)
    ready()


def start(host="mailpit"):
    docker("compose", "run", "-d", "--no-deps", "--name", CONTAINER,
           "-p", f"127.0.0.1:{PORT}:8080", "-e", "FyrUsage__Enabled=false",
           "-e", "PostgreSQL__ConnectionString=" + connection, "-e", "PublicApi__BaseUrl=" + BASE,
           "-e", "PublicWeb__BaseUrl=http://127.0.0.1:45173", "-e", "Email__Host=" + host, "api")
    ready()


def mail(email, subject):
    body = None

    def found():
        nonlocal body
        messages = request("/api/v1/search?query=" + urllib.parse.quote("to:" + email),
                           base=MAIL).get("messages", [])
        row = next((m for m in messages if m["Subject"] == subject), None)
        if not row:
            return False
        body = html.unescape(request("/api/v1/message/" + row["ID"], base=MAIL)["HTML"])
        return True
    wait(found)
    return body


def confirm(email):
    body = mail(email, "Confirm your DocParse email")
    link = re.search('href="([^"]+)"', body).group(1)
    assert link.startswith(BASE + "/api/auth/confirmEmail?")
    request(link.removeprefix(BASE))


def owner(email, name):
    request("/api/onboarding/register", {"email": email, "userName": name, "password": PASSWORD,
                                        "organizationName": name + " business"}, expected=202)
    request("/api/auth/login", {"email": email, "password": PASSWORD}, expected=401)
    confirm(email)
    token = request("/api/auth/login", {"email": email, "password": PASSWORD})["accessToken"]
    org = request("/api/organizations/", token=token)[0]
    assert org["role"] == "Admin"
    return token, org["id"]


created = False
try:
    db_password = docker("compose", "exec", "-T", "postgres", "printenv", "POSTGRES_PASSWORD").strip()
    connection = f"Host=postgres;Database={DATABASE};Username=docparse;Password={db_password}"
    docker("compose", "exec", "-T", "postgres", "createdb", "-U", "docparse", DATABASE)
    created = True
    docker("compose", "run", "--rm", "--no-deps", "-e", "PostgreSQL__ConnectionString=" + connection, "migrate")
    start()
    a_email = "owner-" + SUFFIX + "@example.test"
    at, org = owner(a_email, "owner-" + SUFFIX[:10])
    root = f"/api/organizations/{org}"
    request("/api/onboarding/register", {"email": "invalid-" + SUFFIX + "@example.test",
                                        "userName": "weak", "password": "1234",
                                        "organizationName": "Invalid"}, expected=400)
    request(f"/api/organizations/{uuid.uuid4()}/bots/", token=at, expected=404)
    creation = str(uuid.uuid4())
    first = request("/api/organizations/", {"name": "Second business", "requestId": creation}, at, expected=201)
    again = request("/api/organizations/", {"name": "Second business", "requestId": creation}, at, expected=201)
    assert first["id"] == again["id"]
    bot = request(root + "/bots/", {"name": "Assigned"}, at, expected=201)["id"]
    request(root + "/bots/", {"name": "Not assigned"}, at, expected=201)
    viewer = "viewer-" + SUFFIX + "@example.test"
    request(root + f"/bots/{bot}/access/", {"email": viewer, "allBots": False}, at)
    invitation = re.search("#invite=([A-F0-9]{64})", mail(viewer, "DocParse organization invitation")).group(1)
    preview = request("/api/invitations/preview", {"token": invitation})
    assert preview["email"].lower() == viewer and not preview["existingAccount"]
    request("/api/invitations/register", {"email": "wrong@example.test", "userName": "wrong",
                                         "password": PASSWORD, "token": invitation}, expected=400)
    request("/api/invitations/register", {"email": viewer, "userName": "viewer-" + SUFFIX[:10],
                                         "password": PASSWORD, "token": invitation})
    confirm(viewer)
    vt = request("/api/auth/login", {"email": viewer, "password": PASSWORD})["accessToken"]
    assert {b["id"] for b in request(root + "/bots/", token=vt)} == {bot}
    request(root + "/bots/trash", token=vt, expected=404)
    request(root + "/knowledge-bases/trash", token=vt, expected=404)
    request(root + f"/bots/{bot}/channels/keys", {"name": "denied"}, vt, expected=404)
    request(root + f"/bots/{bot}/access/?email=" + urllib.parse.quote(viewer),
            token=at, method="DELETE", expected=204)
    assert request(root + "/bots/", token=vt) == []

    restart()
    b_email = "existing-" + SUFFIX + "@example.test"
    bt, _ = owner(b_email, "existing-" + SUFFIX[:8])
    snapshot = {b["id"] for b in request(root + "/bots/", token=at)}
    request(root + f"/bots/{bot}/access/", {"email": b_email, "allBots": True}, at)
    request(root + "/bots/", {"name": "Future"}, at, expected=201)
    existing_token = re.search("#invite=([A-F0-9]{64})",
                               mail(b_email, "DocParse organization invitation")).group(1)
    assert request("/api/invitations/preview", {"token": existing_token})["existingAccount"]
    request("/api/invitations/accept", {"token": existing_token}, bt, expected=204)
    assert {b["id"] for b in request(root + "/bots/", token=bt)} == snapshot
    request(root + "/knowledge-bases/", token=bt, expected=404)

    stale = "stale-" + SUFFIX + "@example.test"
    request(root + f"/bots/{bot}/access/", {"email": stale, "allBots": False}, at)
    stale_token = re.search("#invite=([A-F0-9]{64})", mail(stale, "DocParse organization invitation")).group(1)
    pending = next(i for i in request(root + "/invitations", token=at) if i["email"].lower() == stale)
    request(root + "/invitations/" + pending["id"], token=at, method="DELETE", expected=204)
    request("/api/invitations/preview", {"token": stale_token}, expected=400)
    restart()
    st, _ = owner(stale, "stale-" + SUFFIX[:8])
    request(root + "/invitations", {"email": stale, "role": "Viewer"}, at, expected=201)
    latest = None

    def current_invite():
        global latest
        rows = request("/api/v1/search?query=" + urllib.parse.quote("to:" + stale), base=MAIL)["messages"]
        for row in rows:
            if row["Subject"] == "DocParse organization invitation":
                body = html.unescape(request("/api/v1/message/" + row["ID"], base=MAIL)["HTML"])
                value = re.search("#invite=([A-F0-9]{64})", body).group(1)
                if value != stale_token:
                    latest = value
                    return True
        return False
    wait(current_invite)
    request("/api/invitations/accept", {"token": latest}, st, expected=204)
    assert request(root + "/bots/", token=st) == []
    request(root + f"/bots/{bot}/access/", {"email": stale, "allBots": False}, at)
    assert {b["id"] for b in request(root + "/bots/", token=st)} == {bot}

    docker("rm", "-f", CONTAINER)
    start("127.0.0.1")
    failed = "mail-failure-" + SUFFIX + "@example.test"
    request("/api/onboarding/register", {"email": failed, "userName": "mail-" + SUFFIX[:10],
                                        "password": PASSWORD, "organizationName": "Mail durability"}, expected=202)
    job = sql('SELECT "Id" FROM access.mail_outbox ORDER BY "CreatedAt" DESC LIMIT 1')
    for attempt, delay in [(1, 10), (2, 30), (3, 120), (4, 300), (5, 300)]:
        wait(lambda: sql(f'''SELECT "Attempts"::text||':'||"State" FROM access.mail_outbox
                             WHERE "Id"='{job}' ''') == f"{attempt}:" + ("failed" if attempt == 5 else "pending"))
        assert float(sql(f'''SELECT extract(epoch FROM("NextAttemptAt"-"UpdatedAt"))
                            FROM access.mail_outbox WHERE "Id"='{job}' ''')) == delay
        if attempt == 1:
            timestamp = sql(f'''SELECT "NextAttemptAt" FROM access.mail_outbox WHERE "Id"='{job}' ''')
            restart()
            assert sql(f'''SELECT "NextAttemptAt" FROM access.mail_outbox WHERE "Id"='{job}' ''') == timestamp
        if attempt < 5:
            sql(f'''UPDATE access.mail_outbox SET "NextAttemptAt"=now()-interval '1 second'
                    WHERE "Id"='{job}' ''')
    docker("rm", "-f", CONTAINER)
    start()
    request("/api/auth/resendConfirmationEmail", {"email": failed})
    confirm(failed)
    assert request("/api/auth/login", {"email": failed, "password": PASSWORD})["accessToken"]
    print("PASS: public verified owner onboarding, organization idempotency, scoped Viewer invites, "
          "existing-account acceptance, generation revocation, 10/30/120/300 SMTP retries and restart recovery.")
except Exception:
    print(docker("logs", "--tail", "30", CONTAINER, check=False))
    raise
finally:
    docker("rm", "-f", CONTAINER, check=False)
    if created:
        docker("compose", "exec", "-T", "postgres", "dropdb", "--force", "-U", "docparse", DATABASE)
