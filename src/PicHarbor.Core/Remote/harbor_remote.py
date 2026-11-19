"""PicHarbor remote base v1. Publisher-trusted scripts; not an untrusted-code sandbox.

No Google client, credentials or image library is imported here. Native hosts
serialize calls and supply only user-selected attachments and local preferences.
"""
import base64
from contextlib import closing
import datetime
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sqlite3
import sys
import time
import uuid
import zipfile
from harbor_telemetry import Bridge, run_collector, checked_record, safe_facts

ORIGIN = 'https://api.akihito.dpdns.org'
PUBLIC = ('iuedfHRaV44kApnWvkzd4I8845b5jR3fF-J4ZhT-4d8', 'VnI6wfaBJwEcODfDuEm8Hw1ck7ucrVV6xfMRma3YH2U')
KEY_ID = 'picharbor-2026'
_CONTENT_CACHE = {}
EVENT_NAMES={'launch','active','backup_success','backup_failure','transfer_success','transfer_failure','tool_use','script_failure','update_failure','duration_under_1m','duration_1m_10m','duration_over_10m','size_under_10mb','size_10mb_100mb','size_over_100mb'}
MAX_FILE = 5 * 1024 * 1024
P = 0xffffffff00000001000000000000000000000000ffffffffffffffffffffffff
N = 0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551
G = (0x6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296,
     0x4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5)

def _add(a, b):
    if a is None: return b
    if b is None: return a
    x,y=a; u,v=b
    if x == u and (y+v) % P == 0: return None
    m=((3*x*x-3)*pow(2*y,-1,P) if a == b else (v-y)*pow(u-x,-1,P)) % P
    q=(m*m-x-u) % P
    return q,(m*(x-q)-y) % P

def _mul(n, p):
    r=None
    while n:
        if n & 1: r=_add(r,p)
        p=_add(p,p); n >>= 1
    return r

def verify(envelope):
    """Verify exact UTF-8 payload bytes with strict DER ECDSA/SHA-256."""
    if envelope.get('key_id') != KEY_ID: raise ValueError('SIGNATURE_KEY')
    raw=base64.b64decode(envelope['payload'],validate=True)
    sig=base64.b64decode(envelope['signature'],validate=True)
    if len(raw)>262144 or len(sig)<8 or sig[0]!=48 or sig[1]!=len(sig)-2: raise ValueError('SIGNATURE_FORMAT')
    def integer(at):
        if sig[at]!=2: raise ValueError('SIGNATURE_FORMAT')
        size=sig[at+1]; end=at+2+size
        value=sig[at+2:end]
        if not value or value[0]&128 or size>33 or (size>1 and value[0]==0 and not value[1]&128):raise ValueError('SIGNATURE_FORMAT')
        return int.from_bytes(value,'big'),end
    r,end=integer(2); s,end=integer(end)
    if end!=len(sig) or not 0<r<N or not 0<s<N: raise ValueError('SIGNATURE_FORMAT')
    q=tuple(int.from_bytes(base64.urlsafe_b64decode(x+'='),'big') for x in PUBLIC)
    w=pow(s,-1,N); z=int.from_bytes(hashlib.sha256(raw).digest(),'big')
    point=_add(_mul(z*w%N,G),_mul(r*w%N,q))
    if point is None or point[0]%N!=r:raise ValueError('SIGNATURE_INVALID')
    data=json.loads(raw)
    if data.get('schema')!=1 or data.get('app')!='picharbor' or data.get('min_base',1)>2 or not isinstance(data.get('revision'),int):raise ValueError('CONTENT_INCOMPATIBLE')
    if len(data.get('script',''))>131072:raise ValueError('SCRIPT_LIMIT')
    return data

def _write(path, data):
    tmp=path.with_suffix('.new');tmp.write_text(json.dumps(data,ensure_ascii=False),encoding='utf-8');os.replace(tmp,path)

def _read(path, default):
    try:return json.loads(path.read_text(encoding='utf-8'))
    except (OSError,ValueError):return default

