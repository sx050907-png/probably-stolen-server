from pathlib import Path
import argparse, hashlib, shutil, subprocess, tarfile, zipfile

root=Path(__file__).resolve().parent.parent
parser=argparse.ArgumentParser()
parser.add_argument('--output',default='release')
parser.add_argument('--dotnet',default='dotnet')
parser.add_argument('--skip-build',action='store_true')
args=parser.parse_args()
output=Path(args.output).resolve();output.mkdir(parents=True,exist_ok=True)
for runtime in ('linux-x64','win-x64'):
    app=root/'artifacts'/runtime/'app';app.mkdir(parents=True,exist_ok=True)
    if not args.skip_build:
        subprocess.run([args.dotnet,'publish',str(root/'src/Server/NightStud.Server.csproj'),'-c','Release','-r',runtime,'--self-contained','true','-o',str(app),'-p:DebugType=None'],check=True)
    executable=app/('NightStud.Server.exe' if runtime.startswith('win') else 'NightStud.Server')
    assert executable.is_file(),str(executable)
    entries={}
    for file in app.rglob('*'):
        if file.is_file() and file.suffix!='.pdb':entries['app/'+file.relative_to(app).as_posix()]=file
    for name in ('start.cmd','start.ps1','start.sh','config.env.example'):entries[name]=root/'deploy'/name
    for name in ('README.md','LICENSE'):entries[name]=root/name
    for file in (root/'THIRD-PARTY').rglob('*'):
        if file.is_file():entries['THIRD-PARTY/'+file.relative_to(root/'THIRD-PARTY').as_posix()]=file
    for name in entries:
        assert not set(Path(name).parts)&{'data','logs','backups','certificates','bin','obj'} and Path(name).name!='config.env'
    if runtime.startswith('linux'):
        with tarfile.open(output/('nightstud-server-'+runtime+'.tar.gz'),'w:gz') as archive:
            for name,file in sorted(entries.items()):
                info=archive.gettarinfo(str(file),name);info.uid=info.gid=0;info.uname=info.gname='';info.mode=0o755 if name in ('app/NightStud.Server','start.sh') else 0o644
                with file.open('rb') as stream:archive.addfile(info,stream)
    else:
        with zipfile.ZipFile(output/('nightstud-server-'+runtime+'.zip'),'w',zipfile.ZIP_DEFLATED,compresslevel=4) as archive:
            for name,file in sorted(entries.items()):archive.write(file,name)
for name in ('install.sh','install.ps1'):shutil.copy2(root/'deploy'/name,output/name)
with zipfile.ZipFile(output/'nightstud-server-installer-windows.zip','w',zipfile.ZIP_DEFLATED) as archive:
    for name in ('install.cmd','install.ps1'):archive.write(root/'deploy'/name,name)
    archive.write(root/'README.md','README.md')
files=sorted(p for p in output.iterdir() if p.is_file() and p.name!='SHA256SUMS')
(output/'SHA256SUMS').write_text(''.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.name+'\n' for p in files),'utf-8')
for file in files:print(file.name,file.stat().st_size)
