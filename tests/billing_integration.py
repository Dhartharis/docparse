"""Exercise sandbox billing with disposable PostgreSQL and a local Stripe double."""

from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
SUFFIX = uuid.uuid4().hex
DATABASE = "docparse_billing_test_" + SUFFIX
CONTAINER = "docparse-billing-test-" + SUFFIX
IMAGE = "docparse-billing-test:" + SUFFIX
SECRET = "whsec_public_fixture_only"
API_VERSION = "2026-10-28.endive"
PASSWORD = "Fixture-" + SUFFIX + "!aA1"


def docker(*args, check=True):
    return subprocess.run(["docker", *args], cwd=ROOT, text=True, encoding="utf-8", capture_output=True, check=check)


def sql(statement):
    return docker("compose", "exec", "-T", "postgres", "psql", "-U", "docparse", "-d", DATABASE,
                  "-At", "-v", "ON_ERROR_STOP=1", "-c", statement).stdout.strip()


class StripeDouble(BaseHTTPRequestHandler):
    customers = {}
    sessions = {}
    subscriptions = {}
    idempotency = {}
    subscription_failure = 0
    checkout_lost_ack = False
    price_live = False
    portal_url = "https://billing.stripe.com/p/session/test_fixture"
    calls = []
    lock = threading.Lock()

    def log_message(self, *args):
        pass

    def handle_request(self):
        assert self.headers["Authorization"] == "Bearer sk_test_public_fixture_only"
        assert self.headers["Stripe-Version"] == API_VERSION
        parsed = urllib.parse.urlsplit(self.path)
        body = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode()
        fields = {key: values[0] for key, values in urllib.parse.parse_qs(body).items()}
        key = self.headers.get("Idempotency-Key")
        with type(self).lock:
            type(self).calls.append((self.command, parsed.path, key, fields))
            if parsed.path == "/v1/subscriptions":
                if type(self).subscription_failure:
                    status = type(self).subscription_failure
                    self.reply(status, {})
                    return
                customer = urllib.parse.parse_qs(parsed.query)["customer"][0]
                self.reply(200, {"data": type(self).subscriptions.get(customer, []), "has_more": False})
                return
            if self.command == "GET" and parsed.path.startswith("/v1/prices/"):
                self.reply(200, {"id": parsed.path.rsplit("/", 1)[1], "livemode": type(self).price_live, "active": True,
                                 "type": "recurring", "currency": "usd", "unit_amount": 499,
                                 "recurring": {"interval": "month", "interval_count": 1}})
                return
            if self.command == "GET" and parsed.path.startswith("/v1/checkout/sessions/"):
                self.reply(200, type(self).sessions[parsed.path.rsplit("/", 1)[1]])
                return
            if key and key in type(self).idempotency:
                original, result = type(self).idempotency[key]
                assert original == fields, "Idempotent retries must use identical provider parameters."
                self.reply(200, result)
                return
            if parsed.path == "/v1/customers":
                customer = "cus_" + uuid.uuid4().hex
                result = {"id": customer, "livemode": False, "metadata": {"docparse_organization_id": fields["metadata[docparse_organization_id]"]}}
                type(self).customers[customer] = fields["metadata[docparse_organization_id]"]
            elif parsed.path == "/v1/checkout/sessions":
                assert fields["mode"] == "subscription" and fields["line_items[0][quantity]"] == "1"
                assert fields["line_items[0][price]"] in ("price_starter_fixture", "price_pro_fixture")
                assert fields["success_url"] == "http://127.0.0.1:45173/#billing=return"
                checkout_id = "cs_test_" + uuid.uuid4().hex
                result = {"id": checkout_id, "livemode": False, "mode": "subscription", "status": "open",
                          "customer": fields["customer"], "client_reference_id": fields["client_reference_id"],
                          "url": "https://checkout.stripe.com/c/pay/" + checkout_id,
                          "expires_at": int(time.time()) + 86400}
                type(self).sessions[checkout_id] = result
            elif parsed.path == "/v1/billing_portal/sessions":
                assert fields["customer"] in type(self).customers
                self.reply(200, {"livemode": False, "customer": fields["customer"], "url": type(self).portal_url})
                return
            else:
                self.reply(404, {})
                return
            assert key, "External mutations require persisted idempotency identity."
            type(self).idempotency[key] = (fields, result)
            if parsed.path == "/v1/checkout/sessions" and type(self).checkout_lost_ack:
                type(self).checkout_lost_ack = False
                self.reply(503, {})  # Provider succeeded; caller lost the acknowledgement.
                return
            self.reply(200, result)

    def do_GET(self):
        self.handle_request()

    def do_POST(self):
        self.handle_request()

    def reply(self, status, body):
        raw = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