def mask_email(email):
    if not isinstance(email,str) or '@' not in email:return ''
    local,domain=email.rsplit('@',1)
    # Short local parts never reveal most of the account name.
    hint=(local[:2]+'***'+local[-2:]) if len(local)>=7 else (local[:1]+'***' if len(local)>=3 else '***')
    return hint+'@'+(domain.lower() if domain.lower() in ('gmail.com','googlemail.com') else '[domain]')

# Every key is a local native preference, never a setting chosen from a remote
# method name. Tuples mean numeric limits, lists mean enumerated values.
COMMON={'language':['system','zh-CN','zh-HK','zh-TW','zh-Hans','zh-Hant','yue-Hant-HK','en','en-US'],
 'appearance':['system','light','dark'],'palette':['system','lavender','sage','peach','blue','rose','teal','amber'],
 'quality':['original','saver','quota'],'pipelineMode':['automatic','custom','balanced','battery','fast','conservative'],
 'automatic':bool,'wifiOnly':bool,'syncAlbums':bool,'archiveEnabled':bool,'archiveDays':(1,90),
 'activitySource':['all','manual','automatic'],'activityStatus':['all','unfinished','completed','failed'],'activityGrouping':['batch','day'],
 'archiveSource':['all','manual','automatic']}
REGISTRY={
 'ios':{**{k:v for k,v in COMMON.items() if k not in ('palette','activityGrouping')},'language':['system','zh-Hans','en','yue-Hant-HK'],'live':['automatic','motion','applePair','appleReconcile'],'edits':['both','original','current'],
        'pipelineOverrides':dict,'executionOverrides':dict},
 'android':{**{k:v for k,v in COMMON.items() if k not in ('appearance','automatic','wifiOnly','syncAlbums','activityGrouping')},'theme':['system','light','dark'],'auto':bool,'wifi':bool,'restart':bool,
        'albumMode':['none','folder'],'livePhotoPairing':['automatic','original','stepwise','motion'],
        'concurrency':(1,3),'timeout':(30,120),'retries':(0,3),'pipelineLimits':str,'executionPolicy':str},
 'windows':{'CurrentLanguage':['zh-CN','zh-HK','en-US'],'GroupMediaByDevice':bool,'SyncExifToLastWriteTime':bool,'SyncExifToCreationTime':bool,
        'AutoCompleteLivePhotoPair':bool,'AutoCompleteAaeSidecar':bool,'AutoCompleteRawJpg':bool,
        'GooglePhotosThreads':(1,16),'GooglePhotosUnlimitedQuality':bool,'GooglePhotosStorageSaver':bool,
        'GooglePhotosLiveStrategy':['motion','applePair','appleReconcile'],'GooglePhotosSkipExistingFilenames':bool,
        'GooglePhotosTimeoutSeconds':(10,300),'GooglePhotosAutoRetryAttempts':(0,5),'GooglePhotosRetryDelaySeconds':(0,60),
        'PreviewMuted':bool,'AndroidBackupMinFileSizeKb':(0,1048576),'AndroidBackupIgnoreSmallImages':bool,
        'AndroidBackupIncludePhotos':bool,'AndroidBackupIncludeVideos':bool,'ReadTimeoutSeconds':(10,300)}}

def valid(key,value,platform):
    if value is None and platform=='ios' and key in ('pipelineOverrides','executionOverrides'):return True
    rule=REGISTRY.get(platform,{}).get(key)
    if rule is None:return False
    if isinstance(rule,list):return value in rule
    if isinstance(rule,tuple):return type(value) in (int,float) and rule[0]<=value<=rule[1]
    if rule is dict or rule is str:
        if not isinstance(value,rule):return False
        if value=='':return True
        try:obj=json.loads(value) if rule is str else value
        except ValueError:return False
        limits={'preparation':(1,3),'lookup':(1,4),'upload':(1,12),'commit':(1,4),'confirmation':(1,4),'bufferMB':(128,8192),'buffer_mb':(128,8192),
                'retries':(0,10),'querySeconds':(15,300),'query_seconds':(15,300),'commitSeconds':(15,600),'commit_seconds':(15,600),'idleSeconds':(30,1800),'idle_seconds':(30,1800),'fileMinutes':(10,1440),'file_minutes':(10,1440),'cloudMinutes':(10,240),'cloud_minutes':(10,240)}
        expected=({'preparation','lookup','upload','commit','confirmation','bufferMB' if platform=='ios' else 'buffer_mb'} if key in ('pipelineOverrides','pipelineLimits') else {'retries','querySeconds','commitSeconds','idleSeconds','fileMinutes','cloudMinutes'} if platform=='ios' else {'retries','query_seconds','commit_seconds','idle_seconds','file_minutes','cloud_minutes'})
        return isinstance(obj,dict) and (not obj or set(obj)==expected) and all(k in limits and type(v) is int and limits[k][0]<=v<=limits[k][1] for k,v in obj.items())
    return isinstance(value,rule)

