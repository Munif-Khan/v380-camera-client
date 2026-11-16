import http.server,json,pathlib,subprocess,os,sys,urllib.parse,urllib.request,urllib.error,threading,ipaddress
from concurrent.futures import ThreadPoolExecutor

ROOT=pathlib.Path(__file__).resolve().parent
MANAGER_PORT=8080
children={}
guard=threading.Lock()

def config_path():
    for name in ('cameras.local.json', 'cameras.json'):
        p = ROOT / name
        if p.exists(): return p
    return ROOT / 'cameras.local.json'

def config():
    p = config_path()
    if not p.exists():
        example = ROOT / 'cameras.example.json'
        if example.exists():
            return json.loads(example.read_text(encoding='utf-8'))
        return {'cameras': []}
    return json.loads(p.read_text(encoding='utf-8'))

def camera(cid):return next((c for c in config()['cameras'] if str(c['id'])==cid),None)
def url(c,path=''):return f'http://127.0.0.1:{int(c["webPort"])}{path}'

def start(c):
 if c['id'] in children and children[c['id']].poll() is None:return
 env=dict(os.environ,CAMERA_ID=str(c['id']))
 log_dir = ROOT/'logs'
 log_dir.mkdir(parents=True, exist_ok=True)
 log=open(log_dir/('camera-'+str(c['id'])+'.log'),'ab')
 children[c['id']]=subprocess.Popen([sys.executable,str(ROOT/'camera-web.py')],env=env,stdout=log,stderr=log,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))

def ensure_started(c):
 with guard:start(c)

def save_config(cfg):
 target = config_path()
 tmp=target.with_suffix('.tmp')
 tmp.write_text(json.dumps(cfg,indent=2),encoding='utf-8')
 tmp.replace(target)

