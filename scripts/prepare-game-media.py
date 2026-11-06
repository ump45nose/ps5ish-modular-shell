"""从 SDK 导出的游戏目录关联本地素材，可选下载官方公开背景与预告。"""
import hashlib
import argparse
import datetime
import json
import shutil
import re
import subprocess
import tempfile
from concurrent.futures import ThreadPoolExecutor
import urllib.parse
import urllib.request
from pathlib import Path


def read_json(path, default):
    """兼容 Windows UTF-8 BOM，缺失文件才采用默认值。"""
    return json.loads(path.read_text(encoding='utf-8-sig')) if path.exists() else default


def validate_url(url):
    """校验清单及其间接引用，禁止跳转到非官方素材域。"""
    host = urllib.parse.urlsplit(url).hostname or ''
    if not url.startswith('https://') or not any(host == d or host.endswith('.'+d) for d in ('steampowered.com','steamstatic.com','steamcontent.com','steamcdn-a.akamaihd.net')):
        raise ValueError('素材域名不在允许范围')


def download(url, destination, limit):
    """只接受官方 HTTPS 素材域，限制单文件大小并在完成后原子提交。"""
    validate_url(url)
    if destination.exists():
        return
    temporary = destination.with_suffix(destination.suffix+'.tmp')
    try:
        with urllib.request.urlopen(url, timeout=15) as response, temporary.open('wb') as target:
            validate_url(response.geturl())
            expected = response.headers.get('Content-Length')
            if expected and int(expected)>limit:
                raise ValueError('素材超过限额')
            size = 0
            while True:
                block = response.read(65536)
                if not block:
                    break
                size += len(block)
                if size>limit:
                    raise ValueError('素材超过限额')
                target.write(block)
            if not size or expected and size != int(expected):
                raise ValueError('素材长度不完整')
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def cache_hls(url, destination):
    """将公开的未加密 HLS 点播预告缓存为 WPF 可播放的本地 MP4。"""
    if destination.exists():
        return
    ffmpeg = shutil.which('ffmpeg')
    if not ffmpeg:
        raise ValueError('HLS 预告缓存需要 ffmpeg')
    with tempfile.TemporaryDirectory(dir=destination.parent, prefix='trailer-') as temporary:
        root = Path(temporary)

        def manifest(address, name):
            """有界下载清单；相对引用均以实际清单地址解析。"""
            path = root/name
            download(address, path, 1024*1024)
            return path.read_text(encoding='utf-8-sig').splitlines()

        lines = manifest(url, 'master.m3u8')
        variants = []
        audio = None
        for i, line in enumerate(lines):
            if line.startswith('#EXT-X-MEDIA:') and 'TYPE=AUDIO' in line:
                match = re.search(r'URI="([^"]+)"', line)
                if match:
                    audio = urllib.parse.urljoin(url, match[1])
            if line.startswith('#EXT-X-STREAM-INF:'):
                size = re.search(r'RESOLUTION=\d+x(\d+)', line)
                if size and i+1 < len(lines) and not lines[i+1].startswith('#'):
                    variants.append((int(size[1]), urllib.parse.urljoin(url, lines[i+1])))
        suitable = [v for v in variants if v[0] <= 480]
        if not suitable:
            raise ValueError('没有可缓存的 480p HLS 轨道')
        video = max(suitable)[1]
        remaining = 128*1024*1024
        inputs = []
        for number, address in enumerate([video] + ([audio] if audio else [])):
            track = manifest(address, 'track-'+str(number)+'.m3u8')
            if '#EXT-X-ENDLIST' not in track or any(s.startswith('#EXT-X-KEY') for s in track):
                raise ValueError('仅缓存未加密的完整点播预告')
            duration = sum(float(s.split(':',1)[1].rstrip(',')) for s in track if s.startswith('#EXTINF:'))
            if duration > 600:
                raise ValueError('预告超过十分钟限额')
            segments = []
            for line in track:
                if line.startswith('#EXT-X-MAP:'):
                    match = re.search(r'URI="([^"]+)"', line)
                    if match:
                        segments.append(match[1])
                elif line and not line.startswith('#'):
                    segments.append(line)
            if not segments or len(segments) > 400 or any('BYTERANGE' in s for s in track):
                raise ValueError('不支持的预告分片清单')
            combined = root/('input-'+str(number)+'.mp4')
            with combined.open('wb') as output, ThreadPoolExecutor(max_workers=4) as pool:
                # 每批最多四片，均分剩余额度；网络并发不改变拼接顺序或总容量上限。
                for offset in range(0,len(segments),4):
                    batch=segments[offset:offset+4]
                    parts=[root/('part-'+str(number)+'-'+str(offset+i)) for i in range(len(batch))]
                    pending=[pool.submit(download,urllib.parse.urljoin(address,segment),part,remaining//len(batch)) for segment,part in zip(batch,parts)]
                    for future in pending:
                        future.result()
                    for part in parts:
                        remaining -= part.stat().st_size
                        with part.open('rb') as source:
                            shutil.copyfileobj(source, output)
                        part.unlink()
            inputs.extend(['-protocol_whitelist', 'file,pipe', '-i', str(combined)])
        # ffmpeg 只读取已验证的本地文件，禁止其自行访问网络或交互等待。
        result = root/'trailer.mp4'
        command = [ffmpeg, '-nostdin', '-v', 'error', '-y'] + inputs + ['-map','0:v:0']
        command += ['-map','1:a:0'] if audio else ['-map','0:a?']
        subprocess.run(command + ['-c','copy','-movflags','+faststart',str(result)], check=True,
                       capture_output=True, timeout=120, creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        if not result.exists() or not 0 < result.stat().st_size <= 128*1024*1024:
            raise ValueError('预告合并结果无效')
        result.replace(destination)


def local_soundtrack(steam_root, appid):
    """只关联已安装游戏的明确原声目录，不把语音、音效或预告音轨当作配乐。"""
    manifest=steam_root/'steamapps'/('appmanifest_'+appid+'.acf')
    if not manifest.is_file():
        return None
    match=re.search(r'"installdir"\s+"([^"]+)"',manifest.read_text(encoding='utf-8-sig'))
    if not match:
        return None
    common=(steam_root/'steamapps/common').resolve()
    install=(common/match[1]).resolve()
    if not install.is_relative_to(common):
        return None
    # 以下路径已在本机原始安装目录核对，不解析或解密游戏资源包。
    directories=[install/'game/main_menu/music/mp3',install/'game/soundtrack/original_game_soundtrack/MP3']
    files=[path for directory in directories if directory.is_dir() for path in directory.glob('*.mp3')]
    return sorted(files,key=lambda path:(0 if re.search(r'main.?theme|main.?title',path.name,re.I) else 1,path.name))[0] if files else None


def prepare(user_root, steam_root, online, app_ids=None):
    """首十款已安装 Steam 游戏按 SDK 顺序处理；已锁定及手动选择不被自动覆盖。"""
    catalog = read_json(user_root/'game-catalog.json', [])
    index_path = user_root/'assets.json'
    index = read_json(index_path, {'schema':1,'items':[]})
    if index.get('schema') != 1:
        raise ValueError('不支持的素材索引')
    entries = {(e['provider'],e['sourceInstance'],e['itemId']):e for e in index['items']}
    games = [g for g in catalog if g['IsInstalled'] and g.get('Platform') == 'Steam' and g.get('SourceId') not in ('431960','993090','228980')][:10]
    if app_ids:
        games=[g for g in games if g['SourceId'] in app_ids]
    report = []
    for game in games:
        appid = game['SourceId']
        if not appid.isdigit():
            continue
        key = ('playnite','local',game['ItemId'])
        entry = entries.get(key, {'provider':key[0],'sourceInstance':key[1],'itemId':key[2]})
        if entry.get('locked'):
            report.append({'title':game['Title'],'status':'保留锁定素材'})
            continue
        local = steam_root/'appcache/librarycache'/appid
        cache = user_root/'game-media'/appid
        cache.mkdir(parents=True, exist_ok=True)
        if not entry.get('music'):
            music=local_soundtrack(steam_root,appid)
            if music:
                entry['music']=str(music)
                entry['musicSource']={'provider':'Installed game original soundtrack','path':str(music),'attribution':'原声归游戏发行商及作曲者所有，仅引用用户本机安装文件，不随主题分发','sha256':hashlib.sha256(music.read_bytes()).hexdigest()}
        backgrounds = []
        for source in (local/'library_hero.jpg',Path(game.get('BackgroundPath') or '')):
            if source.is_file():
                target = cache/('background-'+str(len(backgrounds))+source.suffix)
                if not target.exists():
                    shutil.copy2(source,target)
                backgrounds.append(str(target))
        # 完全相同的缓存副本不算两张壁纸。
        seen=set()
        backgrounds=[path for path in backgrounds if not ((digest:=hashlib.sha256(Path(path).read_bytes()).hexdigest()) in seen or seen.add(digest))]
        logo = local/'logo.png'
        if logo.exists() and not entry.get('logo'):
            target=cache/'logo.png'
            if not target.exists():
                shutil.copy2(logo,target)
            entry['logo']=str(target)
        failure = None
        if online:
            try:
                url='https://store.steampowered.com/api/appdetails?'+urllib.parse.urlencode({'appids':appid,'l':'schinese','cc':'CN'})
                with urllib.request.urlopen(url,timeout=15) as response:
                    data=json.load(response)[appid]
                if not data.get('success'):
                    raise ValueError('商店未返回可用素材')
                data=data['data']
                for i,shot in enumerate(data.get('screenshots',[])[:3]):
                    target=cache/('screenshot-'+str(i)+'.jpg')
                    download(shot['path_full'],target,8*1024*1024)
                    backgrounds.append(str(target))
                for movie in data.get('movies',[])[:1]:
                    video=movie.get('mp4',{}).get('480') or movie.get('webm',{}).get('480')
                    if video and not entry.get('video'):
                        target=cache/('trailer'+('.webm' if 'webm' in video else '.mp4'))
                        download(video.replace('http:','https:',1),target,128*1024*1024)
                        entry['video']=str(target)
                    elif movie.get('hls_h264') and not entry.get('video'):
                        target=cache/'trailer.mp4'
                        cache_hls(movie['hls_h264'], target)
                        entry['video']=str(target)
                        entry['videoSource']={'url':movie['hls_h264'],'title':movie.get('name'),'format':'HLS H264 cached MP4'}
            except Exception as error:
                failure=type(error).__name__+': '+str(error)
        automatic = entry.get('generatedBy') == 'prepare-game-media' or entry.get('source',{}).get('provider') == 'Steam local cache / official store'
        # 自动索引可补入新背景；用户原有字段和锁定项保持更高优先级。
        if not entry.get('backgrounds') or automatic:
            candidates = list(entry.get('backgrounds',[])) + backgrounds
            seen = set()
            entry['backgrounds'] = []
            for path in candidates:
                if not Path(path).is_file():
                    continue
                digest = hashlib.sha256(Path(path).read_bytes()).hexdigest()
                if digest not in seen:
                    seen.add(digest)
                    entry['backgrounds'].append(path)
        if automatic or not entry.get('source'):
            entry['generatedBy']='prepare-game-media'
            entry['source']={'provider':'Steam local cache / official store','appId':appid,'url':'https://store.steampowered.com/app/'+appid,'fetchedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'attribution':'游戏图片及预告归对应发行商所有，仅作本机展示缓存，不随主题再分发'}
            # 清单包含落盘证据，文件存在不再仅以布尔值表示。
            entry['source']['files']=[{'path':path,'bytes':Path(path).stat().st_size,'sha256':hashlib.sha256(Path(path).read_bytes()).hexdigest()} for path in entry.get('backgrounds',[])+[entry.get('logo'),entry.get('video'),entry.get('music')] if path and Path(path).is_file()]
        entries[key]=entry
        report.append({'title':game['Title'],'appId':appid,'backgrounds':len(entry.get('backgrounds',[])),'logo':bool(entry.get('logo')),'music':bool(entry.get('music')),'video':bool(entry.get('video')),'networkError':failure})
    index['items']=list(entries.values())
    if index_path.exists():
        shutil.copy2(index_path,index_path.with_suffix('.json.bak'))
    temporary=index_path.with_suffix('.json.tmp')
    temporary.write_text(json.dumps(index,ensure_ascii=False,indent=2),encoding='utf-8')
    temporary.replace(index_path)
    # 定向重试更新对应条目，保留首批其他游戏的核对记录。
    if app_ids:
        previous=read_json(user_root/'game-media-report.json',[])
        replaced={r.get('appId') for r in report}
        report=[r for r in previous if r.get('appId') not in replaced]+report
    (user_root/'game-media-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False,indent=2))


if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--user-root',type=Path,required=True)
    parser.add_argument('--steam-root',type=Path,required=True)
    parser.add_argument('--download',action='store_true')
    parser.add_argument('--app-id',action='append',help='仅重试首批清单中指定游戏，可重复提供')
    args=parser.parse_args()
    prepare(args.user_root,args.steam_root,args.download,args.app_id)