class Remote:
    def __init__(self, home, context=None):
        self.root=Path(home)/'Remote';self.root.mkdir(parents=True,exist_ok=True)
        self.state=_read(self.root/'state.json',{})
        self.state.setdefault('installation',str(uuid.uuid4()))
        self.context=context or {};self.platform=self.context.get('platform','ios')
        self.context={k:v for k,v in self.context.items() if k in ('platform','version','build','channel','language','system','device_class')}
        self.content={};self.envelope=None
        for name in ('content.json','previous.json'):
            try:
                path=self.root/name
                if not path.exists():continue
                info=path.stat();stamp=(str(path),info.st_mtime_ns,info.st_size)
                cached=_CONTENT_CACHE.get(stamp)
                if cached:self.envelope,self.content=cached;break
                envelope=_read(path,{})
                if envelope:
                    self.content=verify(envelope);self.envelope=envelope
                    if len(_CONTENT_CACHE)>=8:_CONTENT_CACHE.clear()
                    _CONTENT_CACHE[stamp]=(envelope,self.content);break
            except (ValueError,KeyError,TypeError,IndexError):continue

    def save(self):_write(self.root/'state.json',self.state)

    def request(self,path,payload=None,method=None,data=None,headers=None):
        import requests
        session=requests.Session()
        # A distinct session: never borrow Google auth/cookies or app proxy credentials.
        try:
            r=session.request(method or ('POST' if payload is not None else 'GET'),ORIGIN+path,json=payload,data=data,headers={'User-Agent':'PicHarbor-Remote/1','Accept':'application/json',**(headers or {})},timeout=(5,12),allow_redirects=False)
            if not r.ok:raise ValueError('SERVICE_'+str(r.status_code))
            if len(r.content)>524288:raise ValueError('RESPONSE_LIMIT')
            return r.json()
        finally:session.close()

    def sync(self):
        envelope=self.request('/v1/apps/picharbor/manifest.json');data=verify(envelope)
        if data['revision']<self.content.get('revision',0):raise ValueError('CONTENT_ROLLBACK')
        if data['revision']==self.content.get('revision',0):return {'ok':True,'changed':False}
        # Compile/validate before activation; startup code is not executed now.
        compile(data.get('script',''),'remote-content','exec')
        self.validating=True
        previous_content=self.content;previous_failures=self.state.get('script_failures',0)
        self.content=data;self.state['script_failures']=0
        checked=self.script('build_more')
        self.content=previous_content;self.state['script_failures']=previous_failures
        self.validating=False
        if data.get('telemetry_script'):
            result=run_collector(data['telemetry_script'],{'name':'environment.snapshot','fields':{},'id':'preview','timestamp':int(time.time()),'day':datetime.date.today().isoformat()},dict(self.context),{})
            for record in result.get('records',[]):checked_record(record)
        if checked is None:raise ValueError('CONTENT_VALIDATION_FAILED')
        if self.envelope:_write(self.root/'previous.json',self.envelope)
        _write(self.root/'content.json',envelope);self.content=data;self.envelope=envelope
        self.state['script_failures']=0
        self.observe('remote.fetch',{'success':True,'content_revision':data['revision'],'operation':'activate'})
        return {'ok':True,'changed':True}

    def script(self,entry,action='',values=None):
        if not self.content:return {'title':'更多','items':[]} if entry=='build_more' else None
        fallback=self.state.get('script_failures',0)>=3
        source=self.content.get('script','')
        if fallback:
            try:source=verify(_read(self.root/'previous.json',{})).get('script','')
            except (ValueError,KeyError,TypeError,IndexError):return {'title':'更多','items':[]} if entry=='build_more' else None
        # Publisher-owned Python with a deliberately small host surface.
        # This is a capability discipline, not a claim of hostile-code isolation.
        safe={k:__builtins__[k] for k in ('str','int','float','bool','len','dict','list','tuple','set','min','max','sum','range','enumerate','zip','round','abs','isinstance','sorted') } if isinstance(__builtins__,dict) else {k:getattr(__builtins__,k) for k in ('str','int','float','bool','len','dict','list','tuple','set','min','max','sum','range','enumerate','zip','round','abs','isinstance','sorted')}
        namespace={'__builtins__':safe};deadline=time.monotonic()+2;steps=0
        def trace(frame,event,arg):
            nonlocal steps
            frame.f_trace_opcodes = True
            steps+=1
            if steps>100000 or time.monotonic()>deadline:raise TimeoutError('SCRIPT_BUDGET')
            return trace
        old=sys.gettrace()
        try:
            sys.settrace(trace);exec(compile(source,'remote-content','exec'),namespace)
            fn=namespace.get(entry)
            result=fn(action,values or {},dict(self.context)) if entry=='handle_action' and fn else fn(dict(self.context)) if fn else None
            if result is not None:validate_page(result)
            if not fallback:self.state['script_failures']=0
            return result
        except Exception:
            self.state['script_failures']=self.state.get('script_failures',0)+1
            self.observe('remote.execute',{'success':False,'operation':entry,'error_code':'SCRIPT_FAILED'});return None
        finally:sys.settrace(old)

    def observe(self,name,fields=None,key=''):
        if getattr(self,'_observing',False) or getattr(self,'validating',False):return False
        self._observing=True
        try:
            ok=Bridge(self).observe(name,fields,key)
            if ok:self.state['collector_failures']=0
            return ok
        except Exception:
            self.state['collector_failures']=self.state.get('collector_failures',0)+1
            self.state['collector_error']='COLLECTOR_FAILED'
            if self.state['collector_failures']>=3:
                try:
                    previous=verify(_read(self.root/'previous.json',{}));current=self.content
                    self.content={**current,'telemetry_script':previous.get('telemetry_script',''),'telemetry':previous.get('telemetry',{})}
                    try:return Bridge(self).observe(name,fields,key)
                    finally:self.content=current
                except Exception:pass
            return False
        finally:self._observing=False

    def event(self,name,event_id='',duration_ms=None,size=None):
        # Compatibility surface only. The remote collector decides whether
        # legacy facts are useful; no built-in histogram or counting policy.
        fields={'operation':str(name)}
        if duration_ms is not None:fields['duration_ms']=duration_ms
        if size is not None:fields['bytes']=size
        self.observe('legacy.event',fields,event_id)

    def lifecycle(self,phase,fields=None):
        stamp=int(time.time()*1000);session=self.state.setdefault('session',{})
        if phase=='start':
            if session.get('foreground'):self.observe('app.previous_exit',{},session.get('id',''))
            session={'id':str(uuid.uuid4()),'foreground':True,'since':stamp,'active_ms':0};self.state['session']=session
        elif not session:
            session={'id':str(uuid.uuid4()),'foreground':False,'since':stamp,'active_ms':0};self.state['session']=session
        if session.get('foreground'):session['active_ms']+=max(0,min(300000,stamp-session.get('since',stamp)))
        session['since']=stamp
        if phase in ('start','foreground'):session['foreground']=True
        elif phase=='background':session['foreground']=False
        self.observe('app.'+phase,{**(fields or {}),'active_ms':session['active_ms']},session['id'])

    def flush(self):
        today=datetime.date.today().isoformat()
        for day,data in list(self.state.get('daily',{}).items()):
            if data.get('sent')!=data['events']:
                self.request('/v1/statistics',{'id':data['id'],'installation':self.state['installation'],'platform':self.platform,'version':data.get('version',str(self.context.get('version','unknown'))[:40]),'day':day,'events':data['events'],'environment':{k:str(self.context.get(k,''))[:32] for k in ('language','system','device_class')}})
                data['sent']=dict(data['events'])
            del self.state['daily'][day]
            self.save()
        policy=self.content.get('telemetry',{})
        interval=max(60,min(3600,policy.get('flush_seconds',900)))
        if time.time()-self.state.get('telemetry_last_flush',0)>=interval:
            try:
                Bridge(self).flush();self.state['telemetry_last_flush']=time.time();self.save()
            except Exception:pass
        for item in list(self.state.get('pending_feedback',[])):
            result=self.request('/v1/feedback',item['payload']);self.upload_files(result['id'],item['payload']['request_id'],item.get('files',[]))
            self.state['pending_feedback'].remove(item);self.save()

    def resolve(self,current,scope):
        device_keys=set(REGISTRY['windows']) if self.platform=='windows' else {'language','theme','palette','archiveEnabled','archiveDays','activitySource','activityStatus','activityGrouping','archiveSource'}
        result={'ok':True,'values':{},'personal':{},'forced':[],'hints':{}}
        for part,key in (({k:v for k,v in current.items() if k in device_keys},'device'),({k:v for k,v in current.items() if k not in device_keys},scope)):
            value=self._resolve(part,key)
            for hint in value['hints'].values():hint['scope']=value['scope']
            for field in ('values','personal','hints'):result[field].update(value[field])
            result['forced'].extend(value['forced']);result['scope']=value['scope']
        return result

    def _resolve(self,current,scope):
        platform=self.platform;scope=hashlib.sha256(str(scope).encode()).hexdigest()[:24]
        profiles=self.state.setdefault('profiles',{});old=profiles.setdefault(platform+':'+scope,{'personal':{},'forced':{},'receipts':[],'seen':[]})
        personal=old['personal'];effective={k:v for k,v in current.items() if k in REGISTRY.get(platform,{})};hints={};forced={}
        for key,value in effective.items():
            if key not in old['forced']:personal[key]=value
            elif key in personal:effective[key]=personal[key]
        for policy in self.content.get('policies',[]):
            if policy.get('mode') not in ('once','force'):continue
            for key,value in policy.get('values',{}).get(platform,{}).items():
                if key not in effective or not valid(key,value,platform):continue
                receipt=str(policy.get('id',''))+':'+str(policy.get('revision',1))+':'+key
                if policy['mode']=='force':effective[key]=value;forced[key]=value
                elif receipt not in old['receipts']:
                    effective[key]=value;personal[key]=value;old['receipts'].append(receipt)
                if policy.get('notify_user',False) and receipt not in old['seen']:hints[key]={'id':receipt,'message':str(policy.get('message') or '此设置已由应用配置调整。')[:400]}
        old['forced']=forced;self.save()
        return {'ok':True,'values':effective,'personal':personal,'forced':list(forced),'hints':hints,'scope':platform+':'+scope}

    def hint(self,scope,receipt):
        profile=self.state.get('profiles',{}).get(scope)
        if profile and receipt not in profile['seen']:profile['seen'].append(receipt)

    def feedback(self,values,files):
        body=str(values.get('body','')).strip();contact=str(values.get('contact','')).strip()
        if not body or len(body)>10000 or len(contact)>200:raise ValueError('INVALID_FEEDBACK')
        account_key=hashlib.sha256(str(values.get('email','')).encode()).hexdigest()
        ref=self.state.setdefault('account_refs',{}).setdefault(account_key,str(uuid.uuid4())) if values.get('email') else ''
        self.observe('feedback.stage',{'operation':'feedback.submit'})
        payload={'request_id':str(uuid.uuid4()),'installation':self.state['installation'],'account_ref':ref[:64],
                 'account_hint':mask_email(values.get('email','')),'platform':self.platform,'version':str(self.context.get('version','unknown'))[:40], 'body':body,'contact':contact}
        queue=self.state.setdefault('pending_feedback',[])
        if len(queue)>=10:raise ValueError('FEEDBACK_QUEUE_FULL')
        # Only these explicitly user-submitted fields may contain personal text.
        checked=[]
        for f in files[:4]:
            path=Path(f['path']);kind=f['kind']
            if not path.is_file() or not 0<path.stat().st_size<=MAX_FILE:raise ValueError('ATTACHMENT_SIZE')
            if kind=='diagnostic':
                data=sanitize_diagnostic(path.read_bytes());target=self.root/(str(uuid.uuid4())+'.zip');target.write_bytes(data)
                checked.append({'id':str(uuid.uuid4()),'path':str(target),'kind':kind,'mime':'application/zip'})
            elif kind=='screenshot' and f.get('mime') in ('image/jpeg','image/png'):
                # Native hosts decode/re-encode selected images without metadata first.
                target=self.root/(str(uuid.uuid4())+'.jpg');target.write_bytes(path.read_bytes());checked.append({'id':str(uuid.uuid4()),'path':str(target),'kind':kind,'mime':f['mime']})
        if sum(Path(f['path']).stat().st_size for f in checked)>4*MAX_FILE:raise ValueError('ATTACHMENT_SIZE')
        item={'payload':payload,'files':checked};queue.append(item);self.save()
        try:
            result=self.request('/v1/feedback',payload);self.upload_files(result['id'],payload['request_id'],checked);queue.remove(item)
            self.observe('feedback.stage',{'operation':'feedback.sent','success':True})
            return {'ok':True,'id':result['id'],'queued':False}
        except Exception:
            self.observe('feedback.stage',{'operation':'feedback.queued','success':False})
            return {'ok':True,'id':payload['request_id'],'queued':True}

    def upload_files(self,fid,rid,files):
        for f in files:
            path=Path(f['path'])
            if not path.exists():continue
            data=path.read_bytes();ticket=self.request('/v1/attachments',{'feedback':fid,'request_id':rid,'file_id':f.setdefault('id',str(uuid.uuid4())),'kind':f['kind'],'mime':f['mime'],'bytes':len(data)})
            if not ticket.get('uploaded'):self.request('/v1/attachments/'+ticket['id'],method='PUT',data=data,headers={'Content-Type':f['mime'],'Authorization':'Bearer '+ticket['token']})
            path.unlink(missing_ok=True)

