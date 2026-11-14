"""Exercise the Simpfun contract: start.sh, SERVER_PORT and no console input."""
from pathlib import Path
import argparse, functools, hashlib, http.server, json, os, signal, subprocess, tempfile, threading, time, urllib.request

parser=argparse.ArgumentParser();parser.add_argument('--release',required=True);args=parser.parse_args()
repository=Path(__file__).resolve().parent.parent;release=Path(args.release).resolve()
assert os.name=='posix','Run on Linux'
remote='https://github.com/sx050907-png/probably-stolen-server/releases/latest/download'
downloads=[];fail_download=False;bad_archive=False
class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self,*_):pass
    def do_GET(self):
        downloads.append(self.path)
        if fail_download:self.send_error(404);return
        if self.path=='/install.sh':
            body=(release/'install.sh').read_text('utf-8').replace(remote,base).encode()
            self.send_response(200);self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body);return
        if bad_archive and self.path.endswith('.tar.gz'):
            body=b'corrupted release fixture';self.send_response(200);self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body);return
        return super().do_GET()
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(release)))
base='http://127.0.0.1:'+str(server.server_port)
threading.Thread(target=server.serve_forever,daemon=True).start()
work=repository/'work';work.mkdir(exist_ok=True)
checks=0
def check(value,name):
    global checks
    checks+=1
    if not value:raise AssertionError(name)
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
with tempfile.TemporaryDirectory(prefix='simpfun start ',dir=work) as temporary:
    target=Path(temporary);(target/'data').mkdir()
    (target/'config.env').write_text('NIGHTSTUD_PORT=1\nNIGHTSTUD_SCHEME=http\nNIGHTSTUD_ALLOW_LEGACY_LOGIN=0\nNIGHTSTUD_ALLOW_GUEST=0\n','utf-8')
    sentinel=target/'data/operator-file.txt';sentinel.write_text('preserve existing operator data','utf-8')
    protected={p:digest(p) for p in (target/'config.env',sentinel)}
    import socket
    with socket.socket() as port_socket:port_socket.bind(('127.0.0.1',0));port=port_socket.getsockname()[1]
    environment=dict(os.environ,SERVER_PORT=str(port),NIGHTSTUD_DATA_DIR=str(target/'data'),NIGHTSTUD_LOG_DIR=str(target/'logs'))
    environment.pop('NIGHTSTUD_MAINTENANCE',None)
    environment.pop('NIGHTSTUD_WEB_TEST_UI',None)
    def entry():
        (target/'start.sh').write_text((repository/'deploy/start.sh').read_text('utf-8').replace(remote,base),'utf-8')
    def spawn():return subprocess.Popen(['bash','start.sh'],cwd=target,env=environment,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,start_new_session=True)
    def stop(process):
        if process.poll() is None:os.killpg(process.pid,signal.SIGTERM)
        output=process.communicate(timeout=15)[0].decode('utf-8',errors='replace')
        return output
    def run():
        process=spawn()
        for _ in range(300):
            if process.poll() is not None:raise AssertionError('start.sh stopped: '+stop(process)[-2000:])
            try:
                with urllib.request.urlopen('http://127.0.0.1:'+str(port)+'/health',timeout=.3) as response:health=json.load(response)
                return process,health
            except Exception:time.sleep(.1)
        raise AssertionError('No server health: '+stop(process)[-2000:])
    try:
        entry();process,health=run()
        check(health['version']=='0.6.12','start.sh downloads and starts the published server with no console input')
        check(all((target/'app/wwwroot'/name).is_file() for name in ('index.html','app.js','style.css')),'deployed archive contains actual test files for default-off verification')
        for route in ('/','/index.html','/app.js','/style.css'):
            try:
                with urllib.request.urlopen('http://127.0.0.1:'+str(port)+route,timeout=2) as response:status=response.status
            except urllib.error.HTTPError as error:status=error.code
            check(status==404,'published server blocks browser test UI '+route)
        stop(process)
        check(all(digest(p)==v for p,v in protected.items()),'bootstrap preserves configuration and operator data')
        check(not list(target.glob('.install-*')),'installer staging is removed before the persistent service starts')
        before=len(downloads);process,health=run();stop(process)
        check(len(downloads)==before,'normal start works without contacting the release host')
        (target/'backup.request').touch();process,health=run();stop(process)
        check(not (target/'backup.request').exists() and len(list((target/'backups').glob('accounts-*.db')))==1,'backup marker creates one real SQLite backup and continues startup')
        process,health=run();stop(process)
        check(len(list((target/'backups').glob('accounts-*.db')))==1,'consumed backup request is not repeated')
        entry();(target/'update.request').touch();process,health=run();stop(process)
        check(len(downloads)>before and not (target/'update.request').exists(),'update marker is consumed only after successful installation')
        check(all(digest(p)==v for p,v in protected.items()),'update preserves existing configuration and data')
        entry();(target/'update.request').touch();old_binary=digest(target/'app/NightStud.Server');bad_archive=True
        process=spawn();output=process.communicate(timeout=15)[0]
        check(process.returncode!=0 and (target/'update.request').exists(),'failed checksum preserves the update request for retry')
        check(digest(target/'app/NightStud.Server')==old_binary and all(digest(p)==v for p,v in protected.items()),'failed update does not replace the program or player data')
        bad_archive=False;fail_download=True;entry();process=spawn();process.communicate(timeout=15)
        check(process.returncode!=0 and digest(target/'app/NightStud.Server')==old_binary,'unreachable download stops clearly without damaging the current server')
    finally:
        if 'process' in locals() and process.poll() is None:stop(process)
        server.shutdown();server.server_close()
proof='PASS '+str(checks)+' start.sh-only checks: first install, allocated port, no stdin, offline restart, marker update and backup, no repeated backup, protected data, checksum failure, unreachable download.'
(work/'start-entry-success.txt').write_text(proof,'utf-8');print(proof)
