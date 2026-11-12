"""Google OAuth/Drive fixtures; no Google account, credentials, billing or external network."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime,timedelta,timezone
from http.server import ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit,parse_qs
import base64
import hashlib
import json
import os
import subprocess
import sys
import threading
import uuid
import urllib.request
import time
from scheduled_sync_integration import ScheduledProvider
from catalog_integration import workbook

ROOT=Path(__file__).resolve().parents[1]
class GoogleProvider(ScheduledProvider):
    grants={};token_calls=0;refreshes=0;changed=0
    blocked=threading.Event();release=threading.Event()
    def answer(self,status,value):
        raw=json.dumps(value).encode();self.send_response(status);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(raw)));self.end_headers();self.wfile.write(raw)
    def do_POST(self):
        if self.path=='/fixture-google':
            command=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            if command['action']=='grant':type(self).grants[command['code']]=command['challenge']
            if command['action']=='reset_block':type(self).blocked.clear();type(self).release.clear()
            if command['action']=='release':type(self).release.set()
            return self.answer(200,{'calls':type(self).token_calls,'blocked':type(self).blocked.is_set()})
        if self.path!='/token':return super().do_POST()
        form=parse_qs(self.rfile.read(int(self.headers['Content-Length'])).decode());cls=type(self)
        assert form['client_id']==['fixture-client'] and form['client_secret']==['fixture-secret']
        if form['grant_type']==['authorization_code']:
            cls.token_calls+=1;code=form['code'][0]
            if code=='blocked':cls.blocked.set();assert cls.release.wait(8)
            expected=cls.grants.get(code)
            verifier=form.get('code_verifier',[''])[0]
            challenge=base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip('=')
            if expected!=challenge:return self.answer(400,{'error':'invalid_grant'})
            del cls.grants[code]
        else:
            assert form['grant_type']==['refresh_token'] and form['refresh_token']==['fixture-refresh'];cls.refreshes+=1
        result={'access_token':'fixture-access','refresh_token':'fixture-refresh','expires_in':3600,'refresh_token_expires_in':86400,'token_type':'Bearer','scope':'https://www.googleapis.com/auth/drive.file'}
        if form.get('code')==['denied-scope']:result['scope']='openid'
        if form.get('code')==['missing-refresh']:del result['refresh_token']
        self.answer(200,result)
    def do_GET(self):
        path=urlsplit(self.path).path;cls=type(self)
        if path.startswith('/drive/') or path.startswith('/sheets/'):
            assert self.headers.get('Authorization')=='Bearer fixture-access'
            if path.startswith('/drive/files/'):
                file=path.split('/')[-1]
                if 'alt=media' in self.path:
                    if file=='redirect':self.send_response(302);self.send_header('Location','http://127.0.0.1:1/secret');self.end_headers();return
                    raw=workbook();self.send_response(200);self.send_header('Content-Length',str(len(raw)));self.end_headers();self.wfile.write(raw);return
                if file=='changing':cls.changed+=1
                if file=='read-blocked':cls.blocked.set();assert cls.release.wait(8)
                self.answer(200,{'id':file,'name':'Inventory','mimeType':'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' if file in ['excel','redirect'] else 'application/vnd.google-apps.spreadsheet','version':str(cls.changed) if file=='changing' else '1','modifiedTime':(datetime.now(timezone.utc)-timedelta(seconds=10)).replace(microsecond=0).isoformat().replace('+00:00','Z') if file=='changing' else '2026-11-11T00:00:00Z','trashed':False,'isAppAuthorized':file!='unselected','capabilities':{'canDownload':file!='denied'},'size':'1024'});return
            if '/values/' in path:
                instant=(datetime.now(timezone.utc)-timedelta(seconds=5)).isoformat().replace('+00:00','Z')
                self.answer(200,{'range':"'Inventory'!A1:E2",'majorDimension':'ROWS','values':[['ID','Name','Price','Stock','Updated'],['0001','Milk','999999999999999999.123456',0,instant]]});return
            self.answer(200,{'sheets':[{'properties':{'title':'Inventory'}}]});return
        return super().do_GET()

def exercise(h):
    h['docker']('restart',h['CONTAINER']);h['healthy']();http,expect,sql=h['http'],h['expect'],h['sql'];a,b=h['a'],h['b'];org=h['org'];root=f'/api/organizations/{org}/google-drive'
    def control(action,**values):
        with urllib.request.urlopen(urllib.request.Request(os.environ['DOCPARSE_GOOGLE_LOCAL_ORIGIN']+'/fixture-google',data=json.dumps({'action':action,**values}).encode(),headers={'Content-Type':'application/json'}),timeout=10)as response:return json.load(response)
    expect(401,http(root+'/'));expect(404,http(root+'/',token=b));assert expect(200,http(root+'/',token=a))=={'enabled':True,'accounts':[]}
    def authorize(code=None):
        start=expect(200,http(root+'/authorize',{},a));params=parse_qs(urlsplit(start['authorizationUrl']).query)
        assert params['scope']==['https://www.googleapis.com/auth/drive.file'] and params['access_type']==['offline'] and params['code_challenge_method']==['S256']
        code=code or uuid.uuid4().hex;control('grant',code=code,challenge=params['code_challenge'][0]);return start,code
    start,code=authorize();state=start['state'];account=start['id'];body={'state':state,'code':code}
    expect(404,http(root+'/complete',body,b));expect(400,http(root+'/complete',{'state':state+'0','code':code},a))
    assert expect(200,http(root+'/complete',body,a))=={'id':account}
    calls=control('counts')['calls'];assert expect(200,http(root+'/complete',body,a))=={'id':account} and calls==control('counts')['calls']
    accounts=expect(200,http(root+'/',token=a));assert len(accounts['accounts'])==1 and 'fixture-'not in json.dumps(accounts)
    assert 'fixture-access' not in sql("SELECT protected_tokens FROM integrations.google_accounts") and 'fixture-refresh' not in sql("SELECT protected_tokens FROM integrations.google_accounts")
    assert sql("SELECT encode(state_hash,'hex') FROM integrations.google_authorizations")==hashlib.sha256(state.encode()).hexdigest()
    expect(200,http(root+f'/{account}/picker',{},a));h['docker']('restart',h['CONTAINER']);h['healthy']();expect(200,http(root+f'/{account}/picker',{},a))
    # Same organization, different Admin: credentials stay bound to their original customer user.
    other_user=sql("SELECT \"Id\" FROM access.\"AspNetUsers\" WHERE \"UserName\"='push-b'")
    sql(f"""INSERT INTO access.memberships("OrganizationId","UserId","Role") VALUES('{org}','{other_user}','Admin') ON CONFLICT("OrganizationId","UserId") DO UPDATE SET "Role"='Admin'""")
    expect(400,http(root+f'/{account}/picker',{},b));assert expect(200,http(root+'/',token=b))['accounts']==[]
    user=sql("SELECT \"Id\" FROM access.\"AspNetUsers\" WHERE \"UserName\"='push-a'")
    before=sql('SELECT count(*) FROM operational_data.records')
    for file in ['sheet','excel']:
        inspected=expect(200,http(root+f'/{account}/inspect',{'fileId':file,'headerRow':1},a));assert inspected['inspection']['headers']==['ID','Name','Price','Stock','Updated']
    mapping={'headerRow':1,'columns':{'externalId':'ID','name':'Name','productId':None,'sku':None,'presentation':None,'unit':None,'price':'Price','currency':None,'available':'Stock','reserved':None,'active':None,'observedAt':'Updated'},'defaults':{'productIdFromExternalId':True,'presentation':'One carton','unit':'L','currency':'USD','active':True,'taxTreatment':'inclusive'},'freshnessBasis':'column','allowCachedFormulas':False}
    result=expect(200,http(root+f'/{account}/preview',{'fileId':'sheet','sheet':'Inventory','mapping':mapping},a));assert result['preview']['canImport'] and result['preview']['offers'][0]['externalId']=='0001'
    for file in ['unselected','denied','../escape']:
        expect(400,http(root+f'/{account}/inspect',{'fileId':file,'headerRow':1},a))
    assert expect(400,http(root+f'/{account}/inspect',{'fileId':'changing','headerRow':1},a))['code']=='google_changed'
    expect(503,http(root+f'/{account}/inspect',{'fileId':'redirect','headerRow':1},a))
    assert before==sql('SELECT count(*) FROM operational_data.records')
    # Refresh, fencing and abandoned leases with controlled time against actual persistence.
    port=h['docker']('compose','port','postgres','5432').stdout.strip().rsplit(':',1)[1]
    password=h['password'];env=dict(os.environ,DOCPARSE_GOOGLE_TEST_DB=f'Host=127.0.0.1;Port={port};Database={h["DATABASE"]};Username=docparse;Password={password}',DOCPARSE_GOOGLE_TEST_ORG=org,DOCPARSE_GOOGLE_TEST_USER=user,DOCPARSE_GOOGLE_TEST_PROVIDER=os.environ['DOCPARSE_GOOGLE_LOCAL_ORIGIN'])
    subprocess.run(['dotnet','run','--project','tests/GoogleDrivePolicyTests/GoogleDrivePolicyTests.csproj','--','--persistence'],cwd=ROOT,env=env,check=True)
    # Reset IP rate window before the independent in-flight tests and browser journey.
    h['docker']('restart',h['CONTAINER']);h['healthy']()
    blocked,blocked_code=authorize('blocked');control('reset_block')
    with ThreadPoolExecutor(1)as pool:
        future=pool.submit(http,root+'/complete',{'state':blocked['state'],'code':blocked_code},a)

        deadline=time.monotonic()+5
        while not control('counts')['blocked']:
            assert time.monotonic()<deadline;time.sleep(.05)
        expect(204,http(root+f'/{blocked["id"]}/disconnect',{},a));control('release');expect(400,future.result())
    expired,_=authorize();sql(f"UPDATE integrations.google_authorizations SET expires_at=now()-interval '1 second' WHERE account_id='{expired['id']}'")
    expect(400,http(root+'/complete',{'state':expired['state'],'code':'unused'},a))
    raced,raced_code=authorize();before_calls=control('counts')['calls']
    with ThreadPoolExecutor(2)as pool:
        outcomes=list(pool.map(lambda _:http(root+'/complete',{'state':raced['state'],'code':raced_code},a),range(2)))
    assert any(value[0]==200 for value in outcomes) and all(value[0]in(200,400)for value in outcomes) and control('counts')['calls']==before_calls+1
    h['docker']('restart',h['CONTAINER']);h['healthy']()
    control('reset_block')
    with ThreadPoolExecutor(1)as pool:
        future=pool.submit(http,root+f'/{raced["id"]}/inspect',{'fileId':'read-blocked','headerRow':1},a)
        deadline=time.monotonic()+5
        while not control('counts')['blocked']:
            assert time.monotonic()<deadline;time.sleep(.05)
        expect(204,http(root+f'/{raced["id"]}/disconnect',{},a));control('release');expect(400,future.result())
    for invalid_code in ['denied-scope','missing-refresh']:
        denied,denied_code=authorize(invalid_code)
        assert expect(400,http(root+'/complete',{'state':denied['state'],'code':denied_code},a))['code']=='google_permission'
        assert sql(f"SELECT protected_tokens IS NULL FROM integrations.google_accounts WHERE id='{denied['id']}'")=='t'
    if '--browser' in sys.argv:
        browser_env=dict(os.environ,VITE_API_PROXY_TARGET=h['BASE'],DOCPARSE_REAL_API=h['BASE'],DOCPARSE_TEST_ADMIN_EMAIL='push-a',DOCPARSE_TEST_ADMIN_PASSWORD=h['PASSWORD'],DOCPARSE_GOOGLE_ACCOUNT=account)
        subprocess.run(['npm.cmd'if os.name=='nt'else'npm','--prefix','front','test','--','tests/real-google-drive.spec.ts','--workers=1','--output='+str(ROOT/'.local'/'google-qa'/h['SUFFIX'])],cwd=ROOT,env=browser_env,check=True)
    expect(204,http(root+f'/{account}/disconnect',{},a));expect(400,http(root+f'/{account}/picker',{},a));assert sql(f"SELECT protected_tokens IS NULL FROM integrations.google_accounts WHERE id='{account}'")=='t'
    sql(f"""UPDATE access.memberships SET "Role"='Viewer' WHERE "OrganizationId"='{org}' AND "UserId"='{other_user}'""")
    print('PASS: Google OAuth state/PKCE, ownership/tenant denial, encrypted credentials/restart, refresh/leases/fencing, selected-file enforcement, Sheets/XLSX previews, redirects/change detection and local disconnect; no catalog writes.')

if __name__=='__main__':
    server=ThreadingHTTPServer(('0.0.0.0',0),GoogleProvider);threading.Thread(target=server.serve_forever,daemon=True).start()
    env=dict(os.environ,DOCPARSE_LIVE_PROVIDER_ORIGIN=f'http://host.docker.internal:{server.server_port}',DOCPARSE_GOOGLE_ORIGIN=f'http://host.docker.internal:{server.server_port}',DOCPARSE_GOOGLE_LOCAL_ORIGIN=f'http://127.0.0.1:{server.server_port}')
    try:subprocess.run([sys.executable,'tests/push_integration.py','--live','--scheduled','--catalog','--google',*sys.argv[1:]],cwd=ROOT,env=env,check=True)
    finally:server.shutdown();server.server_close()
