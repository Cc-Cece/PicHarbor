"""Mechanical event bridge. Collection decisions live in signed remote code.

This module filters facts before they reach the collector, bounds execution and
disk/network work, and provides idempotent cumulative record storage. It has no
backup success-rate, sampling or tool-usage policy.
"""
from contextlib import closing
import ast
import datetime
import hashlib
import json
import math
import re
import sqlite3
import sys
import time
import uuid

FACT_VERSION=1
FACT_NAMES={'legacy.event','app.start','app.foreground','app.background','app.heartbeat','app.previous_exit',
 'environment.snapshot','preferences.snapshot','backup.conditions','backup.batch','backup.item','backup.stage',
 'transfer.batch','error.native','error.operation','remote.fetch','remote.validation','remote.execute','remote.policy',
 'feature.action','feedback.stage','update.stage','renewal.stage','proxy.result','runtime.resource'}
TEXT_FIELDS={'os_version','brand','model_family','abi','ram_bucket','language','theme','orientation','network','photo_access','auth_state',
 'battery_optimization','thermal','battery_bucket','source','quality','live_mode','album_mode','concurrency_mode','blocked_reason',
 'stage','status','media_type','error_code','exception_type','target_version','operation','protocol','tool_id','channel','device_class','fingerprint'}
BOOL_FIELDS={'proxy_enabled','power_save','low_memory','foreground','auto_backup','wifi_only','backup_scope_confirmed','renewal_configured','success'}
NUMBER_FIELDS={'font_scale','media_total','media_success','media_failed','media_skipped','resource_total','resource_success','resource_failed',
 'attempts','retries','bytes','duration_ms','active_ms','wait_ms','prepare_ms','upload_ms','confirm_ms','queue_peak','actual_concurrency',
 'target_build','content_revision','collector_revision','base_version','validity_hours','latency_ms','started_ms','ended_ms'}
DIMENSION_FIELDS=TEXT_FIELDS|BOOL_FIELDS|{'content_revision','collector_revision','base_version','font_scale','target_build','validity_hours'}
KINDS={'profile','session','task','error','feature','remote','condition','update','renewal','proxy'}
RESULTS={'success','failure','cancelled','waiting','active','closed','unknown','none'}
SYMBOL=re.compile(r'^[A-Za-z0-9_. -]{1,80}$')
NAME=re.compile(r'^[a-z][a-z0-9_.-]{0,63}$')
METRIC=re.compile(r'^[a-z][a-z0-9_.]{0,47}$')
_COMPILED={}
DATA_METHODS={'get','items','keys','values','startswith','endswith','split','lower','upper','strip','replace','join','setdefault','append','extend','pop','copy','isdigit','isalpha','isalnum'}

