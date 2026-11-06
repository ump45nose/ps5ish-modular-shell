"""生成本项目原创界面音效；默认音乐由独立授权资源恢复脚本提供。"""
from pathlib import Path
import math
import struct
import wave
import random


def generate():
    """合成带快速衰减的提示音，只写固定 artifacts 构建目录。"""
    output = Path(__file__).resolve().parent.parent / 'artifacts/themes/audio'
    output.mkdir(parents=True, exist_ok=True)
    rate = 44100
    # 回退上一轮玻璃和弦，使用短促空气感瞬态与低幅滑音，避免导航像播放音符。
    # 这些是独立合成的风格替代音，不使用来源不明的 PS5 原机录音。
    for name, duration, notes in [('move', .12, (980,)), ('switch', .22, (620, 930)), ('confirm', .32, (740, 1110, 1480)), ('back', .24, (820, 615)), ('startup', 3.2, (185, 277.18, 369.99, 554.37, 739.99)), ('trophy', 2.2, (659.25, 987.77, 1318.51, 1975.53))]:
        samples = bytearray()
        rng = random.Random(73)
        smooth_noise = 0.0
        for i in range(int(rate * duration)):
            t = i / rate
            value = 0.0
            for index, frequency in enumerate(notes):
                phase_time = t - (0 if name == 'move' else index * .025)
                if phase_time < 0:
                    continue
                # 首尾平滑衰减消除瞬时跳变，低幅谐波保留清晰度而不过分刺耳。
                envelope = min(1, phase_time / (.3 if name in ('startup', 'trophy') else .008)) * math.exp(-phase_time * (.9 if name in ('startup', 'trophy') else 28))
                envelope *= min(1, max(0, (duration - t) / .025))
                value += (.36 if name in ('startup', 'trophy') else .24) * envelope * math.sin(2 * math.pi * (frequency * phase_time - (0 if name in ('startup', 'trophy') else 170) * phase_time * phase_time)) / len(notes)
            # 固定种子和平滑噪声确保每次构建一致；尾部窗避免截断产生电流爆音。
            smooth_noise = .72 * smooth_noise + .28 * rng.uniform(-1, 1)
            value += .012 * smooth_noise * min(1, t / .008) * math.exp(-t * 50) * min(1, (duration-t)/.02)
            samples.extend(struct.pack('<h', int(max(-1, min(1, value)) * 32767)))
        with wave.open(str(output / (name + '.wav')), 'wb') as stream:
            stream.setparams((1, 2, rate, 0, 'NONE', 'not compressed'))
            stream.writeframes(samples)


if __name__ == '__main__':
    generate()
