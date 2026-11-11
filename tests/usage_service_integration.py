"""Run old and new Fyr Usage PostgreSQL suites in a disposable local database."""
import os,subprocess,uuid,urllib.parse
from pathlib import Path
root=Path(__file__).resolve().parents[1];usage=root.parent/'fyr-usage';database='fyr_usage_test_'+uuid.uuid4().hex

def docker(*args):return subprocess.run(['docker',*args],cwd=root,capture_output=True,text=True,encoding='utf-8',check=True).stdout
created=False
try:
 password=docker('compose','exec','-T','postgres','printenv','POSTGRES_PASSWORD').strip()
 docker('compose','exec','-T','postgres','createdb','-U','docparse',database);created=True
 env=dict(os.environ,RUN_POSTGRES_INTEGRATION='1',DATABASE_URL='postgresql://docparse:'+urllib.parse.quote(password,safe='')+'@127.0.0.1:15432/'+database)
 subprocess.run(['npm.cmd' if os.name=='nt' else 'npm','test','--','--runInBand'],cwd=usage,env=env,check=True)
 print('PASS: Fyr Usage V1 compatibility and V2 reservation/credit accounting against isolated PostgreSQL.')
finally:
 if created:docker('compose','exec','-T','postgres','dropdb','--force','-U','docparse',database)
