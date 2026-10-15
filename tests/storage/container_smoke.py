"""Exercise gateway + relay + Caddy HTTPS with real mutual TLS, no certificate-validation bypasses."""
import hashlib,json,os,secrets,socket,ssl,subprocess,tempfile,time,urllib.request,uuid
from pathlib import Path

def main():
 repository=Path(__file__).resolve().parents[2]
 dotnet=os.environ.get('STORAGE_TEST_DOTNET','dotnet')
 initializer=os.environ.get('STORAGE_TEST_INITIALIZER')
 project=repository/'src/OneCBackup.StorageBridge'
 names=['onec-storage-'+uuid.uuid4().hex[:12] for _ in range(4)]
 network,hub,edge,gateway=names
 image=os.environ.get('STORAGE_TEST_IMAGE','onec-storage-bridge:ci')
 containers=[]
 with tempfile.TemporaryDirectory(prefix='onec-storage-test-') as temporary:
  root=Path(temporary);config=root/'config';data=root/'data';data.mkdir()
  with socket.socket() as available: available.bind(('127.0.0.1',0));port=available.getsockname()[1]
  def run(command,**kwargs):return subprocess.run(command,check=True,capture_output=True,text=True,**kwargs)
  def docker(*args):return run(['docker',*args])
  initialize=[initializer] if initializer else [dotnet,'run','--project',str(project),'--']
  run([*initialize,'initialize',str(config),'wss://localhost:'+str(port)])
  if initializer:run([dotnet,'build',str(project)])
  options=json.loads((config/'gateway.json').read_text());options['Endpoint']='wss://hub:8443'
  (config/'gateway.json').write_text(json.dumps(options))
  run(['openssl','req','-x509','-newkey','rsa:2048','-days','2','-nodes','-subj','/CN=localhost',
   '-addext','subjectAltName=DNS:localhost,DNS:hub,IP:127.0.0.1','-addext','basicConstraints=critical,CA:TRUE',
   '-keyout',str(root/'edge.key'),'-out',str(root/'edge.crt')])
  (root/'Caddyfile').write_text('{\n admin off\n auto_https off\n}\n:8443 {\n tls /certs/edge.crt /certs/edge.key\n reverse_proxy 127.0.0.1:7087\n}\n')
  environment=dict(os.environ,SSL_CERT_FILE=str(root/'edge.crt'))
  def client(*args):return run([dotnet,'run','--no-build','--project',str(project),'--',*args],env=environment)
  try:
   docker('network','create',network)
   docker('run','-d','--name',hub,'--network',network,'--network-alias','hub','--user',str(os.getuid())+':'+str(os.getgid()),
    '-p','127.0.0.1:'+str(port)+':8443','-v',str(config/'hub.json')+':/config/hub.json:ro',image,'hub','/config/hub.json');containers.append(hub)
   docker('run','-d','--name',edge,'--network','container:'+hub,'-v',str(root/'Caddyfile')+':/etc/caddy/Caddyfile:ro',
    '-v',str(root/'edge.crt')+':/certs/edge.crt:ro','-v',str(root/'edge.key')+':/certs/edge.key:ro','caddy:2.10.2');containers.append(edge)
   docker('run','-d','--name',gateway,'--network',network,'--user',str(os.getuid())+':'+str(os.getgid()),
    '--read-only','--tmpfs','/tmp','--cap-drop','ALL','--security-opt','no-new-privileges',
    '-e','SSL_CERT_FILE=/certs/edge.crt','-e','NO_PROXY=hub','-e','no_proxy=hub',
    *sum((['-v',str(config/file)+':/config/'+file+':ro'] for file in ['gateway.json','gateway.pfx','agent.pem','certificate-password.txt','access-token.txt']),[]),
    '-v',str(data)+':/backups','-v',str(root/'edge.crt')+':/certs/edge.crt:ro',image);containers.append(gateway)
   assert docker('exec',hub,'sh','-c','ls -1 /config').stdout.strip()=='hub.json'
   deadline=time.monotonic()+45
   context=ssl.create_default_context(cafile=str(root/'edge.crt'))
   while True:
    try:
     with urllib.request.urlopen('https://localhost:'+str(port)+'/healthz',context=context,timeout=3) as response:assert json.load(response)['ready']
     break
    except (OSError,urllib.error.URLError):
     if time.monotonic()>deadline:raise
     time.sleep(1)
   while True:
    try:client('probe',str(config/'client-pairing.json'));break
    except subprocess.CalledProcessError as error:
     if time.monotonic()>deadline:print(error.stderr[-1000:]);raise
     time.sleep(1)
   source=root/'source.zip';source.write_bytes(secrets.token_bytes(1024*1024))
   job=str(uuid.uuid4());object_id=str(uuid.uuid4())
   result=json.loads(client('put',str(config/'client-pairing.json'),job,object_id,str(source)).stdout.strip())
   assert result['Length']==source.stat().st_size
   assert result['Sha256'].lower()==hashlib.file_digest(source.open('rb'),'sha256').hexdigest()
   downloaded=root/'downloaded.zip'
   client('get',str(config/'client-pairing.json'),job,object_id,str(result['Length']),result['Sha256'],str(downloaded))
   assert downloaded.read_bytes()==source.read_bytes()
   docker('restart',gateway)
   deadline=time.monotonic()+30
   while True:
    try:client('stat',str(config/'client-pairing.json'),job,object_id);break
    except subprocess.CalledProcessError:
     if time.monotonic()>deadline:raise
     time.sleep(1)
   client('delete',str(config/'client-pairing.json'),job,object_id,str(result['Length']),result['Sha256'])
   assert not list(data.rglob('*.bin'))
   print(json.dumps({'Success':True,'ContainerGateway':True,'HttpsReverseProxy':True,'MutualTls':True,
    'VerifiedUploadDownloadDelete':True,'RestartKeepsCopy':True,'ExternalPairingInitializer':bool(initializer),'LetsEncryptIssuanceTested':False}))
  except Exception:
   for name in containers:
    logs=subprocess.run(['docker','logs',name],capture_output=True,text=True)
    # Do not print Caddy request/ACME logging or secret-bearing configuration.
    sensitive=[(config/'access-token.txt').read_text().strip(),(config/'certificate-password.txt').read_text().strip(),json.loads((config/'client-pairing.json').read_text())['Certificate']]
    text=(logs.stdout+logs.stderr)[-5000:]
    for value in sensitive:text=text.replace(value,'[redacted]')
    print(name,text)
   raise
  finally:
   for name in reversed(containers):subprocess.run(['docker','rm','-f',name],capture_output=True)
   subprocess.run(['docker','network','rm',network],capture_output=True)

if __name__=='__main__':main()
