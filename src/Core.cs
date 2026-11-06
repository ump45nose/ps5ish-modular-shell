using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ModularShell
{
    /// <summary>预览取消原型：每次选择生成新上下文；不声称已实现媒体播放。</summary>
    public sealed class PreviewCoordinator : IDisposable
    {
        private CancellationTokenSource pending;
        private int generation;

        /// <summary>立即发布静态状态，稳定停留后发布音乐与视频准备状态。</summary>
        public async Task SelectAsync(string title, Action<string> publish, int musicMs, int videoMs)
        {
            Cancel();
            int context = generation;
            pending = new CancellationTokenSource();
            var token = pending.Token;
            publish(title + " · 静态背景");
            try
            {
                await Task.Delay(musicMs, token);
                if (context != generation || token.IsCancellationRequested) return;
                publish(title + " · 音乐时序到达（未连接后端）");
                await Task.Delay(Math.Max(0, videoMs - musicMs), token);
                if (context == generation && !token.IsCancellationRequested)
                    publish(title + " · 视频准备时序到达（未连接后端）");
            }
            catch (OperationCanceledException) { /* 切换或离开时，旧请求正常终止。 */ }
        }

        /// <summary>失去前台或退出时令全部旧上下文失效。</summary>
        public void Cancel()
        {
            generation++;
            if (pending != null) { pending.Cancel(); pending.Dispose(); pending = null; }
        }

        /// <summary>释放待执行预览。</summary>
        public void Dispose() { Cancel(); }
    }
}