def kill_tree(p):
 if not p or p.poll() is not None:return
 # The worker owns a decoder child. TerminateProcess (what terminate() does on
 # Windows) skips its cleanup, so ask Windows to end the whole tree.
 try:
  if os.name=='nt':
   subprocess.run(['taskkill','/PID',str(p.pid),'/T','/F'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=15)
  else:p.terminate()
 except Exception:
  try:p.kill()
  except Exception:pass
 try:p.wait(timeout=8)
 except Exception:pass

def stop(cid):
 kill_tree(children.pop(cid,None))

def probe(c):
 try:
  with urllib.request.urlopen(url(c,'/health'),timeout=2.5) as r:return json.loads(r.read())
 except Exception:return None

def describe(c,health):
 return {'id':c['id'],'name':c['name'],'host':c['host'],'deviceId':c['deviceId'],
  'webPort':c['webPort'],'quality':c.get('quality','sd'),'url':url(c),'username':c.get('username',''),
  # A worker can answer /health while the camera is unreachable, so only
  # count a camera as live when it is actually producing fresh video.
  'online':bool(health and health.get('videoFresh')),'health':health,
  'capabilities':c.get('capabilities') or {}}

def list_cameras():
 cams=config()['cameras']
 for c in cams:ensure_started(c)
 with ThreadPoolExecutor(max_workers=min(8,max(1,len(cams)))) as pool:states=list(pool.map(probe,cams))
 return [describe(c,h) for c,h in zip(cams,states)]

CONTENT_TYPES={'css':'text/css; charset=utf-8','js':'application/javascript; charset=utf-8',
 'html':'text/html; charset=utf-8','svg':'image/svg+xml','json':'application/json',
 'webmanifest':'application/manifest+json','png':'image/png','ico':'image/x-icon'}

class Handler(http.server.BaseHTTPRequestHandler):
 protocol_version='HTTP/1.1'
 def log_message(self,*a):pass

 def send_bytes(self,status,body,ctype,cache='no-cache',extra=None):
  self.send_response(status);self.send_header('Content-Type',ctype);self.send_header('Cache-Control',cache)
  self.send_header('Content-Length',str(len(body)))
  for k,v in (extra or {}).items():self.send_header(k,v)
  self.end_headers()
  try:self.wfile.write(body)
  except (BrokenPipeError,ConnectionResetError):pass

 def send_json(self,status,value):
  self.send_bytes(status,json.dumps(value).encode(),'application/json; charset=utf-8','no-store')

 def serve_static(self,name):
  # Only files inside ui/, never a path escape.
  ui=(ROOT/'ui').resolve()
  target=(ui/name).resolve()
  if not target.is_relative_to(ui) or not target.is_file():self.send_error(404);return
  try:body=target.read_bytes()
  except OSError:self.send_error(404);return
  self.send_bytes(200,body,CONTENT_TYPES.get(target.suffix.lstrip('.').lower(),'application/octet-stream'))

 def serve_home(self):
  try:body=(ROOT/'ui'/'home.html').read_bytes()
  except OSError:self.send_error(500,'UI files missing');return
  self.send_bytes(200,body,'text/html; charset=utf-8')

 def relay(self,c,path,ctype,timeout):
  # Forward one call to a camera worker and pass its body through unchanged.
  try:
   with urllib.request.urlopen(url(c,path),timeout=timeout) as r:
    self.send_bytes(r.status,r.read(),r.headers.get('Content-Type',ctype),'no-store')
  except urllib.error.HTTPError as err:
   self.send_bytes(err.code,err.read(65536),err.headers.get('Content-Type',ctype),'no-store')
  except Exception:
   self.send_error(503,'Camera worker unavailable')

 def do_GET(self):
  path=urllib.parse.urlsplit(self.path).path
  if path=='/':self.serve_home()
  elif path.startswith('/ui/'):self.serve_static(urllib.parse.unquote(path[len('/ui/'):]))
  elif path=='/favicon.ico':self.serve_static('icons/icon-192.png')
  elif path=='/api/cameras':self.send_json(200,list_cameras())
  elif path.startswith('/api/camera/'):
   # /api/camera/<id>/snapshot or /api/camera/<id>/health
   rest=path[len('/api/camera/'):]
   cid,_,leaf=rest.partition('/')
   c=camera(urllib.parse.unquote(cid))
   if c is None:self.send_error(404);return
   ensure_started(c)
   if leaf=='snapshot':self.relay(c,'/snapshot','image/jpeg',8)
   elif leaf=='health':self.relay(c,'/health','application/json',5)
   else:self.send_error(404)
  else:self.send_error(404)

 def do_POST(self):
  path=urllib.parse.urlsplit(self.path).path
  if path not in ('/add','/edit','/remove'):self.send_error(404);return
  if self.headers.get('Origin') not in (f'http://127.0.0.1:{MANAGER_PORT}',f'http://localhost:{MANAGER_PORT}'):
   self.send_error(403);return
  n=int(self.headers.get('Content-Length','0'))
  if n>8192:self.send_error(413);return
  f=urllib.parse.parse_qs(self.rfile.read(n).decode(),keep_blank_values=True)
  try:
   if path=='/remove':
    cid=f['id'][0]
    with guard:
     cfg=config();cfg['cameras']=[c for c in cfg['cameras'] if c['id']!=cid];save_config(cfg);stop(cid)
   elif path=='/add':
    name=f['name'][0].strip();host=f['host'][0].strip();device=int(f['deviceId'][0]);quality=f['quality'][0]
    ipaddress.ip_address(host)
    if not name or len(name)>80 or device<1 or quality not in ('sd','hd'):raise ValueError()
    with guard:
     cfg=config();idx=len(cfg['cameras'])
     if any(c['host']==host and int(c['deviceId'])==device for c in cfg['cameras']):raise ValueError('Camera already added')
     # Ports are per camera; skip any port already claimed by another entry.
     used={int(c['webPort']) for c in cfg['cameras']}|{int(c['apiPort']) for c in cfg['cameras']}
     web=8090
     while web in used or web+1 in used:web+=2
     c={'id':'camera-'+str(idx+1),'name':name,'host':host,'deviceId':device,
      'username':f['username'][0],'password':f.get('password',[''])[0],'quality':quality,
      'webPort':web,'apiPort':web+1,
      'capabilities':{'light':'unverified','audioListen':'unverified','ptz':'unknown','sdPlayback':'unverified','alarmSettings':'unverified'}}
     cfg['cameras'].append(c);save_config(cfg);start(c)
   else:
    cid=f['id'][0];name=f['name'][0].strip();host=f['host'][0].strip()
    device=int(f['deviceId'][0]);quality=f['quality'][0];password=f.get('password',[''])[0]
    ipaddress.ip_address(host)
    if not name or len(name)>80 or device<1 or quality not in ('sd','hd'):raise ValueError()
    with guard:
     cfg=config();c=next((x for x in cfg['cameras'] if x['id']==cid),None)
     if c is None:raise ValueError('Unknown camera')
     if any(x['id']!=cid and x['host']==host and int(x['deviceId'])==device for x in cfg['cameras']):raise ValueError('Camera already added')
     connection=(c['host'],int(c['deviceId']),c.get('username',''),c.get('password',''),c.get('quality','sd'))
     c.update({'name':name,'host':host,'deviceId':device,'username':f['username'][0],'quality':quality})
     if password:c['password']=password
     save_config(cfg)
     if connection!=(c['host'],int(c['deviceId']),c['username'],c.get('password',''),c.get('quality','sd')):
      stop(cid);start(c)
   self.send_response(303);self.send_header('Location','/');self.send_header('Content-Length','0');self.end_headers()
  except (ValueError,KeyError,IndexError):self.send_error(400,'Check the fields; the camera may already be added')

for c in config()['cameras']:start(c)
try:http.server.ThreadingHTTPServer(('127.0.0.1',MANAGER_PORT),Handler).serve_forever()
finally:
 for cid in list(children):stop(cid)