provider = ThreadingHTTPServer(("0.0.0.0", 0), StripeDouble)
threading.Thread(target=provider.serve_forever, daemon=True).start()
BASE = ""


def http(path, data=None, token=None, headers=None, raw=None):
    payload = raw if raw is not None else (json.dumps(data).encode() if data is not None else None)
    combined = {"Content-Type": "application/json", **(headers or {})}
    if token:
        combined["Authorization"] = "Bearer " + token
    request = urllib.request.Request(BASE + path, data=payload, headers=combined)
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        content = response.read()
        return response.status, json.loads(content) if content else None


def expect(status, result):
    assert result[0] == status, (status, result)
    return result[1]


def wait_for(predicate, description):
    for _ in range(100):
        value = predicate()
        if value:
            return value
        time.sleep(0.2)
    raise AssertionError("Timed out: " + description)


def event(event_id, customer, event_type="customer.subscription.updated", **extra):
    return {"id": event_id, "type": event_type, "livemode": False, "api_version": API_VERSION,
            "data": {"object": {"customer": customer}}, **extra}


def notify(value, timestamp=None, corrupt=False):
    raw = json.dumps(value, separators=(",", ":")).encode()
    timestamp = int(time.time()) if timestamp is None else timestamp
    signature = hmac.new(SECRET.encode(), str(timestamp).encode() + b"." + raw, hashlib.sha256).hexdigest()
    if corrupt:
        signature = "0" * 64
    return http("/api/billing/stripe/webhook", raw=raw, headers={"Stripe-Signature": f"t={timestamp},v1={signature}"})