def validate_page(page,depth=0):
    if depth>8 or not isinstance(page,dict) or len(json.dumps(page))>65536:raise ValueError('PAGE_LIMIT')
    if 'items' not in page: # Optional startup UI is a title/message alert.
        if set(page)-{'title','message'}:raise ValueError('INVALID_STARTUP_UI')
        return
    items=page['items']
    if not isinstance(items,list) or len(items)>80:raise ValueError('PAGE_LIMIT')
    for item in items:
        if item.get('type') not in ('text','button','input','multiline','number','toggle','select','group','result','attachments','loading'):raise ValueError('UNKNOWN_COMPONENT')
        if item.get('url') and not str(item['url']).startswith('https://'):raise ValueError('INVALID_URL')
        if item.get('capability') not in (None,'feedback.submit'):raise ValueError('UNKNOWN_CAPABILITY')
        if 'items' in item:validate_page(item,depth+1)

def sanitize_diagnostic(data):
    """Rebuild a recognized diagnostic archive using an allowlist of fields.

    Never include original names, arbitrary text files or opaque binary blobs.
    """
    safe=[]
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            if len(archive.infolist())>200 or sum(x.file_size for x in archive.infolist())>50*1024*1024:raise ValueError('DIAGNOSTIC_LIMIT')
            for entry in archive.infolist():
                if not entry.filename.endswith('.jsonl') or entry.file_size>10*1024*1024:continue
                for line in archive.read(entry).splitlines():
                    try:row=json.loads(line)
                    except ValueError:continue
                    value={}
                    allowed=('schema','schema_version','level','producer','event','timestamp','time','ts','ts_utc','error_code','phase','duration_ms','elapsed_ms','bytes','count','status','attempt','retry_count','http_status','source_read_bytes','query_ms','upload_ms','commit_ms','duration_bucket','size_bucket')
                    fields={**{k:v for k,v in row.get('attrs',{}).items() if k in allowed},**row} if isinstance(row.get('attrs'),dict) else row
                    for k in allowed:
                        v=fields.get(k)
                        if type(v) in (int,float,bool):value[k]=v
                        elif isinstance(v,str) and re.fullmatch(r'[A-Za-z0-9_.:+TZ -]{1,100}',v) and '@' not in v and 'http' not in v:value[k]=v
                    if value:safe.append(value)
                    if len(safe)>=20000:break
    except zipfile.BadZipFile:raise ValueError('UNRECOGNIZED_DIAGNOSTIC')
    if not safe:raise ValueError('UNRECOGNIZED_DIAGNOSTIC')
    out=io.BytesIO()
    with zipfile.ZipFile(out,'w',zipfile.ZIP_DEFLATED) as archive:archive.writestr('diagnostics.jsonl','\n'.join(json.dumps(x) for x in safe))
    return out.getvalue()