def arithmetic(op,a,b):
    if op=='Add' and isinstance(a,(str,list,tuple)) and type(a) is type(b):
        if len(a)+len(b)>(32768 if isinstance(a,str) else 256):raise ValueError('COLLECTOR_ALLOCATION')
        return a+b
    if op=='Mult' and (isinstance(a,(str,list,tuple)) or isinstance(b,(str,list,tuple))):
        seq,n=(a,b) if isinstance(a,(str,list,tuple)) else (b,a)
        if type(n) is not int or n<0 or len(seq)*n>(32768 if isinstance(seq,str) else 256):raise ValueError('COLLECTOR_ALLOCATION')
        return seq*n
    if type(a) not in (int,float) or type(b) not in (int,float) or abs(a)>1e15 or abs(b)>1e15:raise ValueError('COLLECTOR_ARITHMETIC')
    if op=='Pow' and (b<0 or b>12):raise ValueError('COLLECTOR_ARITHMETIC')
    value={'Add':lambda:a+b,'Sub':lambda:a-b,'Mult':lambda:a*b,'Div':lambda:a/b,'FloorDiv':lambda:a//b,'Mod':lambda:a%b,'Pow':lambda:a**b}.get(op,lambda:None)()
    if type(value) not in (int,float) or not math.isfinite(value) or abs(value)>1e15:raise ValueError('COLLECTOR_ARITHMETIC')
    return value

def data_method(value,name,*args,**kwargs):
    if name not in DATA_METHODS or not isinstance(value,(str,list,dict,set,tuple)):raise ValueError('COLLECTOR_CAPABILITY')
    if name=='join':
        sequence=[]
        for item in args[0]:
            sequence.append(item)
            if len(sequence)>256:raise ValueError('COLLECTOR_ALLOCATION')
        if any(not isinstance(s,str) for s in sequence) or sum(len(s) for s in sequence)+len(value)*len(sequence)>32768:raise ValueError('COLLECTOR_ALLOCATION')
        args=(sequence,)
    if name=='replace' and len(args)>=2 and isinstance(value,str):
        count=min(value.count(args[0]),args[2] if len(args)>2 and args[2]>=0 else 32768)
        if len(value)+count*(len(args[1])-len(args[0]))>32768:raise ValueError('COLLECTOR_ALLOCATION')
    if name=='append' and len(value)>=256:raise ValueError('COLLECTOR_ALLOCATION')
    if name=='extend':
        sequence=[]
        for item in args[0]:
            sequence.append(item)
            if len(sequence)+len(value)>256:raise ValueError('COLLECTOR_ALLOCATION')
        args=(sequence,)
    result=getattr(value,name)(*args,**kwargs)
    if isinstance(result,(str,list,dict,set,tuple)) and len(result)>(32768 if isinstance(result,str) else 256):raise ValueError('COLLECTOR_ALLOCATION')
    return result

class BoundedExpressions(ast.NodeTransformer):
    def visit_BinOp(self,node):
        self.generic_visit(node)
        return ast.copy_location(ast.Call(ast.Name('_arithmetic',ast.Load()),[ast.Constant(type(node.op).__name__),node.left,node.right],[]),node)
    def visit_Call(self,node):
        self.generic_visit(node)
        if isinstance(node.func,ast.Attribute):return ast.copy_location(ast.Call(ast.Name('_data_method',ast.Load()),[node.func.value,ast.Constant(node.func.attr),*node.args],node.keywords),node)
        return node

def collector_code(source):
    digest=hashlib.sha256(source.encode()).hexdigest()
    if digest in _COMPILED:return _COMPILED[digest]
    if len(source.encode())>98304:raise ValueError('COLLECTOR_SIZE')
    tree=ast.parse(source,'remote-collector')
    methods=DATA_METHODS
    for node in ast.walk(tree):
        if isinstance(node,(ast.Import,ast.ImportFrom,ast.ClassDef,ast.Global,ast.Nonlocal,ast.With,ast.AsyncWith,ast.Try,ast.TryStar,ast.AugAssign)):raise ValueError('COLLECTOR_CAPABILITY')
        if isinstance(node,ast.Name) and (node.id.startswith('__') or node.id in {'_arithmetic','_data_method','open','eval','exec','compile','globals','locals','vars','getattr','setattr','delattr','input','help','breakpoint'}):raise ValueError('COLLECTOR_CAPABILITY')
        if isinstance(node,ast.Attribute) and node.attr not in methods:raise ValueError('COLLECTOR_CAPABILITY')
        if isinstance(node,ast.Constant) and isinstance(node.value,str) and len(node.value)>16384:raise ValueError('COLLECTOR_LITERAL')
    code=compile(ast.fix_missing_locations(BoundedExpressions().visit(tree)),'remote-collector','exec')
    if len(_COMPILED)>8:_COMPILED.clear()
    _COMPILED[digest]=code
    return code

def number(value):return type(value) in (int,float) and math.isfinite(value) and 0<=value<=10**15

def safe_facts(fields):
    if not isinstance(fields,dict):return {}
    result={}
    for key,value in fields.items():
        if key in TEXT_FIELDS and isinstance(value,str) and SYMBOL.fullmatch(value) and not any(s in value.lower() for s in ('bearer ','oauth','password','token=')):result[key]=value
        elif key in BOOL_FIELDS and type(value) is bool:result[key]=value
        elif key in NUMBER_FIELDS and number(value):result[key]=value
        elif key=='frames' and isinstance(value,list):
            frames=[s for s in value[:12] if isinstance(s,str) and re.fullmatch(r'[A-Za-z0-9_.$<>:-]{1,160}',s)]
            if frames:result[key]=frames
    return result

def run_collector(source,event,context,state=None):
    allowed=('str','int','float','bool','len','dict','list','tuple','set','min','max','sum','range','enumerate','zip','round','abs','isinstance','sorted')
    builtins=__builtins__ if isinstance(__builtins__,dict) else vars(__builtins__)
    namespace={'__builtins__':{key:builtins[key] for key in allowed},'_arithmetic':arithmetic,'_data_method':data_method}
    def bounded_range(*args):
        value=range(*args)
        if len(value)>4096:raise ValueError('COLLECTOR_ALLOCATION')
        return value
    def bounded_items(value=()):
        items=[]
        for item in value:
            if len(items)>=4096:raise ValueError('COLLECTOR_ALLOCATION')
            items.append(item)
        return items
    namespace['__builtins__']['range']=bounded_range
    namespace['__builtins__']['sorted']=lambda value,**kwargs:sorted(bounded_items(value),**kwargs)
    steps=0;deadline=time.monotonic()+.25
    def trace(frame,kind,arg):
        nonlocal steps
        frame.f_trace_opcodes=True;steps+=1
        if steps>120000 or time.monotonic()>deadline:raise TimeoutError('COLLECTOR_BUDGET')
        return trace
    old=sys.gettrace()
    try:
        code=collector_code(source)
        sys.settrace(trace);exec(code,namespace)
        handler=namespace.get('on_event')
        if not callable(handler):raise ValueError('COLLECTOR_HANDLER')
        value=handler(event,dict(context),dict(state or {}))
        if not isinstance(value,dict) or not isinstance(value.get('records',[]),list) or len(value.get('records',[]))>12:raise ValueError('COLLECTOR_RESULT')
        if not isinstance(value.get('state',{}),dict) or len(json.dumps(value.get('state',{})))>16384:raise ValueError('COLLECTOR_STATE_LIMIT')
        return value
    finally:sys.settrace(old)

def checked_record(value):
    if not isinstance(value,dict) or value.get('kind') not in KINDS or not NAME.fullmatch(str(value.get('name',''))) or value.get('result','none') not in RESULTS:raise ValueError('COLLECTOR_RECORD')
    key=value.get('key','')
    if not isinstance(key,str) or not 1<=len(key)<=200 or any(c in key for c in ('@','/','\\','?','=')):raise ValueError('COLLECTOR_KEY')
    if value.get('mode','snapshot') not in ('counter','snapshot'):raise ValueError('COLLECTOR_MODE')
    metrics=value.get('metrics',{})
    if not isinstance(metrics,dict) or len(metrics)>40 or any(not METRIC.fullmatch(k) or not number(v) for k,v in metrics.items()):raise ValueError('COLLECTOR_METRICS')
    dimensions=safe_facts(value.get('dimensions',{}))
    dimensions={k:v for k,v in dimensions.items() if k in DIMENSION_FIELDS}
    frames=value.get('frames',[]);frames=safe_facts({'frames':frames}).get('frames',[])
    return {**{k:value.get(k,'none' if k=='result' else 'snapshot' if k=='mode' else '') for k in ('kind','name','result','mode','key')},'metrics':metrics,'dimensions':dimensions,'frames':frames}

class Bridge:
    def __init__(self,remote):self.remote=remote;self.path=remote.root/'telemetry.sqlite'
    def database(self):
        db=sqlite3.connect(self.path);db.execute('PRAGMA journal_mode=WAL');db.execute('PRAGMA synchronous=NORMAL')
        db.executescript('CREATE TABLE IF NOT EXISTS records(id TEXT PRIMARY KEY,payload TEXT NOT NULL,sequence INTEGER NOT NULL,pending INTEGER NOT NULL,updated INTEGER NOT NULL);CREATE TABLE IF NOT EXISTS facts(id INTEGER PRIMARY KEY AUTOINCREMENT,payload TEXT NOT NULL,created INTEGER NOT NULL);CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);')
        return db
    def observe(self,name,fields=None,key=''):
        if name not in FACT_NAMES:return False
        remote=self.remote;stamp=int(time.time());day=datetime.datetime.fromtimestamp(stamp,datetime.timezone.utc).date().isoformat()
        identifier=hashlib.sha256((remote.state['installation']+str(key)).encode()).hexdigest()[:32] if key else uuid.uuid4().hex
        event={'name':name,'fields':safe_facts(fields or {}),'id':identifier,'timestamp':stamp,'day':day,
               'version':str(remote.context.get('version','unknown'))[:40]}
        source=remote.content.get('telemetry_script','');policy=remote.content.get('telemetry',{})
        with closing(self.database()) as db,db:
            db.execute('DELETE FROM records WHERE updated<?',(stamp-30*86400,));db.execute('DELETE FROM facts WHERE created<?',(stamp-3600,))
            if not source:
                db.execute('INSERT INTO facts(payload,created) VALUES(?,?)',(json.dumps(event),stamp));db.execute('DELETE FROM facts WHERE id NOT IN (SELECT id FROM facts ORDER BY id DESC LIMIT 200)');return False
            if not policy.get('enabled',False):
                db.execute('DELETE FROM facts');return False
            revision=str(policy.get('revision',0));state_row=db.execute('SELECT value FROM meta WHERE key=?',('state:'+revision,)).fetchone()
            state=json.loads(state_row[0]) if state_row else {}
            events=[json.loads(row[0]) for row in db.execute('SELECT payload FROM facts ORDER BY id LIMIT 200')]+[event]
            context={**remote.context,'day':day,'collector_revision':policy.get('revision',0),'content_revision':remote.content.get('revision',0),'fact_version':FACT_VERSION}
            for fact in events:
                value=run_collector(source,fact,context,state)
                # Validate the entire result before accepting any of its records.
                records=[checked_record(record) for record in value.get('records',[])]
                for record in records:self.store(db,record,fact,{**context,'version':fact.get('version',context.get('version','unknown'))},stamp)
                state=value.get('state',{})
            db.execute('DELETE FROM facts');db.execute('INSERT OR REPLACE INTO meta VALUES(?,?)',('state:'+revision,json.dumps(state)))
            db.execute('DELETE FROM meta WHERE key LIKE ? AND key!=?',('state:%','state:'+revision))
            db.execute('DELETE FROM records WHERE id NOT IN (SELECT id FROM records ORDER BY updated DESC LIMIT 5000)')
            # Bound live payload bytes as well as row count. Checkpoints keep the
            # WAL small; SQLite's reusable pages do not grow with event history.
            total=db.execute('SELECT COALESCE(SUM(length(payload)),0) FROM records').fetchone()[0]
            if total>4*1024*1024:
                for identifier,length in db.execute('SELECT id,length(payload) FROM records ORDER BY pending,updated').fetchall():
                    if total<=4*1024*1024:break
                    db.execute('DELETE FROM records WHERE id=?',(identifier,));total-=length
        return True
    def store(self,db,record,event,context,stamp):
        remote=self.remote
        namespace='' if record['kind']=='task' else str(context['collector_revision'])+':'+str(context.get('version',''))+':'
        identifier=hashlib.sha256((remote.state['installation']+':'+namespace+record['key']).encode()).hexdigest()
        old=db.execute('SELECT payload,sequence FROM records WHERE id=?',(identifier,)).fetchone();sequence=(old[1] if old else 0)+1
        if old:
            previous=json.loads(old[0])
            if record['mode']=='counter':record['metrics']={k:min(10**15,v+previous['metrics'].get(k,0)) for k,v in record['metrics'].items()}
        payload={'id':identifier,'kind':record['kind'],'name':record['name'],'result':record['result'],'metrics':record['metrics'],'dimensions':record['dimensions'],'frames':record['frames'],
                 'occurred_at':event['timestamp'],'collector_revision':context['collector_revision'],'content_revision':context['content_revision'],'base_version':2,'version':str(context.get('version','unknown'))[:40]}
        if old and record['mode']=='counter':payload['occurred_at']=previous['occurred_at']
        if old and {k:v for k,v in previous.items() if k!='sequence'}==payload:return
        payload['sequence']=sequence
        db.execute('INSERT OR REPLACE INTO records VALUES(?,?,?,?,?)',(identifier,json.dumps(payload,separators=(',',':')),sequence,1,stamp))
    def flush(self):
        remote=self.remote;policy=remote.content.get('telemetry',{})
        if not policy.get('enabled',False):return
        with closing(self.database()) as db:
            rows=db.execute('SELECT id,payload,sequence FROM records WHERE pending=1 ORDER BY updated LIMIT 40').fetchall()
            if not rows:return
            remote.request('/v2/telemetry',{'installation':remote.state['installation'],'platform':remote.platform,'version':str(remote.context.get('version','unknown'))[:40],
                'build':str(remote.context.get('build',''))[:32],'channel':str(remote.context.get('channel','unknown'))[:32],'records':[json.loads(row[1]) for row in rows]})
            with db:
                for identifier,_,sequence in rows:db.execute('UPDATE records SET pending=0 WHERE id=? AND sequence=?',(identifier,sequence))
