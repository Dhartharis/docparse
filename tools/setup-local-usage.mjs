import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { generateKeyPairSync, createPrivateKey, createPublicKey } from 'node:crypto';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const usage=path.resolve(root,'../fyr-usage');
const require=createRequire(path.join(usage,'package.json'));const {Client}=require('pg');
const password=execFileSync('docker',['compose','exec','-T','postgres','printenv','POSTGRES_PASSWORD'],{cwd:root,encoding:'utf8'}).trim();
const options={host:'127.0.0.1',port:Number(process.env.POSTGRES_PORT??15432),user:'docparse',password};
const admin=new Client({...options,database:'postgres'});await admin.connect();
try{const r=await admin.query("SELECT 1 FROM pg_database WHERE datname='fyr_usage_local'");if(!r.rowCount)await admin.query('CREATE DATABASE fyr_usage_local');}finally{await admin.end();}
await mkdir(path.join(root,'.local'),{recursive:true});const keyPath=path.join(root,'.local/usage-private.pem');let privateKey;
try{privateKey=createPrivateKey(await readFile(keyPath,'utf8'));}catch(error){if(error.code!=='ENOENT')throw error;const pair=generateKeyPairSync('ec',{namedCurve:'prime256v1'});privateKey=pair.privateKey;await writeFile(keyPath,privateKey.export({type:'pkcs8',format:'pem'}),{mode:0o600});}
const publicKey=createPublicKey(privateKey).export({type:'spki',format:'pem'});await writeFile(path.join(root,'.local/usage-public.pem'),publicKey);
const db=new Client({...options,database:'fyr_usage_local'});await db.connect();
try{
 await db.query('SELECT pg_advisory_lock(693865321)');
 for(const file of ['0001_initial.sql','0002_user_erasure.sql','0003_credit_reservations.sql'])await db.query(await readFile(path.join(usage,'migrations',file),'utf8'));
 await db.query("INSERT INTO clients(key,name) VALUES('docparse-local','DocParse local') ON CONFLICT(key) DO NOTHING");
 const id=(await db.query("SELECT id FROM clients WHERE key='docparse-local'")).rows[0].id;
 await db.query("INSERT INTO client_keys(client_id,kid,public_key,valid_from) VALUES($1,'local-v1',$2,now()-interval '1 minute') ON CONFLICT(client_id,kid) DO NOTHING",[id,publicKey]);
 const config=JSON.parse(await readFile(path.join(root,'tools/usage-local.json'),'utf8'));
 for(const p of config.plans)await db.query('INSERT INTO credit_plans(client_id,key,included_microcredits,credit_value_nano_usd,cost_fraction_ppm,allow_overage,simulation) VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT(client_id,key) DO UPDATE SET included_microcredits=excluded.included_microcredits,credit_value_nano_usd=excluded.credit_value_nano_usd,cost_fraction_ppm=excluded.cost_fraction_ppm,allow_overage=excluded.allow_overage,simulation=excluded.simulation',[id,p.key,p.includedMicrocredits,p.creditValueNanoUsd,p.costFractionPpm,p.allowOverage,p.simulation]);
 for(const r of config.rates)await db.query('INSERT INTO credit_rates(client_id,provider,model,input_nano_usd,output_nano_usd,simulation) VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(client_id,provider,model) DO UPDATE SET input_nano_usd=excluded.input_nano_usd,output_nano_usd=excluded.output_nano_usd,simulation=excluded.simulation',[id,r.provider,r.model,r.inputNanoUsd,r.outputNanoUsd,r.simulation]);
 console.log('Configured isolated Fyr Usage local database, simulation plans and development machine key. No production credentials used.');
}finally{await db.query('SELECT pg_advisory_unlock(693865321)');await db.end();}
