import os, sys, shutil, atexit, logging, logging.handlers, re, signal
import http.server, urllib.request, urllib.error, subprocess, threading, time, json, pathlib

ROOT=pathlib.Path(__file__).resolve().parent

cfg_file = ROOT/'cameras.local.json'
if not cfg_file.exists():
    cfg_file = ROOT/'cameras.json'
if not cfg_file.exists():
    sys.exit("Config file not found. Copy cameras.example.json to cameras.local.json and add your camera details.")

CONFIG=json.loads(cfg_file.read_text(encoding='utf-8'))
target_cid = os.environ.get('CAMERA_ID')
if target_cid:
    CAMERA=next((c for c in CONFIG['cameras'] if str(c.get('id'))==target_cid), None)
else:
    CAMERA=CONFIG['cameras'][0] if CONFIG['cameras'] else None

if not CAMERA:
    sys.exit("No camera configured. Check cameras.local.json.")

WEB_PORT=int(CAMERA['webPort'])
API_PORT=int(CAMERA['apiPort'])

FFMPEG=shutil.which('ffmpeg') or CONFIG.get('ffmpeg','ffmpeg')
DOTNET=shutil.which('dotnet') or str(ROOT/'tools/dotnet/dotnet.exe')
DECODER_DLL=os.environ.get('V380_DECODER_DLL') or str(ROOT/'tools/decoder/V380Decoder.dll')

frame=None

frame_time=0

sequence=0

lock=threading.Condition()

stop_event=threading.Event()
processes=set()
process_lock=threading.Lock()
status={'state':'starting','restarts':0,'lastError':None}
logger=logging.getLogger('camera.'+str(CAMERA['id']))
logger.setLevel(logging.INFO)

log_dir = ROOT/'logs'
log_dir.mkdir(parents=True, exist_ok=True)
log_handler=logging.handlers.RotatingFileHandler(log_dir/(str(CAMERA['id'])+'-capture.log'),maxBytes=1048576,backupCount=2,encoding='utf-8')
log_handler.setFormatter(logging.Formatter('%(asctime)s %(message)s'))
logger.addHandler(log_handler)
def diagnostic(line):
 text=line.decode('utf-8',errors='replace') if isinstance(line,bytes) else str(line)
 for secret in (CAMERA.get('password'),CAMERA.get('username')):
  if secret: text=text.replace(secret,'[redacted]')
 text=re.sub(r'ticket[=:]\s*\d+','ticket=[redacted]',text,flags=re.I)
 logger.info(text.strip()[:2000])
def log_pipe(pipe):
 try:
  for line in iter(pipe.readline,b''): diagnostic(line)
 finally: pipe.close()
def launch(args,**kwargs):
 proc=subprocess.Popen(args,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),**kwargs)
 with process_lock: processes.add(proc)
 return proc
def terminate(proc):
 if proc is None:return
 try:
  if proc.poll() is None: proc.terminate()
  try:proc.wait(timeout=3)
  except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=3)
 except (OSError,subprocess.TimeoutExpired):pass
 with process_lock:processes.discard(proc)
def cleanup():
 stop_event.set()
 with process_lock:owned=list(processes)
 for proc in owned:terminate(proc)
