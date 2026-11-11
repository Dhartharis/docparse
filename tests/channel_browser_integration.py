"""Focused isolated local fixture for channel UI and real iframe checks."""
import os, sys, subprocess, time, uuid, urllib.request, urllib.error, socket
from pathlib import Path
root=Path(__file__).resolve().parents[1]
suffix=uuid.uuid4().hex; database="docparse_channels_"+suffix; container="docparse-channels-"+suffix
with socket.socket() as sock: sock.bind(("127.0.0.1",0)); port=sock.getsockname()[1]
base=f"http://127.0.0.1:{port}"
def docker(*args,check=True):return subprocess.run(["docker",*args],cwd=root,capture_output=True,text=True,encoding="utf-8",check=check).stdout
created=False
try:
 password=docker("compose","exec","-T","postgres","printenv","POSTGRES_PASSWORD").strip()
 connection=f"Host=postgres;Database={database};Username=docparse;Password={password}"
 docker("compose","exec","-T","postgres","createdb","-U","docparse",database);created=True
 docker("compose","run","--rm","--no-deps","-e","FyrUsage__Enabled=false","-e","PostgreSQL__ConnectionString="+connection,"migrate")
 docker("compose","run","-d","--no-deps","--name",container,"-p",f"127.0.0.1:{port}:8080","-e","FyrUsage__Enabled=false","-e","PostgreSQL__ConnectionString="+connection,"-e","PublicApi__BaseUrl="+base,"-e","PublicWeb__BaseUrl=http://127.0.0.1:45173","api")
 email="channels-"+suffix+"@example.test";secret="Channels-"+suffix+"!aA1"
 docker("exec","-e","LocalAdmin__Email="+email,"-e","LocalAdmin__UserName=channels-admin","-e","LocalAdmin__Password="+secret,container,"dotnet","DocParse.dll","--bootstrap-local-admin")
 for _ in range(60):
  try:urllib.request.urlopen(base+"/health",timeout=2);break
  except (urllib.error.URLError,ConnectionError):time.sleep(.2)
 environment=dict(os.environ,DOCPARSE_REAL_API=base,VITE_API_PROXY_TARGET=base,DOCPARSE_TEST_ADMIN_EMAIL=email,DOCPARSE_TEST_ADMIN_PASSWORD=secret)
 subprocess.run(["npm.cmd" if os.name=="nt" else "npm","--prefix","front","test","--",("tests/real-owner.spec.ts" if "--owner" in sys.argv else "tests/real-channels.spec.ts"),"--workers=1"],cwd=root,env=environment,check=True)
 print("PASS: isolated API keys and embedded shared UI.")
except Exception:
 print(docker("logs","--tail","40",container,check=False));raise
finally:
 docker("rm","-f",container,check=False)
 if created:docker("compose","exec","-T","postgres","dropdb","--force","-U","docparse",database)
