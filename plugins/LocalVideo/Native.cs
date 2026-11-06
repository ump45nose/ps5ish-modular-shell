using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalVideo
{
    /// <summary>窄范围 LibVLC 3 C ABI 绑定；绝对路径装载，不修改进程 DLL 搜索目录。</summary>
    internal sealed class Native
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr New(int count, IntPtr args);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr One(IntPtr arg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr MediaNew(IntPtr instance, IntPtr path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Release(IntPtr arg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int State(IntPtr player);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate long TimeValue(IntPtr player);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SetTimeValue(IntPtr player, long time);
        [StructLayout(LayoutKind.Sequential)] internal struct TrackDescription { internal int Id; internal IntPtr Name, Next; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SetInt(IntPtr player, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Volume(IntPtr player, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr Lock(IntPtr opaque, IntPtr planes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Display(IntPtr opaque, IntPtr picture);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Callbacks(IntPtr player, Lock locked, Unlock unlocked, Display display, IntPtr opaque);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Format(IntPtr player, [MarshalAs(UnmanagedType.LPStr)] string chroma, uint width, uint height, uint pitch);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint FormatSetup(ref IntPtr opaque,IntPtr chroma,ref uint width,ref uint height,IntPtr pitches,IntPtr lines);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FormatCallbacks(IntPtr player,FormatSetup setup,Release cleanup);
        [StructLayout(LayoutKind.Sequential)] internal struct MediaTrack {internal uint Codec,Original;internal int Id,Type,Profile,Level;internal IntPtr Detail;}
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint TracksGet(IntPtr media,out IntPtr tracks);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TracksRelease(IntPtr tracks,uint count);
        internal One GetMedia;
        internal TracksGet GetMediaTracks;
        internal TracksRelease ReleaseMediaTracks;

        private readonly IntPtr library;
        internal New Create;
        internal Release RetainInstance, ReleaseInstance, ReleaseMedia, ReleasePlayer, Stop;
        internal MediaNew CreateMedia, CreateLocation;
        internal TimeValue GetTime;
        internal SetTimeValue SetTime;
        internal State IsSeekable;
        internal One GetAudioTracks, GetSubtitleTracks;
        internal Release ReleaseTracks;
        internal Volume SetAudioTrack, SetSubtitleTrack;
        internal One CreatePlayer;
        internal State Play, GetState;
        internal SetInt Pause;
        internal Volume SetVolume;
        internal Callbacks SetCallbacks;
        internal Format SetFormat;
        internal FormatCallbacks SetFormatCallbacks;

        /// <summary>按宿主进程位数加载匹配运行时；函数委托持有模块直到进程退出。</summary>
        internal Native(string root)
        {
            // Playnite 全屏当前为 x86，独立验证程序可能为 x64，不能按操作系统位数选择。
            if (IntPtr.Size == 4) root = Path.Combine(root, "x86");
            library = LoadLibraryEx(Path.Combine(root, "libvlc.dll"), IntPtr.Zero, 0x1100);
            if (library == IntPtr.Zero) throw new InvalidOperationException("LibVLC 加载失败：" + Marshal.GetLastWin32Error());
            Create = Get<New>("libvlc_new"); ReleaseInstance = Get<Release>("libvlc_release");
            RetainInstance = Get<Release>("libvlc_retain");
            CreateMedia = Get<MediaNew>("libvlc_media_new_path"); ReleaseMedia = Get<Release>("libvlc_media_release");
            CreatePlayer = Get<One>("libvlc_media_player_new_from_media"); ReleasePlayer = Get<Release>("libvlc_media_player_release");
            Stop = Get<Release>("libvlc_media_player_stop"); Play = Get<State>("libvlc_media_player_play"); GetState = Get<State>("libvlc_media_player_get_state");
            Pause = Get<SetInt>("libvlc_media_player_set_pause"); SetVolume = Get<Volume>("libvlc_audio_set_volume");
            SetCallbacks = Get<Callbacks>("libvlc_video_set_callbacks"); SetFormat = Get<Format>("libvlc_video_set_format");
            SetFormatCallbacks=Get<FormatCallbacks>("libvlc_video_set_format_callbacks");
            GetMedia=Get<One>("libvlc_media_player_get_media");GetMediaTracks=Get<TracksGet>("libvlc_media_tracks_get");ReleaseMediaTracks=Get<TracksRelease>("libvlc_media_tracks_release");
            CreateLocation=Get<MediaNew>("libvlc_media_new_location"); GetTime=Get<TimeValue>("libvlc_media_player_get_time"); SetTime=Get<SetTimeValue>("libvlc_media_player_set_time");
            IsSeekable=Get<State>("libvlc_media_player_is_seekable"); GetAudioTracks=Get<One>("libvlc_audio_get_track_description"); GetSubtitleTracks=Get<One>("libvlc_video_get_spu_description");
            ReleaseTracks=Get<Release>("libvlc_track_description_list_release"); SetAudioTrack=Get<Volume>("libvlc_audio_set_track"); SetSubtitleTrack=Get<Volume>("libvlc_video_set_spu");
        }

        /// <summary>按官方导出符号创建 Cdecl 委托，缺失 API 时拒绝运行。</summary>
        private T Get<T>(string name) where T : class
        {
            var address = GetProcAddress(library, name);
            if (address == IntPtr.Zero) throw new MissingMethodException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        /// <summary>生成 UTF-8 零终止路径，支持中文文件名。</summary>
        internal static IntPtr Utf8(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, pointer, bytes.Length); return pointer;
        }

        /// <summary>创建无界面、无网络发现、软件解码的预览实例。</summary>
        internal IntPtr CreateInstance()
        {
            string[] options = { "--quiet", "--no-video-title-show", "--no-osd", "--no-sub-autodetect-file", "--avcodec-hw=none" };
            IntPtr array = Marshal.AllocHGlobal(options.Length * IntPtr.Size);
            var pointers = new IntPtr[options.Length];
            try
            {
                for (int i = 0; i < options.Length; i++) { pointers[i] = Utf8(options[i]); Marshal.WriteIntPtr(array, i * IntPtr.Size, pointers[i]); }
                return Create(options.Length, array);
            }
            finally { foreach (var pointer in pointers) if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(array); }
        }
    }
}