atexit.register(cleanup)
def capture():
 global frame,frame_time,sequence
 failures=0
 while not stop_event.is_set():
  dec=p=None;attempt_stop=threading.Event();before=sequence
  try:
   status['state']='connecting'
   if not pathlib.Path(DECODER_DLL).exists():
       status['lastError'] = f"Decoder DLL not found at {DECODER_DLL}. Set V380_DECODER_DLL."
       diagnostic(status['lastError'])
       time.sleep(3)
       continue
   dec=launch([DOTNET,DECODER_DLL,'--id',str(CAMERA['deviceId']),'--username',CAMERA['username'],'--password',CAMERA['password'],'--ip',CAMERA['host'],'--output','video','--quality',CAMERA.get('quality','sd'),'--enable-api','--http-port',str(API_PORT)],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
   threading.Thread(target=log_pipe,args=(dec.stderr,),daemon=True).start()
   p=launch([FFMPEG,'-hide_banner','-loglevel','warning','-f','h264','-i','pipe:0','-an','-vf','fps=5,scale=720:-2','-q:v','5','-f','image2pipe','-vcodec','mjpeg','pipe:1'],stdin=dec.stdout,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
   dec.stdout.close()
   threading.Thread(target=log_pipe,args=(p.stderr,),daemon=True).start()
   started=time.monotonic()
   def watchdog(proc,decoder,started,finished):
    while not finished.wait(1) and not stop_event.is_set():
     if proc.poll() is not None:break
     if decoder.poll() is not None or time.monotonic()-max(frame_time,started)>20:
      status['state']='reconnecting';status['lastError']='Video stalled; restarting connection'
      diagnostic(status['lastError']);terminate(proc);break
   threading.Thread(target=watchdog,args=(p,dec,started,attempt_stop),daemon=True).start()
   buf=b''
   while not stop_event.is_set():
    chunk=p.stdout.read1(65536)
    if not chunk:break
    buf+=chunk
    while True:
     start=buf.find(b'\xff\xd8');end=buf.find(b'\xff\xd9',max(start,0))
     if start<0 or end<0:break
     jpg=buf[start:end+2];buf=buf[end+2:]
     with lock:
      frame=jpg;frame_time=time.monotonic();sequence+=1
      status['state']='streaming';status['lastError']=None;lock.notify_all()
    if len(buf)>8000000:raise RuntimeError('JPEG framing buffer exceeded limit')
  except Exception as exc:
   status['lastError']=type(exc).__name__+': video process failed';diagnostic(status['lastError'])
  finally:
   attempt_stop.set();terminate(p);terminate(dec)
  if stop_event.is_set():break
  status['state']='reconnecting';status['restarts']+=1
  failures=0 if sequence>before else min(failures+1,4)
  stop_event.wait(min(2**failures,15))

class Handler(http.server.BaseHTTPRequestHandler):

 def log_message(self,*args): pass

 def do_GET(self):

  path=self.path.split('?')[0]

  if path in ('/audio.mp3','/audio.wav'):

   # /audio.wav matches the Android app (A-law expanded to 16-bit PCM) so the
   # page has one audio element for both builds. /audio.mp3 is kept for anyone
   # still holding an older page.
   codec=['-codec:a','libmp3lame','-b:a','32k','-f','mp3'] if path=='/audio.mp3' else ['-codec:a','pcm_s16le','-f','wav']

   audio=launch([FFMPEG,'-hide_banner','-loglevel','error','-f','alaw','-ar','8000','-ac','1','-i',f'http://127.0.0.1:{API_PORT}/audio.alaw',*codec,'pipe:1'],stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)

   self.send_response(200);self.send_header('Content-Type','audio/mpeg' if path=='/audio.mp3' else 'audio/wav');self.send_header('Cache-Control','no-store');self.end_headers()

   try:

    while True:

     b=audio.stdout.read1(4096)

     if not b: break

     self.wfile.write(b);self.wfile.flush()

   except (BrokenPipeError,ConnectionResetError): pass

   finally: terminate(audio)

  elif self.path.split('?')[0]=='/stream.mjpg':

   self.send_response(200);self.send_header('Content-Type','multipart/x-mixed-replace; boundary=frame');self.send_header('Cache-Control','no-store');self.end_headers();last=-1

   try:

    while True:

     with lock:

      lock.wait_for(lambda: sequence!=last,timeout=10)

      if not frame or time.monotonic()-frame_time>10: continue

      jpg=frame;last=sequence

     self.wfile.write(b'--frame\r\nContent-Type: image/jpeg\r\nContent-Length: '+str(len(jpg)).encode()+b'\r\n\r\n'+jpg+b'\r\n');self.wfile.flush()

   except (BrokenPipeError,ConnectionResetError): pass

  elif self.path.split('?')[0]=='/snapshot':

   with lock: jpg=frame if time.monotonic()-frame_time<10 else None

   if not jpg: self.send_error(503,'Waiting for live video');return

   self.send_response(200);self.send_header('Content-Type','image/jpeg');self.send_header('Cache-Control','no-store');self.send_header('Content-Length',str(len(jpg)));self.end_headers();self.wfile.write(jpg)

  elif self.path=='/health':

   data=json.dumps({'frames':sequence,'videoFresh':bool(frame and time.monotonic()-frame_time<10),'frameAgeSeconds':round(time.monotonic()-frame_time,2) if frame else None,**status}).encode();self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(data)

  elif self.path.startswith('/api/clip'):

   self.stream_clip()

  elif self.path=='/':

   self.serve_page()

  elif self.path.startswith('/ui/'):

   self.serve_ui()

  elif self.path=='/favicon.ico':

   self.path='/ui/icons/icon-192.png';self.serve_ui()

  elif self.path.startswith('/api/'):

   self.proxy('GET')

  else: self.send_error(404)

 def do_POST(self):

  origin=self.headers.get('Origin')

  if origin and origin not in (f'http://127.0.0.1:{WEB_PORT}',f'http://localhost:{WEB_PORT}'):self.send_error(403);return

  if not self.path.startswith('/api/'):self.send_error(404);return

  self.proxy('POST')

 def stream_clip(self):

  # SD clips are muxed live and can take minutes to download, so the body is
  # relayed as it arrives instead of being buffered by the generic proxy.
  req=urllib.request.Request(f'http://127.0.0.1:{API_PORT}'+self.path,method='GET')

  try:

   with urllib.request.urlopen(req,timeout=60) as r:

    self.send_response(r.status);self.send_header('Content-Type',r.headers.get('Content-Type','video/mp4'));self.send_header('Cache-Control','no-store')

    disposition=r.headers.get('Content-Disposition')

    if disposition: self.send_header('Content-Disposition',disposition)

    self.end_headers()

    while True:

     chunk=r.read(65536)

     if not chunk: break

     self.wfile.write(chunk);self.wfile.flush()

  except urllib.error.HTTPError as err:

   body=err.read(65536);self.send_response(err.code);self.send_header('Content-Type',err.headers.get('Content-Type','text/plain'));self.end_headers();self.wfile.write(body)

  except (BrokenPipeError,ConnectionResetError): pass

  except (urllib.error.URLError,TimeoutError,OSError):

   try:

    self.send_error(503,'Camera clip unavailable')

   except Exception: pass

 def serve_page(self):

  cfg={'id':CAMERA['id'],'name':CAMERA['name'],'host':CAMERA['host'],'deviceId':CAMERA['deviceId'],'webPort':WEB_PORT,'quality':CAMERA.get('quality','sd'),'hasPtz':(CAMERA.get('capabilities') or {}).get('ptz')!='unsupported','homeUrl':'http://127.0.0.1:8080/','capabilities':CAMERA.get('capabilities') or {}}

  html=(ROOT/'ui'/'camera.html').read_text(encoding='utf-8').replace('__CAMERA_CONFIG__',json.dumps(cfg).replace('</','<\\/'))

  data=html.encode();self.send_response(200);self.send_header('Content-Type','text/html; charset=utf-8');self.send_header('Cache-Control','no-cache');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)

 def serve_ui(self):

  name=urllib.parse.unquote(self.path.split('?')[0][len('/ui/'):])

  ui=(ROOT/'ui').resolve();target=(ui/name).resolve()

  if not target.is_relative_to(ui) or not target.is_file():self.send_error(404);return

  try:data=target.read_bytes()
  except OSError:self.send_error(404);return

  types={'css':'text/css; charset=utf-8','js':'application/javascript; charset=utf-8','html':'text/html; charset=utf-8','svg':'image/svg+xml','json':'application/json','webmanifest':'application/manifest+json','png':'image/png'}

  self.send_response(200);self.send_header('Content-Type',types.get(target.suffix.lstrip('.').lower(),'application/octet-stream'));self.send_header('Cache-Control','no-cache');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)

 def proxy(self,method):

  try:

   req=urllib.request.Request(f'http://127.0.0.1:{API_PORT}'+self.path,data=self.rfile.read(min(int(self.headers.get('Content-Length','0')),65536)) if method=='POST' else None,method=method,headers={'Content-Type':self.headers.get('Content-Type','application/json')})

   with urllib.request.urlopen(req,timeout=60 if self.path.startswith(('/api/alarm','/api/recordings')) else 15) as r:

    data=r.read()

    self.send_response(r.status);self.send_header("Content-Type",r.headers.get("Content-Type","application/json"));self.send_header("Content-Length",str(len(data)));self.end_headers();self.wfile.write(data)

  except urllib.error.HTTPError as err:
   body=err.read(65536);self.send_response(err.code);self.send_header('Content-Type',err.headers.get('Content-Type','application/json'));self.end_headers();self.wfile.write(body)
  except (urllib.error.URLError,TimeoutError):
   self.send_error(503,'Camera control service reconnecting')

threading.Thread(target=capture,daemon=True).start()

server=http.server.ThreadingHTTPServer(('127.0.0.1',WEB_PORT),Handler)
try:server.serve_forever()
finally:cleanup();server.server_close()
