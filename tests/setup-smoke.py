#!/usr/bin/env python3
"""Exercise real CLI setup with isolated homes; fake systemctl/docker, no services started."""
import json, os, pathlib, stat, subprocess, sys, tempfile
sdk = str(pathlib.Path(sys.argv[1]).resolve())
dll = str(pathlib.Path(sys.argv[2]).resolve())
with tempfile.TemporaryDirectory(prefix='soulfuse-setup-') as tmp:
    base = pathlib.Path(tmp)
    root = base / 'managed'
    share = base / 'media $ spaced'; share.mkdir()
    home = base / 'home'; home.mkdir()
    env = os.environ | {'HOME': str(home)}
    def cli(*args, data=None, ok=True):
        result = subprocess.run([sdk, dll, *args, '--home', str(root)], input=data, text=True, capture_output=True, env=env)
        if (result.returncode == 0) != ok:
            raise AssertionError(f'{args}: {result.stdout}\n{result.stderr}')
        return result
    cli('setup', data='alice\nsecret-password\nweb-secret\nbob\n-\n'+str(share)+'\nMedia\n-\n20\n'+str(base/'mount')+'\n')
    config = json.loads((root/'config.json').read_text())
    compose = json.loads((root/'compose.yaml').read_text())
    assert config['users'] == []
    assert compose['services']['slskd']['environment'] == {'SLSKD_API_KEY': '${SLSKD_API_KEY:?Missing SLSKD_API_KEY}'}
    assert compose['services']['slskd']['user'] == '${SLSKFS_UID:?Missing SLSKFS_UID}:${SLSKFS_GID:?Missing SLSKFS_GID}'
    saved_env = dict(line.split('=',1) for line in (root/'.env').read_text().splitlines() if line and not line.startswith('#'))
    assert saved_env['SLSKD_API_KEY'] == (root/'api-key').read_text().strip()
    assert saved_env['SLSKFS_UID'] == str(os.getuid())
    assert saved_env['SLSKFS_GID'] == str(os.getgid())
    assert compose['services']['slskd']['volumes'][1]['source'] == str(share).replace('$', '$$')
    assert compose['services']['slskd']['ports'][0] == '127.0.0.1:5030:5030'
    for name in ['.env', 'api-key', 'slskd/slskd.yml', 'config.json', 'installation.json']:
        assert stat.S_IMODE((root/name).stat().st_mode) == 0o600, name
    original = (root/'slskd/slskd.yml').read_text()
    cli('setup', ok=False)
    assert (root/'slskd/slskd.yml').read_text() == original
    cli('friends','add','charlie.test')
    assert 'charlie.test' in cli('friends','list').stdout
    cli('users','add','bob')
    assert json.loads((root/'config.json').read_text())['users'] == ['bob']
    cli('users','remove','bob')
    assert json.loads((root/'config.json').read_text())['users'] == []
    cli('shares','add','Books',str(share))
    assert '[Books]/soulfuse-shares/Books' in cli('shares','list').stdout
    cli('shares','remove','Books')
    assert '[Books]' not in cli('shares','list').stdout
    listing = json.loads(cli('shares','list').stdout)
    assert listing[0]['name'] == 'Media' and listing[0]['hostPath'] == str(share)
    cli('shares','remove','Media')
    assert json.loads(cli('shares','list').stdout) == []
    assert share.is_dir(), 'Removing a share must preserve host files'
    cli('shares','remove','Media',ok=False)
    # Shared parent bindings must survive removing one child declaration.
    (root/'compose.yaml').write_text('services:\n  slskd:\n    volumes:\n      - ./slskd:/app\n      - '+str(share).replace('$','$$')+':/shared:ro\n')
    (root/'slskd/slskd.yml').write_text("shares:\n  directories:\n    - '[Books]/shared/books'\n    - '[Music]/shared/music'\n")
    listing = json.loads(cli('shares','list').stdout)
    assert listing[0]['hostPath'] == str(share/'books')
    cli('shares','remove','Books')
    assert '/shared:ro' in (root/'compose.yaml').read_text()
    assert len(json.loads(cli('shares','list').stdout)) == 1
    # An exact adopted binding can be removed; /app remains protected.
    (root/'slskd/slskd.yml').write_text("shares:\n  directories:\n    - '[Media]/shared'\n    - '[App]/app'\n")
    cli('shares','remove','Media')
    assert '/shared:ro' not in (root/'compose.yaml').read_text()
    cli('shares','remove','App')
    assert './slskd:/app' in (root/'compose.yaml').read_text()
    # Real service definitions, mocked lifecycle binaries: no Docker or systemd required.
    fake = base/'fake'; fake.mkdir()
    log = base/'calls'
    for name in ['docker','systemctl']:
        executable=fake/name
        executable.write_text('#!/usr/bin/env python3\nimport os,sys,json\nwith open(os.environ["CALL_LOG"],"a") as f: f.write(json.dumps({"args":sys.argv,"hasKey":bool(os.environ.get("SLSKD_API_KEY")),"uid":os.environ.get("SLSKFS_UID")})+"\\n")\n')
        executable.chmod(0o700)
    env |= {'PATH':str(fake)+':'+env['PATH'],'CALL_LOG':str(log)}
    cli('enable'); cli('start'); cli('status'); cli('stop'); cli('disable')
    calls=[json.loads(line) for line in log.read_text().splitlines()]
    docker_calls=[c for c in calls if pathlib.Path(c['args'][0]).name == 'docker']
    assert docker_calls and all(c['hasKey'] and c['uid']==str(os.getuid()) for c in docker_calls)
    assert any(c['args'][1:3]==['--user','start'] for c in calls)
    # Adoption reuses existing files and does not modify cache state or compose/YAML.
    (root/'installation.json').unlink()
    state=root/'state';state.mkdir();(state/'downloads-cache.json').write_text('sentinel-cache')
    before={name:(root/name).read_bytes() for name in ['compose.yaml','slskd/slskd.yml','api-key','state/downloads-cache.json']}
    cli('setup','--adopt',data=str(base/'mount')+'\n')
    for name,content in before.items(): assert (root/name).read_bytes()==content, name
    assert (root/'config.before-setup.json').exists()
    assert (root/'bin/SlskFs.dll').exists()
    print('PASS setup, permissions, repeat refusal, friends/users/shares, lifecycle arguments, adoption preservation')
