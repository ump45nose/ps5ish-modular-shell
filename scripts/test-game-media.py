"""在 artifacts 临时目录验证素材导入，不访问账号、网络或真实用户库。"""
import importlib.util
import json
import tempfile
from unittest.mock import patch
from pathlib import Path


def main():
    """复核自动补图、哈希去重与人工锁定优先级。"""
    root=Path(__file__).resolve().parents[1]
    spec=importlib.util.spec_from_file_location('prepare_media',root/'scripts/prepare-game-media.py')
    module=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    with tempfile.TemporaryDirectory(dir=root/'artifacts',prefix='media-import-test-') as temporary:
        user=Path(temporary)/'user'
        steam=Path(temporary)/'steam'
        local=steam/'appcache/librarycache/123'
        user.mkdir()
        local.mkdir(parents=True)
        (local/'library_hero.jpg').write_bytes(b'first background')
        game={'IsInstalled':True,'Platform':'Steam','SourceId':'123','ItemId':'test','Title':'Test'}
        (user/'game-catalog.json').write_text(json.dumps([game]),encoding='utf-8')
        module.prepare(user,steam,False)
        index=module.read_json(user/'assets.json',{})
        entry=index['items'][0]
        extra=user/'extra.jpg'
        extra.write_bytes(b'second background')
        entry['backgrounds'].extend([entry['backgrounds'][0],str(extra)])
        (user/'assets.json').write_text(json.dumps(index),encoding='utf-8')
        module.prepare(user,steam,False)
        index=module.read_json(user/'assets.json',{})
        assert len(index['items'][0]['backgrounds'])==2
        assert len(index['items'][0]['source']['files'])==2
        print('PASS 自动素材重新导入保留新背景并按内容去重')
        index['items'][0]['locked']=True
        index['items'][0]['source']={'manual':'user'}
        (user/'assets.json').write_text(json.dumps(index),encoding='utf-8')
        module.prepare(user,steam,False)
        assert module.read_json(user/'assets.json',{})==index
        print('PASS 人工锁定素材与来源保持不变')
        # 清单来自外部，验证域名拒绝和加密轨道拒绝均发生在媒体合并之前。
        for address in ('http://video.steamstatic.com/a', 'https://steamstatic.com.evil.example/a'):
            try:
                module.validate_url(address)
                raise AssertionError('未拒绝不受信任素材地址')
            except ValueError:
                pass
        print('PASS 素材协议与域名边界')

        def encrypted_download(address, destination, limit):
            """提供最小加密清单，测试失败时不会发布半成品。"""
            content = '#EXTM3U\n#EXT-X-STREAM-INF:RESOLUTION=854x480\nvideo.m3u8' if destination.name=='master.m3u8' else '#EXTM3U\n#EXT-X-KEY:METHOD=AES-128\n#EXT-X-ENDLIST'
            destination.write_text(content, encoding='utf-8')

        with patch.object(module, 'download', encrypted_download), patch.object(module.shutil, 'which', return_value='ffmpeg'):
            try:
                module.cache_hls('https://video.steamstatic.com/master.m3u8', user/'trailer.mp4')
                raise AssertionError('未拒绝加密预告')
            except ValueError as error:
                assert '未加密' in str(error)
        assert not (user/'trailer.mp4').exists()
        print('PASS 加密预告拒绝且不发布半成品')
        # 原声只引用明确安装目录，同时拒绝清单路径逃逸。
        (steam/'steamapps').mkdir(exist_ok=True)
        soundtrack=steam/'steamapps/common/Test/game/main_menu/music/mp3'
        soundtrack.mkdir(parents=True)
        (soundtrack/'01_main_theme.mp3').write_bytes(b'original soundtrack fixture')
        install_manifest=steam/'steamapps/appmanifest_123.acf'
        install_manifest.write_text('"installdir" "Test"',encoding='utf-8')
        assert module.local_soundtrack(steam,'123').name=='01_main_theme.mp3'
        install_manifest.write_text('"installdir" "../../outside"',encoding='utf-8')
        assert module.local_soundtrack(steam,'123') is None
        print('PASS 本机原声匹配与安装路径边界')
        if module.shutil.which('ffmpeg'):
            # 用一秒本地音视频构造 fMP4 点播，复核真实拼接而非仅模拟返回值。
            fixture=user/'fixture'
            fixture.mkdir()
            module.subprocess.run([module.shutil.which('ffmpeg'),'-nostdin','-v','error','-y',
                '-f','lavfi','-i','color=c=blue:s=320x180:r=10:d=1','-f','lavfi','-i','anullsrc=r=44100:cl=stereo',
                '-t','1','-c:v','libx264','-c:a','aac','-f','hls','-hls_segment_type','fmp4',str(fixture/'video.m3u8')],
                check=True,capture_output=True,timeout=30,cwd=fixture,creationflags=getattr(module.subprocess,'CREATE_NO_WINDOW',0))
            (fixture/'master.m3u8').write_text('#EXTM3U\n#EXT-X-STREAM-INF:RESOLUTION=320x180\nvideo.m3u8',encoding='utf-8')

            def fixture_download(address,destination,limit):
                """复用生产下载边界，网络响应以本地公开格式样本代替。"""
                module.validate_url(address)
                source=fixture/Path(module.urllib.parse.urlsplit(address).path).name
                assert source.stat().st_size<=limit
                module.shutil.copyfile(source,destination)

            with patch.object(module,'download',fixture_download):
                module.cache_hls('https://video.steamstatic.com/master.m3u8',user/'trailer.mp4')
            assert (user/'trailer.mp4').stat().st_size>1000
            print('PASS 真实 fMP4 点播含音轨合并并原子落盘')


if __name__=='__main__':
    main()
