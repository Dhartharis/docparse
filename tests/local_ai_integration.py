"""Opt-in real local AI smoke test. Requires Compose Ollama models; never uses cloud AI.
Uses an isolated database/API and removes the fixtures afterward.
"""
import json
import base64,hashlib,hmac
from email.utils import formatdate
import sys
import urllib.parse
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
SUFFIX = uuid.uuid4().hex
DATABASE = 'docparse_local_ai_' + SUFFIX
CONTAINER = 'docparse-local-ai-' + SUFFIX
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    PORT = sock.getsockname()[1]
BASE = f'http://127.0.0.1:{PORT}'


def docker(*args, check=True):
    result = subprocess.run(['docker', *args], cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
    if check and result.returncode:
        raise RuntimeError('Local Docker command failed: ' + result.stderr[-1000:])
    return result.stdout


def request(path, data=None, token=None, method=None, expected=200):
    headers = {'Content-Type':'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    req = urllib.request.Request(BASE + path, data=None if data is None else json.dumps(data).encode(), headers=headers, method=method)
    return send(req, expected)


def send(req, expected):
    try:
        response = urllib.request.urlopen(req, timeout=300)
    except urllib.error.HTTPError as failure:
        response = failure
    with response:
        body = response.read()
        assert response.status == expected, f'Expected {expected}, got {response.status}: {body[:200]!r}'
        if 'json' in response.headers.get('Content-Type',''):
            return json.loads(body) if body else None
        return body


def upload(path, content, filename, token, expected=200):
    boundary=uuid.uuid4().hex
    body=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode()+content+f'\r\n--{boundary}--\r\n'.encode()
    accepted = send(urllib.request.Request(BASE+path, data=body, headers={'Authorization':'Bearer '+token,'Content-Type':'multipart/form-data; boundary='+boundary}),202)
    endpoint = path.removesuffix('/extract') + '/' + accepted['id']
    for _ in range(1200):
        detail = request(endpoint, token=token)
        state = detail['document']['processingStatus']
        if state in ('ready','failed'):
            assert state == ('failed' if expected==400 else 'ready'), detail
            return {'id':accepted['id']}
        time.sleep(.25)
    raise AssertionError('Local background processing did not finish.')


def pdf(pages, jpeg=None):
    objects=[b'',b'']
    page_ids=[]
    for text in pages:
        page_id=len(objects)+1
        stream_id=page_id+1
        if jpeg is None:
            stream=('BT /F1 18 Tf 50 720 Td ('+text.replace('\\','\\\\').replace('(','\\(').replace(')','\\)')+') Tj ET').encode()
            resources=b'<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>'
        else:
            stream=b'q 612 0 0 792 0 0 cm /Im0 Do Q'
            image_id=stream_id+1
            resources=f'<< /XObject << /Im0 {image_id} 0 R >> >>'.encode()
        objects.append(f'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources '.encode()+resources+f' /Contents {stream_id} 0 R >>'.encode())
        objects.append(f'<< /Length {len(stream)} >>\nstream\n'.encode()+stream+b'\nendstream')
        if jpeg is not None:
            objects.append(f'<< /Type /XObject /Subtype /Image /Width 612 /Height 792 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {len(jpeg)} >>\nstream\n'.encode()+jpeg+b'\nendstream')
        page_ids.append(page_id)
    objects[0]=b'<< /Type /Catalog /Pages 2 0 R >>'
    objects[1]=f'<< /Type /Pages /Count {len(page_ids)} /Kids [ '.encode()+b' '.join(f'{i} 0 R'.encode() for i in page_ids)+b' ] >>'
    output=bytearray(b'%PDF-1.4\n'); offsets=[0]
    for index,obj in enumerate(objects,1):
        offsets.append(len(output));output.extend(f'{index} 0 obj\n'.encode()+obj+b'\nendobj\n')
    start=len(output);output.extend(f'xref\n0 {len(objects)+1}\n0000000000 65535 f \n'.encode())
    for offset in offsets[1:]:output.extend(f'{offset:010d} 00000 n \n'.encode())
    output.extend(f'trailer << /Size {len(objects)+1} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF'.encode())
    return bytes(output)


RETENTION="--retention" in sys.argv
METERED="--metered" in sys.argv
USAGE_CONTAINER="docparse-usage-ai-"+SUFFIX
created=False
try:
    password=docker('compose','exec','-T','postgres','printenv','POSTGRES_PASSWORD').strip()
    connection=f'Host=postgres;Database={DATABASE};Username=docparse;Password={password}'
    docker('compose','exec','-T','postgres','psql','-U','docparse','-c',f'CREATE DATABASE {DATABASE}')
    created=True
    docker('compose','run','--rm','--no-deps','-e','PostgreSQL__ConnectionString='+connection,'migrate')
    if METERED:
        usage_root=ROOT.parent/'fyr-usage'
        for migration in ['0001_initial.sql','0002_user_erasure.sql','0003_credit_reservations.sql']:
            source=usage_root/'migrations'/migration
            subprocess.run(['docker','compose','exec','-T','postgres','psql','-U','docparse','-d',DATABASE,'-v','ON_ERROR_STOP=1'],cwd=ROOT,input=source.read_text(encoding='utf-8'),text=True,encoding='utf-8',capture_output=True,check=True)
        public=(ROOT/'.local/usage-public.pem').read_text(encoding='utf-8').replace("'","''")
        config=json.loads((ROOT/'tools/usage-local.json').read_text(encoding='utf-8'))
        sql="INSERT INTO clients(key,name) VALUES('docparse-local','Local AI test'); INSERT INTO client_keys(client_id,kid,public_key,valid_from) SELECT id,'local-v1','"+public+"',now()-interval '1 minute' FROM clients WHERE key='docparse-local';"
        for plan in config['plans']:
            sql+=f"INSERT INTO credit_plans SELECT id,'{plan['key']}',{plan['includedMicrocredits']},{plan['creditValueNanoUsd']},{plan['costFractionPpm']},{str(plan['allowOverage']).lower()},true FROM clients WHERE key='docparse-local';"
        for rate in config['rates']:
            sql+=f"INSERT INTO credit_rates SELECT id,'{rate['provider']}','{rate['model']}',{rate['inputNanoUsd']},{rate['outputNanoUsd']},true FROM clients WHERE key='docparse-local';"
        subprocess.run(['docker','compose','exec','-T','postgres','psql','-U','docparse','-d',DATABASE,'-v','ON_ERROR_STOP=1'],cwd=ROOT,input=sql,text=True,encoding='utf-8',capture_output=True,check=True)
        usage_url='postgresql://docparse:'+urllib.parse.quote(password,safe='')+'@postgres:5432/'+DATABASE
        docker('run','-d','--name',USAGE_CONTAINER,'--network','docparse_default','-e','DATABASE_URL='+usage_url,'docparse-usage')
    blob=docker('compose','exec','-T','api','printenv','AzureStorage__ConnectionString').strip()
    docker('run','-d','--name',CONTAINER,'--network','docparse_default','-p',f'127.0.0.1:{PORT}:8080',
           '-e','ASPNETCORE_ENVIRONMENT=Development','-e','PostgreSQL__ConnectionString='+connection,
           '-e','AzureStorage__ConnectionString='+blob,'-e','AI__Provider=Ollama','-e','Ollama__Endpoint=http://ollama:11434/',
           '-e','OpenAI__ApiKey=',*(['-e','FyrUsage__Enabled=true','-e','FyrUsage__Endpoint=http://'+USAGE_CONTAINER+':3010/','-e','FyrUsage__PrivateKeyPath=/app/usage-private.pem','-v',str(ROOT/'.local/usage-private.pem')+':/app/usage-private.pem:ro'] if METERED else []),'docparse-api')
    email='local-ai-'+SUFFIX+'@example.test'; secret='Local-'+SUFFIX+'!aA1'
    docker('exec','-e','LocalAdmin__Email='+email,'-e','LocalAdmin__UserName=local-ai-admin','-e','LocalAdmin__Password='+secret,CONTAINER,'dotnet','DocParse.dll','--bootstrap-local-admin')
    for _ in range(60):
        try:
            request('/health');break
        except (urllib.error.URLError,ConnectionError):time.sleep(.2)
    token=request('/api/auth/login',{'email':email,'password':secret})['accessToken']
    org=request('/api/organizations/',token=token)[0]['id']; root=f'/api/organizations/{org}'
    kb=request(root+'/knowledge-bases/',{'name':'Local AI fixture'},token,expected=201)['id']
    bot=request(root+'/bots/',{'name':'Local assistant'},token,expected=201)['id']
    request(root+f'/bots/{bot}/knowledge-bases',{'knowledgeBaseIds':[kb]},token,'PUT',204)
    path=root+f'/knowledge-bases/{kb}/documents'
    original=pdf(['The spacecraft is called NEBULA.', 'The NEBULA launch code is VIOLET-739.', 'NEBULA subscription purchase date: November 3, 2026. Duration: one year. Price: USD 3.80.'])
    document=upload(path+'/extract',original,'local-fixture.pdf',token)['id']
    detail=request(path+'/'+document,token=token)
    assert 'VIOLET-739' in detail['fields']['text']
    assert request(path+'/'+document+'/file',token=token)==original
    rows=docker('compose','exec','-T','postgres','psql','-U','docparse','-d',DATABASE,'-Atc',f"SELECT count(*),count(DISTINCT page_number),min(vector_dims(embedding)) FROM knowledge.document_embeddings WHERE document_id='{document}'").strip()
    assert rows=='3|3|1024', rows
    # A legacy/cloud vector with incompatible dimensions must never enter local ranking.
    docker('compose','exec','-T','postgres','psql','-U','docparse','-d',DATABASE,'-c',f"INSERT INTO knowledge.document_embeddings(id,document_id,content,embedding,created_at,embedding_profile) VALUES('{uuid.uuid4()}','{document}','FORBIDDEN_PROFILE',array_fill(0.1::real,ARRAY[1536])::vector,now(),'openai:text-embedding-3-small')")
    with tempfile.TemporaryDirectory(prefix='docparse-local-ai-') as directory:
        fixture=Path(directory)/'fixture.pdf';fixture.write_bytes(pdf(['NEBULA CODE VIOLET-739']))
        docker('cp',str(fixture),CONTAINER+':/tmp/local-fixture.pdf')
        docker('exec',CONTAINER,'pdftoppm','-r','72','-f','1','-l','1','-singlefile','-jpeg','/tmp/local-fixture.pdf','/tmp/local-fixture')
        image=Path(directory)/'fixture.jpg';docker('cp',CONTAINER+':/tmp/local-fixture.jpg',str(image))
        image_doc=upload(path+'/extract',image.read_bytes(),'ocr.jpg',token)['id']
        assert 'NEBULA' in request(path+'/'+image_doc,token=token)['fields']['text']
        scan=pdf([''],jpeg=image.read_bytes())
        scan_doc=upload(path+'/extract',scan,'scanned.pdf',token)['id']
        assert 'VIOLET' in request(path+'/'+scan_doc,token=token)['fields']['text']
    for attempt in range(600):
        with urllib.request.urlopen('http://127.0.0.1:11434/api/tags', timeout=5) as response:
            ready = any(model['name'] == 'qwen3:4b-instruct' for model in json.load(response)['models'])
        if ready: break
        if attempt == 0: print('Local PDF/image OCR passed; waiting for the chat model download.', flush=True)
        time.sleep(1)
    else: raise AssertionError('Chat model was not downloaded; run the model setup first.')
    answer=request(root+f'/bots/{bot}/messages',{'question':'¿Cuál es el código de lanzamiento de NEBULA? Responde brevemente en español.'},token)
    assert 'VIOLET-739' in answer['answer'], answer['answer']
    assert len(answer['answer']) < 1000 and 'Okay' not in answer['answer'] and '<think>' not in answer['answer']
    followup=request(root+f'/bots/{bot}/messages',{'question':'¿Cuál era ese código?','history':[{'question':'¿Cuál es el código de lanzamiento de NEBULA?','answer':answer['answer'][:800]}]},token)
    assert 'VIOLET-739' in followup['answer'], followup['answer']
    expiry=request(root+f'/bots/{bot}/messages',{'question':'¿Y cuándo vence el primer año?','history':[{'question':'¿Cuánto cuesta el primer año de NEBULA?','answer':'USD 3.80.'}]},token)['answer']
    assert '2027' in expiry and 'noviembre' in expiry.lower(), expiry
    assert 'estim' in expiry.lower() and ('no confirma' in expiry.lower() or 'no está confirm' in expiry.lower()), expiry
    overview=request(root+f'/bots/{bot}/messages',{'question':'¿Qué puedes decirme acerca de los archivos disponibles? Responde en español.'},token)
    assert 'Okay' not in overview['answer'] and 'the user' not in overview['answer'].lower()
    assert len(overview['answer']) < 2500
    assert any(source['pageNumber']==2 for source in answer['sources'])
    assert 'FORBIDDEN_PROFILE' not in answer['answer']
    count=len(request(path+'/all',token=token))
    upload(path+'/extract',b'not-a-real-pdf','invalid.pdf',token,400)
    assert len(request(path+'/all',token=token))==count+1
    if RETENTION:
        def sql(statement):return docker('compose','exec','-T','postgres','psql','-U','docparse','-d',DATABASE,'-Atc',statement).strip()
        storage=dict(part.split('=',1) for part in blob.split(';') if '=' in part)
        def exists(container,key):
            date=formatdate(usegmt=True);version='2023-11-03';account=storage['AccountName']
            canonical='x-ms-date:'+date+'\nx-ms-version:'+version+'\n'
            resource='/'+account+'/'+account+'/'+container+'/'+key
            signed='HEAD'+'\n'*12+canonical+resource
            signature=base64.b64encode(hmac.new(base64.b64decode(storage['AccountKey']),signed.encode(),hashlib.sha256).digest()).decode()
            req=urllib.request.Request('http://127.0.0.1:10000/'+account+'/'+container+'/'+key,method='HEAD',headers={'x-ms-date':date,'x-ms-version':version,'Authorization':'SharedKey '+account+':'+signature})
            try:
                with urllib.request.urlopen(req) as response:code=response.status
            except urllib.error.HTTPError as response:code=response.code
            assert code in (200,404),code
            return code==200
        original_key=document.replace('-','')+'/original.pdf'
        assert exists('documents',original_key)
        assert upload(path+'/extract',original,'same-bytes.pdf',token)['id']==document
        endpoint=root+f'/knowledge-bases/{kb}'
        request(endpoint,token=token,method='DELETE',expected=409)
        request(path+'/'+document,token=token,method='DELETE',expected=204)
        request(path+'/'+document,token=token,expected=404)
        request(path+'/'+document+'/file',token=token,expected=404)
        assert exists('documents',original_key)
        hidden=request(root+f'/bots/{bot}/messages',{'question':'What is the NEBULA launch code?'},token)
        assert all(source['documentId']!=document for source in hidden['sources'])
        request(path+'/'+document+'/restore',{},token,expected=204)
        assert request(path+'/'+document+'/file',token=token)==original
        request(root+f'/bots/{bot}/knowledge-bases',{'knowledgeBaseIds':[]},token,'PUT',204)
        request(path+'/'+document,token=token,method='DELETE',expected=204)
        request(endpoint,token=token,method='DELETE',expected=204)
        request(path+'/'+document+'/restore',{},token,expected=404)
        request(endpoint+'/restore',{},token,expected=204)
        request(path+'/'+document+'/restore',{},token,expected=204)
        request(root+f'/bots/{bot}/knowledge-bases',{'knowledgeBaseIds':[kb]},token,'PUT',204)
        issued=request(root+f'/bots/{bot}/channels/keys',{'name':'Trash key'},token)
        request(root+f'/bots/{bot}',token=token,method='DELETE',expected=204)
        assert any(base['id']==kb for base in request(root+'/knowledge-bases/',token=token))
        request(root+f'/bots/{bot}/messages',{'question':'No access'},token,expected=404)
        req=urllib.request.Request(BASE+f'/api/v1/organizations/{org}/bots/{bot}/messages',data=b'{"question":"No access"}',headers={'Content-Type':'application/json','X-DocParse-Key':issued['secret']})
        send(req,401)
        request(root+f'/bots/{bot}/restore',{},token,expected=204)
        assert not request(root+f'/bots/{bot}/channels/embed',token=token)['enabled']
        send(req,401)
        request(path+'/'+document,token=token,method='DELETE',expected=204)
        sql(f"UPDATE knowledge.documents SET deleted_at=now()-interval '31 days' WHERE id='{document}'")
        for _ in range(100):
            if sql(f"SELECT count(*) FROM knowledge.documents WHERE id='{document}'")=='0' and not exists('documents',original_key):break
            time.sleep(.2)
        else:raise AssertionError('Expired document/file were not purged.')
        assert sql(f"SELECT count(*) FROM knowledge.document_embeddings WHERE document_id='{document}'")=='0'
        request(path+'/'+document+'/restore',{},token,expected=404)
        request(root+f'/bots/{bot}',token=token,method='DELETE',expected=204)
        sql(f"UPDATE bots.bots SET deleted_at=now()-interval '31 days' WHERE id='{bot}'")
        for _ in range(100):
            if sql(f"SELECT count(*) FROM bots.bots WHERE id='{bot}'")=='0':break
            time.sleep(.2)
        else:raise AssertionError('Expired bot not purged.')
        assert any(base['id']==kb for base in request(root+'/knowledge-bases/',token=token))
        retained=sql(f"SELECT id::text||':'||original_blob_name FROM knowledge.documents WHERE knowledge_base_id='{kb}' AND original_blob_name IS NOT NULL").splitlines()
        request(endpoint,token=token,method='DELETE',expected=204)
        sql(f"UPDATE knowledge.knowledge_bases SET deleted_at=now()-interval '31 days' WHERE id='{kb}'")
        for _ in range(200):
            if sql(f"SELECT count(*) FROM knowledge.knowledge_bases WHERE id='{kb}'")=='0' and all(not exists('documents',row.split(':',1)[1]) for row in retained):break
            time.sleep(.2)
        else:raise AssertionError('Expired base/documents/files were not purged.')
        print('PASS: deduplication, linked-base protection, restore order, immediate retrieval/file exclusion, channel revocation, 30-day expiry, vector deletion and private Blob cleanup.')
    if METERED:
        def sql(statement):return docker('compose','exec','-T','postgres','psql','-U','docparse','-d',DATABASE,'-Atc',statement).strip()
        for _ in range(60):
            state=request(root+'/usage/',token=token)
            if state['local']['pendingOperations']==0:break
            time.sleep(.5)
        assert state['local']['pendingOperations']==0 and state['local']['failedSynchronizations']==0,state
        assert int(state['state']['consumedMicrocredits'])>0 and state['state']['reservedMicrocredits']=='0',state
        breakdown=request(root+'/usage/breakdown',token=token)
        assert any(row['resourceId']==bot for row in breakdown) and any(row['feature']=='ingestion' for row in breakdown),breakdown
        consumed_before_outage=state['state']['consumedMicrocredits']
        # An unavailable ledger must stop new provider work before the first model call.
        docker('stop',USAGE_CONTAINER)
        request(root+f'/bots/{bot}/messages',{'question':'No provider work should start'},token,expected=503)
        fresh_org=request('/api/organizations/',{'name':'First-cycle outage'},token,expected=201)['id']
        fresh_root='/api/organizations/'+fresh_org
        fresh_base=request(fresh_root+'/knowledge-bases/',{'name':'Empty base'},token,expected=201)['id']
        fresh_bot=request(fresh_root+'/bots/',{'name':'Outage assistant'},token,expected=201)['id']
        request(fresh_root+f'/bots/{fresh_bot}/knowledge-bases',{'knowledgeBaseIds':[fresh_base]},token,method='PUT',expected=204)
        request(fresh_root+f'/bots/{fresh_bot}/messages',{'question':'No first-cycle provider work'},token,expected=503)
        assert sql(f"SELECT count(*) FROM usage.operations WHERE organization_id='{fresh_org}' AND state NOT IN('complete','denied')")=='0'
        assert sql(f"SELECT count(*) FROM credit_operations WHERE subject='{fresh_org}'")=='0'
        # Simulate a lost settlement acknowledgement, then replay the identical durable report.
        sql("UPDATE usage.operations SET state='pending',retry_at=now() WHERE id=(SELECT id FROM usage.operations WHERE state='complete' AND success=true ORDER BY created_at LIMIT 1)")
        docker('restart',CONTAINER)
        docker('start',USAGE_CONTAINER)
        for _ in range(60):
            try:
                state=request(root+'/usage/',token=token)
                if state['local']['pendingOperations']==0:break
            except (urllib.error.URLError,ConnectionError,AssertionError):pass
            time.sleep(.5)
        assert state['local']['pendingOperations']==0,state
        assert state['state']['consumedMicrocredits']==consumed_before_outage,state
        # Reduce only this disposable cycle to its already-consumed amount.
        docker('compose','exec','-T','postgres','psql','-U','docparse','-d',DATABASE,'-c',f"UPDATE credit_periods SET included_microcredits=consumed_microcredits WHERE subject='{org}'")
        request(root+f'/bots/{bot}/messages',{'question':'Limit before generation'},token,expected=429)
        state=request(root+'/usage/',token=token)
        assert not state['state']['canUseAi'] and state['state']['reservedMicrocredits']=='0',state
        # A never-admitted, unmetered attempt can resolve without reserving when quota is gone.
        sql(f"INSERT INTO usage.operations(id,organization_id,bot_id,resource_id,feature,channel,state,maximum) SELECT '{uuid.uuid4()}',organization_id,bot_id,resource_id,feature,channel,'pending',maximum FROM usage.operations WHERE organization_id='{org}' AND state='denied' ORDER BY created_at DESC LIMIT 1")
        for _ in range(60):
            state=request(root+'/usage/',token=token)
            if state['local']['pendingOperations']==0:break
            time.sleep(.2)
        assert state['local']['pendingOperations']==0 and state['state']['reservedMicrocredits']=='0',state
        print('PASS: real Fyr Usage ES256, measured tokens, per-bot/ingestion credits, durable outbox after outage/restart and quota admission.')
    print('PASS: real local PDF extraction, 1024-dimensional chunks/pages, profile isolation, grounded model answer, original download, image OCR, scanned PDF OCR and invalid-file rollback.')
except Exception:
    print(docker('logs','--tail','20',CONTAINER,check=False))
    raise
finally:
    docker('rm','-f',CONTAINER,check=False)
    if METERED:docker('rm','-f',USAGE_CONTAINER,check=False)
    if created:
        assert re.fullmatch('docparse_local_ai_[a-f0-9]{32}',DATABASE)
        docker('compose','exec','-T','postgres','psql','-U','docparse','-c',f'DROP DATABASE {DATABASE}')