created = False
try:
    docker("build", "-t", IMAGE, "back")
    password = docker("compose", "exec", "-T", "postgres", "printenv", "POSTGRES_PASSWORD").stdout.strip()
    postgres = docker("compose", "ps", "-q", "postgres").stdout.strip()
    inspect = json.loads(docker("inspect", postgres).stdout)[0]
    network = next(iter(inspect["NetworkSettings"]["Networks"]))
    docker("compose", "exec", "-T", "postgres", "createdb", "-U", "docparse", DATABASE)
    created = True
    connection = f"Host=postgres;Port=5432;Database={DATABASE};Username=docparse;Password={password}"
    shared = ["--network", network, "-e", "ASPNETCORE_ENVIRONMENT=Development", "-e", "PostgreSQL__ConnectionString=" + connection,
              "-e", "FyrUsage__Enabled=false"]
    docker("run", "--rm", *shared, IMAGE, "--migrate")
    for name in ("a", "b"):
        docker("run", "--rm", *shared, "-e", f"LocalAdmin__Email=billing-{name}-{SUFFIX}@example.test",
               "-e", "LocalAdmin__UserName=billing-" + name, "-e", "LocalAdmin__Password=" + PASSWORD, IMAGE, "--bootstrap-local-admin")
    live = docker("run", "--rm", *shared, "-e", "Stripe__Enabled=true", "-e", "Stripe__SecretKey=sk_live_public_fixture_only",
                  "-e", "Stripe__WebhookSecret=" + SECRET, IMAGE, "--bootstrap-local-admin", check=False)
    assert live.returncode != 0 and "Live billing is not supported" in live.stderr
    blob_key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="
    with socket.socket() as reserve:
        reserve.bind(("127.0.0.1", 0))
        api_port = reserve.getsockname()[1]
    docker("run", "-d", "--name", CONTAINER, *shared, "-p", f"127.0.0.1:{api_port}:8080", "-e", "ASPNETCORE_URLS=http://+:8080",
           "-e", "AzureStorage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=" + blob_key + ";BlobEndpoint=http://azurite:10000/devstoreaccount1;",
           "-e", "Stripe__Enabled=true", "-e", "Stripe__SecretKey=sk_test_public_fixture_only", "-e", "Stripe__WebhookSecret=" + SECRET,
           "-e", f"Stripe__TestEndpoint=http://host.docker.internal:{provider.server_port}/", "-e", "Stripe__Prices__starter=price_starter_fixture",
           "-e", "Stripe__Prices__pro=price_pro_fixture", "-e", "PublicWeb__BaseUrl=http://127.0.0.1:45173", IMAGE)
    BASE = "http://127.0.0.1:" + str(api_port)

    def healthy():
        try:
            return http("/health")[0] == 200
        except (urllib.error.URLError, ConnectionError):
            return False

    wait_for(healthy, "temporary API startup")
    a = expect(200, http("/api/auth/login", {"email": "billing-a", "password": PASSWORD}))["accessToken"]
    b = expect(200, http("/api/auth/login", {"email": "billing-b", "password": PASSWORD}))["accessToken"]
    org = expect(200, http("/api/organizations/", token=a))[0]["id"]
    other_org = expect(200, http("/api/organizations/", token=b))[0]["id"]
    root = f"/api/organizations/{org}/billing"
    assert expect(200, http(root + "/", token=a))["state"]["status"] == "none"
    expect(401, http(root + "/"))
    expect(404, http(root + "/", token=b))
    sql(f'''INSERT INTO access.memberships("OrganizationId","UserId","Role") SELECT '{org}',"Id",'Viewer' FROM access."AspNetUsers" WHERE "UserName"='billing-b' ''')
    expect(404, http(root + "/checkout", {"requestId": str(uuid.uuid4()), "planKey": "starter"}, b))
    expect(400, http(root + "/checkout", {"requestId": str(uuid.uuid4()), "planKey": "unlimited"}, a))
    StripeDouble.price_live = True
    expect(503, http(root + "/checkout", {"requestId": str(uuid.uuid4()), "planKey": "starter"}, a))
    assert len(StripeDouble.customers) == 0
    StripeDouble.price_live = False
    # A lost acknowledgement must reuse both customer and checkout idempotency identities.
    StripeDouble.checkout_lost_ack = True
    request_id = str(uuid.uuid4())
    expect(503, http(root + "/checkout", {"requestId": request_id, "planKey": "starter", "returnUrl": "https://attacker.invalid", "amount": 1}, a))
    sql(f"UPDATE billing.checkout_attempts SET lease_id='{uuid.uuid4()}',lease_until=now()+interval '2 minutes' WHERE organization_id='{org}'")
    docker("restart", CONTAINER)
    wait_for(healthy, "restart recovery")
    expect(409, http(root + "/checkout", {"requestId": request_id, "planKey": "starter"}, a))
    sql(f"UPDATE billing.checkout_attempts SET lease_until=now()-interval '1 second' WHERE organization_id='{org}'")
    checkout = expect(200, http(root + "/checkout", {"requestId": request_id, "planKey": "starter"}, a))
    assert expect(200, http(root + "/", token=a))["state"]["checkoutPlanKey"] == "starter"
    with ThreadPoolExecutor(max_workers=3) as pool:
        results = list(pool.map(lambda _: http(root + "/checkout", {"requestId": str(uuid.uuid4()), "planKey": "starter"}, a), range(3)))
    assert all(result[0] in (200, 409) for result in results), results
    assert len(StripeDouble.customers) == 1 and len(StripeDouble.sessions) == 1
    assert sql(f"SELECT count(*) FROM billing.checkout_attempts WHERE organization_id='{org}'") == "1"
    assert "checkout.stripe.com" in checkout["url"]
    customer = next(iter(StripeDouble.customers))
    expect(200, http(root + "/portal", {}, a))
    StripeDouble.portal_url = "https://attacker.invalid/pay"
    expect(503, http(root + "/portal", {}, a))
    StripeDouble.portal_url = "https://billing.stripe.com/p/session/test_fixture"
    expect(409, http(f"/api/organizations/{other_org}/billing/portal", {}, b))
    payload = event("evt_valid", customer)
    expect(400, notify(payload, corrupt=True))
    expect(400, notify(payload, timestamp=int(time.time()) - 600))
    expect(400, notify(event("evt_live", customer, livemode=True)))
    expect(400, notify(event("evt_wrong_version", customer, api_version="2020-08-27")))
    expect(400, notify(event("evt_connect", customer, account="acct_other")))
    assert sql("SELECT count(*) FROM billing.webhook_inbox") == "0"
    StripeDouble.subscriptions[customer] = [{"id": "sub_fixture", "customer": customer, "livemode": False, "status": "active", "created": 100,
        "metadata": {"docparse_organization_id": org, "docparse_plan_key": "starter"},
        "items": {"data": [{"price": {"id": "price_starter_fixture"}}]}}]
    expect(200, notify(payload))
    wait_for(lambda: sql("SELECT state FROM billing.webhook_inbox WHERE event_id='evt_valid'") == "complete", "subscription reconciliation")
    assert expect(200, http(root + "/", token=a))["state"]["status"] == "active"
    # Non-identity delivery metadata may differ on a duplicate Stripe event.
    expect(200, notify({**payload, "pending_webhooks": 0}))
    assert sql("SELECT count(*) FROM billing.webhook_inbox") == "1"
    expect(409, notify(event("evt_valid", "cus_someone_else")))
    expect(409, http(root + "/checkout", {"requestId": str(uuid.uuid4()), "planKey": "pro"}, a))
    assert sql(f"SELECT count(*) FROM usage.organization_settings WHERE organization_id='{org}'") == "0", "Test billing must not grant/reset credit entitlements."
    StripeDouble.subscriptions[customer][0]["status"] = "canceled"
    expect(200, notify(event("evt_delayed_active", customer, created=1)))
    wait_for(lambda: sql("SELECT state FROM billing.webhook_inbox WHERE event_id='evt_delayed_active'") == "complete", "out-of-order reconciliation")
    assert expect(200, http(root + "/", token=a))["state"]["status"] == "canceled"
    StripeDouble.subscriptions[customer][0]["metadata"]["docparse_organization_id"] = other_org
    expect(200, notify(event("evt_foreign_metadata", customer)))
    wait_for(lambda: sql("SELECT state||':'||attempts FROM billing.webhook_inbox WHERE event_id='evt_foreign_metadata'") == "failed:1", "provider tenant metadata rejection")
    assert expect(200, http(root + "/", token=a))["state"]["status"] == "canceled"
    StripeDouble.subscriptions[customer][0]["metadata"]["docparse_organization_id"] = org
    # Control due times in the disposable DB, without sleeping through contractual delays.
    StripeDouble.subscription_failure = 503
    expect(200, notify(event("evt_retries", customer)))
    for attempt, delay in enumerate((10, 30, 120, 300, 0), start=1):
        expected_state = "failed" if attempt == 5 else "pending"
        wait_for(lambda: sql(f"SELECT state||':'||attempts FROM billing.webhook_inbox WHERE event_id='evt_retries'") == f"{expected_state}:{attempt}", "retry attempt " + str(attempt))
        assert float(sql("SELECT extract(epoch FROM due_at-updated_at) FROM billing.webhook_inbox WHERE event_id='evt_retries'")) == delay
        if attempt < 5:
            if attempt == 2:
                docker("restart", CONTAINER)
                wait_for(healthy, "retry state after restart")
            sql("UPDATE billing.webhook_inbox SET due_at=now() WHERE event_id='evt_retries'")
    StripeDouble.subscription_failure = 403
    expect(200, notify(event("evt_permanent", customer)))
    wait_for(lambda: sql("SELECT state||':'||attempts FROM billing.webhook_inbox WHERE event_id='evt_permanent'") == "failed:1", "permanent failure classification")
    StripeDouble.subscription_failure = 0
    StripeDouble.subscriptions[customer][0]["status"] = "active"
    expect(204, http(root + "/retry", {}, a))
    wait_for(lambda: expect(200, http(root + "/", token=a))["state"]["failedEvents"] == 0, "manual retry")
    wait_for(lambda: expect(200, http(root + "/", token=a))["state"]["status"] == "active", "final provider state")
    if "--browser" in sys.argv:
        env = dict(os.environ, VITE_API_PROXY_TARGET=BASE, DOCPARSE_REAL_API=BASE,
                   DOCPARSE_TEST_ADMIN_EMAIL="billing-a", DOCPARSE_TEST_ADMIN_PASSWORD=PASSWORD)
        subprocess.run(["npm.cmd" if os.name == "nt" else "npm", "--prefix", "front", "test", "--", "tests/real-billing.spec.ts", "--workers=1"], cwd=ROOT, env=env, check=True)
    print("PASS: test-only Stripe checkout/portal, tenant/Viewer boundaries, lost acknowledgement + restart, concurrent checkout, signed/deduplicated webhooks, current-state reconciliation, exact durable retry delays/max attempts, permanent errors and no credit grants.")
except Exception:
    logs = docker("logs", "--tail", "45", CONTAINER, check=False)
    print(logs.stdout[-6000:], logs.stderr[-6000:])
    raise
finally:
    provider.shutdown()
    provider.server_close()
    docker("rm", "-f", CONTAINER, check=False)
    if created:
        assert re.fullmatch(r"docparse_billing_test_[a-f0-9]{32}", DATABASE)
        docker("compose", "exec", "-T", "postgres", "dropdb", "--force", "-U", "docparse", DATABASE)
    docker("image", "rm", IMAGE, check=False)