def dispatch(home,action,payload='{}'):
    try:
        v=json.loads(payload) if isinstance(payload,str) else payload;r=Remote(home,v.get('context',{}))
        if action=='sync':
            result=r.sync()
            r.observe('remote.fetch',{'success':True,'operation':'check'})
            try:r.flush()
            except Exception:pass
        elif action=='flush':r.flush();result={'ok':True}
        elif action=='startup':
            r.lifecycle('start');r.observe('environment.snapshot',{'os_version':r.context.get('system','unknown'),'language':r.context.get('language','system'),'channel':r.context.get('channel','unknown'),'device_class':r.context.get('device_class','unknown')})
            result={'ok':True,'ui':r.script('on_start')}
        elif action=='observe':result={'ok':True,'collected':r.observe(v.get('name',''),v.get('fields',{}),v.get('id',''))}
        elif action=='lifecycle':r.lifecycle(v.get('phase','heartbeat'),v.get('fields',{}));result={'ok':True}
        elif action=='more':
            r.observe('feature.action',{'tool_id':'more.open'})
            if not r.content:
                try:r.sync()
                except Exception:pass
            result={'ok':True,'page':r.script('build_more') or {'title':'更多','items':[]}}
        elif action=='action':r.observe('feature.action',{'tool_id':v.get('action','unknown')});result={'ok':True,'page':r.script('handle_action',v.get('action',''),v.get('values',{})) or {'title':'更多','items':[]}}
        elif action=='resolve':result=r.resolve(v.get('values',{}),v.get('scope','device'))
        elif action=='hint':r.hint(v.get('scope',''),v.get('receipt',''));result={'ok':True}
        elif action=='event':r.event(v.get('name'),v.get('id',''),v.get('duration_ms'),v.get('bytes'));result={'ok':True}
        elif action=='feedback':result=r.feedback(v.get('values',{}),v.get('files',[]))
        elif action=='update':
            version=str(r.context.get('version',''))
            field='updates' if '-' in version or version.startswith('0.') else 'stable_updates'
            result={'ok':True,'update':r.content.get(field,r.content.get('updates',{})).get(r.platform)}
            r.observe('update.stage',{'operation':'available' if result['update'] else 'current','success':True})
        else:raise ValueError('UNKNOWN_ACTION')
        r.save();return json.dumps(result,ensure_ascii=False)
    except Exception as e:
        code=str(e) if isinstance(e,(ValueError,TimeoutError)) and re.fullmatch('[A-Z_0-9]+',str(e)) else 'REMOTE_UNAVAILABLE'
        try:
            if action=='sync':r.observe('remote.fetch',{'success':False,'operation':'check','error_code':code});r.save()
        except Exception:pass
        return json.dumps({'ok':False,'error':code})

if __name__=='__main__':
    if hasattr(sys.stdin,'reconfigure'):sys.stdin.reconfigure(encoding='utf-8')
    if hasattr(sys.stdout,'reconfigure'):sys.stdout.reconfigure(encoding='utf-8')
    request=json.load(sys.stdin)
    print(dispatch(request['home'],request['action'],request.get('payload',{})))
